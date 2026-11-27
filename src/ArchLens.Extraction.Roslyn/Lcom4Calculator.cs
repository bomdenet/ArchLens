using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchLens.Extraction.Roslyn;

/// <summary>
/// Computes LCOM4 (Lack of Cohesion of Methods 4, Hitz &amp; Montazeri 1995) for a type.
/// Connects instance methods that share instance state (fields, properties, events, primary ctor params,
/// inherited accessible state, or transitively backed properties) or directly call each other.
/// Values: 0 = no instance methods, 1 = cohesive, &gt;= 2 = candidate for splitting.
/// </summary>
public static class Lcom4Calculator
{
    public static int Calculate(INamedTypeSymbol type, Func<SyntaxTree, SemanticModel> getSemanticModel, bool includeGenerated = false)
    {
        if (type.IsStatic ||
            type.TypeKind is Microsoft.CodeAnalysis.TypeKind.Interface or Microsoft.CodeAnalysis.TypeKind.Enum or Microsoft.CodeAnalysis.TypeKind.Delegate)
        {
            return 0;
        }

        var (methods, totalDeclaredInstanceMethods) = CollectInstanceMethods(type, includeGenerated);
        if (methods.Count <= 1)
        {
            return methods.Count == 0 && totalDeclaredInstanceMethods > 0 ? 1 : methods.Count;
        }

        var (instanceState, hasStoredInstanceState) = CollectInstanceState(type);
        if (instanceState.Count == 0 || !hasStoredInstanceState)
        {
            // Stateless types (e.g. pure delegating adapters/services with no own or inherited stored state,
            // even if they expose constant/computed expression-bodied properties) have no disjoint state clusters to split.
            return 1;
        }

        var methodIndices = BuildMethodIndexMap(methods);
        var inheritedBaseMethods = CollectInheritedInstanceMethods(type);
        var (propertyUnderlyingState, propertyCalledMethods, fieldPrimaryCtorLinks) =
            CollectMemberLinks(type, instanceState, methodIndices, getSemanticModel, includeGenerated);

        var dsu = new DisjointSet(methods.Count);
        var stateToMethods = new Dictionary<ISymbol, List<int>>(SymbolEqualityComparer.Default);

        // Computed properties with hand-written bodies act as cohesive connectors between the state they read
        // and the instance methods they invoke.
        foreach (var (propSym, calledMethods) in propertyCalledMethods)
        {
            foreach (var calledIdx in calledMethods)
            {
                RecordStateAccess(stateToMethods, propSym, calledIdx);
                if (propertyUnderlyingState.TryGetValue(propSym, out var underlying))
                {
                    foreach (var backing in underlying)
                    {
                        RecordStateAccess(stateToMethods, backing, calledIdx);
                    }
                }
            }
        }

        for (var methodIdx = 0; methodIdx < methods.Count; methodIdx++)
        {
            AnalyzeMethodReferences(
                type, methods[methodIdx], methodIdx, methodIndices,
                instanceState, inheritedBaseMethods, propertyUnderlyingState, propertyCalledMethods,
                fieldPrimaryCtorLinks, getSemanticModel, dsu, stateToMethods, includeGenerated);
        }

        foreach (var accessors in stateToMethods.Values)
        {
            for (var i = 1; i < accessors.Count; i++)
            {
                dsu.Union(accessors[0], accessors[i]);
            }
        }

        return dsu.CountComponents();
    }

    private static (List<IMethodSymbol> NonTrivial, int TotalDeclared) CollectInstanceMethods(INamedTypeSymbol type, bool includeGenerated)
    {
        var result = new List<IMethodSymbol>();
        var totalDeclared = 0;
        foreach (var m in type.GetMembers().OfType<IMethodSymbol>())
        {
            if (m.MethodKind is not (MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation))
            {
                continue;
            }
            if (m.IsStatic || m.IsAbstract || m.IsImplicitlyDeclared)
            {
                continue;
            }

            var impl = m.PartialImplementationPart ?? m;
            if (impl.DeclaringSyntaxReferences.Length == 0)
            {
                continue;
            }

            if (!includeGenerated &&
                impl.DeclaringSyntaxReferences.All(r =>
                    GeneratedCodeClassifier.IsGeneratedSyntaxTree(r.SyntaxTree) ||
                    GeneratedCodeClassifier.IsGeneratedSyntaxNode(r.GetSyntax())))
            {
                continue;
            }

            totalDeclared++;
            if (IsTrivialNoOpMethod(impl))
            {
                continue;
            }

            result.Add(impl);
        }
        return (result, totalDeclared);
    }

