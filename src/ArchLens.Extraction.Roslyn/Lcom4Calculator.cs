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

        var methods = CollectInstanceMethods(type, includeGenerated);
        if (methods.Count <= 1)
        {
            return methods.Count;
        }

        var methodIndices = BuildMethodIndexMap(methods);
        var instanceState = CollectInstanceState(type);
        var propertyUnderlyingState = CollectPropertyBackingLinks(type, instanceState, getSemanticModel, includeGenerated);

        var dsu = new DisjointSet(methods.Count);
        var stateToMethods = new Dictionary<ISymbol, List<int>>(SymbolEqualityComparer.Default);

        for (var methodIdx = 0; methodIdx < methods.Count; methodIdx++)
        {
            AnalyzeMethodReferences(
                type, methods[methodIdx], methodIdx, methodIndices,
                instanceState, propertyUnderlyingState, getSemanticModel, dsu, stateToMethods, includeGenerated);
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

    private static List<IMethodSymbol> CollectInstanceMethods(INamedTypeSymbol type, bool includeGenerated)
    {
        var result = new List<IMethodSymbol>();
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

            result.Add(impl);
        }
        return result;
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

    private static HashSet<ISymbol> CollectInstanceState(INamedTypeSymbol type)
    {
        var state = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        // Walk the type and its base types (excluding System.Object / System.ValueType)
        for (var current = type; current is not null && current.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType); current = current.BaseType)
        {
            var isSelf = SymbolEqualityComparer.Default.Equals(current, type);
            foreach (var member in current.GetMembers())
            {
                if (!isSelf && member.DeclaredAccessibility == Accessibility.Private)
                {
                    continue;
                }

                if (member is IFieldSymbol { IsStatic: false, IsConst: false } field)
                {
                    state.Add(field.OriginalDefinition);
                }
                else if (member is IPropertySymbol { IsStatic: false } prop)
                {
                    state.Add(prop.OriginalDefinition);
                }
                else if (member is IEventSymbol { IsStatic: false } ev)
                {
                    state.Add(ev.OriginalDefinition);
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
                }
            }
        }

        return state;
    }

    private static Dictionary<ISymbol, List<ISymbol>> CollectPropertyBackingLinks(
        INamedTypeSymbol type,
        HashSet<ISymbol> instanceState,
        Func<SyntaxTree, SemanticModel> getSemanticModel,
        bool includeGenerated)
    {
        var links = new Dictionary<ISymbol, List<ISymbol>>(SymbolEqualityComparer.Default);

        foreach (var prop in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (prop.IsStatic || prop.DeclaringSyntaxReferences.Length == 0)
            {
                continue;
            }

            var propKey = prop.OriginalDefinition;
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
                    if (sym is not null && !SymbolEqualityComparer.Default.Equals(sym, propKey) && instanceState.Contains(sym))
                    {
                        if (!links.TryGetValue(propKey, out var list))
                        {
                            list = [];
                            links[propKey] = list;
                        }
                        list.Add(sym);
                    }
                }
            }
        }

        return links;
    }

    private static void AnalyzeMethodReferences(
        INamedTypeSymbol type,
        IMethodSymbol method,
        int methodIdx,
        Dictionary<ISymbol, int> methodIndices,
        HashSet<ISymbol> instanceState,
        Dictionary<ISymbol, List<ISymbol>> propertyUnderlyingState,
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
                var symbol = NormalizeSymbol(semanticModel.GetSymbolInfo(node).Symbol);
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

        var orig = symbol.OriginalDefinition;
        if (orig is IMethodSymbol { ReducedFrom: not null } reduced)
        {
            orig = reduced.ReducedFrom.OriginalDefinition;
        }

        return orig;
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