    public static int CalculateWithRazorCodeBlock(
        INamedTypeSymbol type,
        Func<SyntaxTree, SemanticModel> getSemanticModel,
        bool includeGenerated,
        IReadOnlyList<(string Name, IReadOnlySet<string> Uses)> razorMethods,
        IReadOnlySet<string> razorFieldsAndProps)
    {
        if (razorMethods.Count == 0 && razorFieldsAndProps.Count == 0)
        {
            return Calculate(type, getSemanticModel, includeGenerated);
        }

        if (type.IsStatic ||
            type.TypeKind is Microsoft.CodeAnalysis.TypeKind.Interface or Microsoft.CodeAnalysis.TypeKind.Enum or Microsoft.CodeAnalysis.TypeKind.Delegate)
        {
            return 0;
        }

        var (csharpMethods, totalDeclaredCsharp) = CollectInstanceMethods(type, includeGenerated);
        var csharpMethodNames = csharpMethods.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);
        var extraRazorMethods = razorMethods
            .Where(rm => !csharpMethodNames.Contains(rm.Name))
            .ToList();

        var totalMethodCount = csharpMethods.Count + extraRazorMethods.Count;
        if (totalMethodCount <= 1)
        {
            return totalMethodCount == 0 && totalDeclaredCsharp > 0 ? 1 : totalMethodCount;
        }

        var (instanceState, hasStoredInstanceState) = CollectInstanceState(type);
        var allStateNames = new HashSet<string>(razorFieldsAndProps, StringComparer.Ordinal);
        foreach (var s in instanceState)
        {
            allStateNames.Add(s.Name);
        }

        var hasAnyStoredState = hasStoredInstanceState || razorFieldsAndProps.Count > 0;
        if (allStateNames.Count == 0 || !hasAnyStoredState)
        {
            return 1;
        }

        var dsu = new DisjointSet(totalMethodCount);
        var stateNameToMethods = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var methodNameToIndices = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        void AddMethodName(string name, int idx)
        {
            if (!methodNameToIndices.TryGetValue(name, out var list))
            {
                list = [];
                methodNameToIndices[name] = list;
            }
            list.Add(idx);
        }

        void AddStateUse(string stateName, int idx)
        {
            if (!stateNameToMethods.TryGetValue(stateName, out var list))
            {
                list = [];
                stateNameToMethods[stateName] = list;
            }
            list.Add(idx);
        }

        for (var i = 0; i < csharpMethods.Count; i++)
        {
            AddMethodName(csharpMethods[i].Name, i);
        }
        for (var i = 0; i < extraRazorMethods.Count; i++)
        {
            AddMethodName(extraRazorMethods[i].Name, csharpMethods.Count + i);
        }

        // First run exact Roslyn analysis for C# partial methods
        var methodIndices = BuildMethodIndexMap(csharpMethods);
        var inheritedBaseMethods = CollectInheritedInstanceMethods(type);
        var (propertyUnderlyingState, propertyCalledMethods, fieldPrimaryCtorLinks) =
            CollectMemberLinks(type, instanceState, methodIndices, getSemanticModel, includeGenerated);
        var stateToMethods = new Dictionary<ISymbol, List<int>>(SymbolEqualityComparer.Default);
        foreach (var (propSym, calledMethods) in propertyCalledMethods)
        {
            foreach (var calledIdx in calledMethods)
            {
                RecordStateAccess(stateToMethods, propSym, calledIdx);
                if (propertyUnderlyingState.TryGetValue(propSym, out var underlying))
                {
                    foreach (var backing in underlying)
                    {
                        RecordStateAccess(stateToMethods, backing, calledIdx);
                    }
                }
            }
        }

        for (var methodIdx = 0; methodIdx < csharpMethods.Count; methodIdx++)
        {
            AnalyzeMethodReferences(
                type, csharpMethods[methodIdx], methodIdx, methodIndices,
                instanceState, inheritedBaseMethods, propertyUnderlyingState, propertyCalledMethods,
                fieldPrimaryCtorLinks, getSemanticModel, dsu, stateToMethods, includeGenerated);

            // Also capture syntactic references from C# partial methods into @code fields/methods
            foreach (var syntaxRef in csharpMethods[methodIdx].DeclaringSyntaxReferences)
            {
                if (!includeGenerated && GeneratedCodeClassifier.IsGeneratedSyntaxTree(syntaxRef.SyntaxTree)) continue;
                var syntax = syntaxRef.GetSyntax();
                foreach (var id in syntax.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    var text = id.Identifier.ValueText;
                    if (allStateNames.Contains(text))
                    {
                        AddStateUse(text, methodIdx);
                    }
                    if (methodNameToIndices.TryGetValue(text, out var targets))
                    {
                        foreach (var t in targets) dsu.Union(methodIdx, t);
                    }
                }
            }
        }

        foreach (var (sym, accessors) in stateToMethods)
        {
            for (var i = 1; i < accessors.Count; i++)
            {
                dsu.Union(accessors[0], accessors[i]);
            }
            foreach (var accIdx in accessors)
            {
                AddStateUse(sym.Name, accIdx);
            }
        }

        // Now connect Razor @code methods via shared state names and direct calls
        for (var i = 0; i < extraRazorMethods.Count; i++)
        {
            var idx = csharpMethods.Count + i;
            foreach (var u in extraRazorMethods[i].Uses)
            {
                if (allStateNames.Contains(u))
                {
                    AddStateUse(u, idx);
                }
                if (methodNameToIndices.TryGetValue(u, out var targets))
                {
                    foreach (var t in targets) dsu.Union(idx, t);
                }
            }
        }

        foreach (var accessors in stateNameToMethods.Values)
        {
            for (var i = 1; i < accessors.Count; i++)
            {
                dsu.Union(accessors[0], accessors[i]);
            }
        }

        return dsu.CountComponents();
    }

    private static bool IsTrivialNoOpMethod(IMethodSymbol method)
    {
        if (method.DeclaringSyntaxReferences.Length != 1)
        {
            return false;
        }

        if (method.DeclaringSyntaxReferences[0].GetSyntax() is not MethodDeclarationSyntax decl)
        {
            return false;
        }

        return IsTrivialNoOpSyntax(decl);
    }

    internal static bool IsTrivialNoOpSyntax(MethodDeclarationSyntax decl)
    {
        if (decl.Body is not null && decl.ExpressionBody is null &&
            decl.Body.Statements.All(s => s is EmptyStatementSyntax))
        {
            return true;
        }

        ExpressionSyntax? returnedExpr = decl.ExpressionBody?.Expression;
        if (returnedExpr is null && decl.Body is { Statements.Count: 1 })
        {
            if (decl.Body.Statements[0] is ReturnStatementSyntax ret)
            {
                if (ret.Expression is null) return true;
                returnedExpr = ret.Expression;
            }
        }

        if (returnedExpr is not null)
        {
            var text = returnedExpr.ToString().Trim();
            if (text is "Task.CompletedTask" or "System.Threading.Tasks.Task.CompletedTask"
                or "ValueTask.CompletedTask" or "System.Threading.Tasks.ValueTask.CompletedTask"
                or "default" or "null" or "true" or "false" or "\"\"" or "string.Empty"
                || text.StartsWith("default(", StringComparison.Ordinal))
            {
                return true;
            }

            if ((text.EndsWith(".Instance", StringComparison.Ordinal) || text.EndsWith(".Empty", StringComparison.Ordinal)) &&
                returnedExpr is MemberAccessExpressionSyntax ma &&
                ma.Expression is not ThisExpressionSyntax &&
                ma.Expression is not BaseExpressionSyntax)
            {
                var recvText = ma.Expression.ToString().Trim();
                // Only treat static type singletons/sentinels (e.g. Guid.Empty, ImmutableArray<int>.Empty, Foo.Instance)
                // as trivial no-ops — never instance fields/properties like `this.Empty` or `_holder.Empty`.
                if (recvText.Length > 0 && char.IsUpper(recvText[0]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static Dictionary<ISymbol, int> BuildMethodIndexMap(List<IMethodSymbol> methods)
    {
        var map = new Dictionary<ISymbol, int>(SymbolEqualityComparer.Default);
        for (var i = 0; i < methods.Count; i++)
        {
            map[methods[i].OriginalDefinition] = i;
            if (methods[i].PartialDefinitionPart is { } defPart)
            {
                map[defPart.OriginalDefinition] = i;
            }
        }
        return map;
    }

    private static (HashSet<ISymbol> State, bool HasStoredState) CollectInstanceState(INamedTypeSymbol type)
    {
        var state = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        var hasStoredState = false;

        // Walk the type and its base types (excluding System.Object / System.ValueType)
        for (var current = type; current is not null && current.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType); current = current.BaseType)
        {
            var isSelf = SymbolEqualityComparer.Default.Equals(current, type);
            var backingFieldProps = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            foreach (var f in current.GetMembers().OfType<IFieldSymbol>())
            {
                if (!f.IsStatic && !f.IsConst && f.AssociatedSymbol is IPropertySymbol assocProp)
                {
                    backingFieldProps.Add(assocProp.OriginalDefinition);
                }
            }

            foreach (var member in current.GetMembers())
            {
                if (!isSelf && member.DeclaredAccessibility == Accessibility.Private)
                {
                    continue;
                }

                if (member is IFieldSymbol { IsStatic: false, IsConst: false } field)
                {
                    state.Add(NormalizeSymbol(field)!);
                    if (isSelf || field.DeclaringSyntaxReferences.Length > 0)
                    {
                        hasStoredState = true;
                    }
                }
                else if (member is IPropertySymbol { IsStatic: false } prop)
                {
                    state.Add(NormalizeSymbol(prop)!);
                    if (backingFieldProps.Contains(prop.OriginalDefinition))
                    {
                        hasStoredState = true;
                    }
                }
                else if (member is IEventSymbol { IsStatic: false } ev)
                {
                    state.Add(NormalizeSymbol(ev)!);
                    if (isSelf || ev.DeclaringSyntaxReferences.Length > 0)
                    {
                        hasStoredState = true;
                    }
                }
            }
        }

        // Include C# 12+ primary constructor parameters captured as instance state
        foreach (var ctor in type.InstanceConstructors)
        {
            if (ctor.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax))
            {
                foreach (var param in ctor.Parameters)
                {
                    state.Add(param.OriginalDefinition);
                    hasStoredState = true;
                }
            }
        }

        return (state, hasStoredState);
    }

    private static HashSet<ISymbol> CollectInheritedInstanceMethods(INamedTypeSymbol type)
    {
        var baseMethods = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        for (var current = type.BaseType;
             current is not null && current.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType);
             current = current.BaseType)
        {
            foreach (var member in current.GetMembers().OfType<IMethodSymbol>())
            {
                if (member.IsStatic || member.DeclaredAccessibility == Accessibility.Private ||
                    member.MethodKind != MethodKind.Ordinary)
                {
                    continue;
                }

                baseMethods.Add(NormalizeSymbol(member)!);
            }
        }

        return baseMethods;
    }

    private static (
        Dictionary<ISymbol, List<ISymbol>> PropertyUnderlyingState,
        Dictionary<ISymbol, List<int>> PropertyCalledMethods,
        Dictionary<ISymbol, List<ISymbol>> FieldPrimaryCtorLinks)
        CollectMemberLinks(
        INamedTypeSymbol type,
        HashSet<ISymbol> instanceState,
        Dictionary<ISymbol, int> methodIndices,
        Func<SyntaxTree, SemanticModel> getSemanticModel,
        bool includeGenerated)
    {
        var stateLinks = new Dictionary<ISymbol, List<ISymbol>>(SymbolEqualityComparer.Default);
        var methodLinks = new Dictionary<ISymbol, List<int>>(SymbolEqualityComparer.Default);
        var fieldCtorLinks = new Dictionary<ISymbol, List<ISymbol>>(SymbolEqualityComparer.Default);

        foreach (var prop in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (prop.IsStatic || prop.DeclaringSyntaxReferences.Length == 0)
            {
                continue;
            }

            var propKey = NormalizeSymbol(prop)!;
            foreach (var syntaxRef in prop.DeclaringSyntaxReferences)
            {
                if (!includeGenerated && GeneratedCodeClassifier.IsGeneratedSyntaxTree(syntaxRef.SyntaxTree))
                {
                    continue;
                }

                var syntax = syntaxRef.GetSyntax();
                var semanticModel = getSemanticModel(syntaxRef.SyntaxTree);
                foreach (var node in EnumerateIdentifierNodesSkippingNameof(syntax))
                {
                    var sym = NormalizeSymbol(semanticModel.GetSymbolInfo(node).Symbol);
                    if (sym is null)
                    {
                        continue;
                    }

                    if (methodIndices.TryGetValue(sym, out var calledMethodIdx))
                    {
                        if (!methodLinks.TryGetValue(propKey, out var mList))
                        {
                            mList = [];
                            methodLinks[propKey] = mList;
                        }
                        if (!mList.Contains(calledMethodIdx))
                        {
                            mList.Add(calledMethodIdx);
                        }
                    }
                    else if (!SymbolEqualityComparer.Default.Equals(sym, propKey) && instanceState.Contains(sym))
                    {
                        if (!stateLinks.TryGetValue(propKey, out var sList))
                        {
                            sList = [];
                            stateLinks[propKey] = sList;
                        }
                        sList.Add(sym);
                    }
                }
            }
        }

        // Link instance fields initialized directly from primary constructor parameters so methods using
        // the field and methods using the captured parameter share the same state cluster.
        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsConst || field.IsImplicitlyDeclared || field.DeclaringSyntaxReferences.Length == 0)
            {
                continue;
            }

            var fieldKey = NormalizeSymbol(field)!;
            foreach (var syntaxRef in field.DeclaringSyntaxReferences)
            {
                if (!includeGenerated && GeneratedCodeClassifier.IsGeneratedSyntaxTree(syntaxRef.SyntaxTree))
                {
                    continue;
                }

                if (syntaxRef.GetSyntax() is VariableDeclaratorSyntax { Initializer: not null } declarator)
                {
                    var semanticModel = getSemanticModel(syntaxRef.SyntaxTree);
                    foreach (var node in EnumerateIdentifierNodesSkippingNameof(declarator.Initializer))
                    {
                        var sym = NormalizeSymbol(semanticModel.GetSymbolInfo(node).Symbol);
                        if (sym is IParameterSymbol && instanceState.Contains(sym))
                        {
                            if (!fieldCtorLinks.TryGetValue(fieldKey, out var list))
                            {
                                list = [];
                                fieldCtorLinks[fieldKey] = list;
                            }
                            list.Add(sym);
                        }
                    }
                }
            }
        }

        return (stateLinks, methodLinks, fieldCtorLinks);
    }

    private static void AnalyzeMethodReferences(
        INamedTypeSymbol type,
        IMethodSymbol method,
        int methodIdx,
        Dictionary<ISymbol, int> methodIndices,
        HashSet<ISymbol> instanceState,
        HashSet<ISymbol> inheritedBaseMethods,
        Dictionary<ISymbol, List<ISymbol>> propertyUnderlyingState,
        Dictionary<ISymbol, List<int>> propertyCalledMethods,
        Dictionary<ISymbol, List<ISymbol>> fieldPrimaryCtorLinks,
        Func<SyntaxTree, SemanticModel> getSemanticModel,
        DisjointSet dsu,
        Dictionary<ISymbol, List<int>> stateToMethods,
        bool includeGenerated)
    {
        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            if (!includeGenerated && GeneratedCodeClassifier.IsGeneratedSyntaxTree(syntaxRef.SyntaxTree))
            {
                continue;
            }

            var syntax = syntaxRef.GetSyntax();
            var semanticModel = getSemanticModel(syntaxRef.SyntaxTree);

            foreach (var node in EnumerateIdentifierNodesSkippingNameof(syntax))
            {
                var rawSymbol = semanticModel.GetSymbolInfo(node).Symbol;
                if (rawSymbol is IMethodSymbol rawMethod)
                {
                    var directOrig = (rawMethod.ReducedFrom ?? rawMethod).OriginalDefinition;
                    if (methodIndices.TryGetValue(directOrig, out var directCalledIdx))
                    {
                        if (directCalledIdx != methodIdx)
                        {
                            dsu.Union(methodIdx, directCalledIdx);
                        }
                        continue;
                    }
                }

                var symbol = NormalizeSymbol(rawSymbol);
                if (symbol is null)
                {
                    continue;
                }

                // Map interface calls on 'this' to explicit interface implementations if present
                if (symbol is IMethodSymbol { ContainingType.TypeKind: Microsoft.CodeAnalysis.TypeKind.Interface } ifaceMethod)
                {
                    var impl = type.FindImplementationForInterfaceMember(ifaceMethod);
                    if (impl is not null)
                    {
                        symbol = impl.OriginalDefinition;
                    }
                }

                if (methodIndices.TryGetValue(symbol, out var calledIdx) && calledIdx != methodIdx)
                {
                    dsu.Union(methodIdx, calledIdx);
                }
                else if (instanceState.Contains(symbol))
                {
                    RecordStateAccess(stateToMethods, symbol, methodIdx);
                    if (propertyUnderlyingState.TryGetValue(symbol, out var underlying))
                    {
                        foreach (var backing in underlying)
                        {
                            RecordStateAccess(stateToMethods, backing, methodIdx);
                        }
                    }
                    if (propertyCalledMethods.TryGetValue(symbol, out var propMethods))
                    {
                        foreach (var propMethodIdx in propMethods)
                        {
                            dsu.Union(methodIdx, propMethodIdx);
                        }
                    }
                    if (fieldPrimaryCtorLinks.TryGetValue(symbol, out var ctorParams))
                    {
                        foreach (var paramSym in ctorParams)
                        {
                            RecordStateAccess(stateToMethods, paramSym, methodIdx);
                        }
                    }
                }
                else if (inheritedBaseMethods.Contains(symbol))
                {
                    RecordStateAccess(stateToMethods, symbol, methodIdx);
                }
            }
        }
    }

    private static ISymbol? NormalizeSymbol(ISymbol? symbol)
    {
        if (symbol is null)
        {
            return null;
        }

        if (symbol is IFieldSymbol { AssociatedSymbol: IPropertySymbol assocProp })
        {
            symbol = assocProp;
        }

        if (symbol is IPropertySymbol prop)
        {
            while (prop.OverriddenProperty is not null)
            {
                prop = prop.OverriddenProperty;
            }
            symbol = prop;
        }
        else if (symbol is IMethodSymbol m)
        {
            if (m.ReducedFrom is not null)
            {
                m = m.ReducedFrom;
            }
            while (m.OverriddenMethod is not null)
            {
                m = m.OverriddenMethod;
            }
            symbol = m;
        }

        return symbol.OriginalDefinition;
    }

    private static void RecordStateAccess(Dictionary<ISymbol, List<int>> map, ISymbol stateSymbol, int methodIdx)
    {
        if (!map.TryGetValue(stateSymbol, out var list))
        {
            list = [];
            map[stateSymbol] = list;
        }
        list.Add(methodIdx);
    }

    private static IEnumerable<SyntaxNode> EnumerateIdentifierNodesSkippingNameof(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes(descendIntoChildren: n => !IsNameofInvocation(n)))
        {
            if (node is IdentifierNameSyntax or GenericNameSyntax)
            {
                yield return node;
            }
        }
    }

    private static bool IsNameofInvocation(SyntaxNode node) =>
        node is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } };

    private sealed class DisjointSet(int size)
    {
        private readonly int[] _parent = Enumerable.Range(0, size).ToArray();

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];
                x = _parent[x];
            }
            return x;
        }

        public void Union(int a, int b)
        {
            var ra = Find(a);
            var rb = Find(b);
            if (ra != rb)
            {
                _parent[ra] = rb;
            }
        }

        public int CountComponents()
        {
            var roots = new HashSet<int>();
            for (var i = 0; i < _parent.Length; i++)
            {
                roots.Add(Find(i));
            }
            return roots.Count;
        }
    }
}
