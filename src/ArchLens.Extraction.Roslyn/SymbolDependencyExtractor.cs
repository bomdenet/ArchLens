using ArchLens.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchLens.Extraction.Roslyn;

/// <summary>
/// Extracts semantic type-level and method-level dependencies and syntax complexity stats from Roslyn symbols.
/// </summary>
internal static class SymbolDependencyExtractor
{
    private static readonly SymbolDisplayFormat TypeIdFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);

    private static readonly SymbolDisplayFormat ConstructedTypeIdFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static readonly SymbolDisplayFormat MethodIdFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
        parameterOptions: SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeType,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    public static string GetTypeId(INamedTypeSymbol type)
    {
        var original = type.OriginalDefinition;
        var baseId = original.ToDisplayString(TypeIdFormat);
        var fileTag = GetFileLocalTag(original);
        return fileTag is null ? baseId : $"{baseId}@{fileTag}";
    }

    /// <summary>
    /// Formats a type symbol preserving closed generic arguments (e.g. IRepository&lt;Order&gt; vs IRepository&lt;Config&gt;),
    /// while open/unbound generics format via OriginalDefinition (e.g. IRepository&lt;T&gt;).
    /// </summary>
    public static string GetConstructedTypeId(INamedTypeSymbol type)
    {
        if (type.IsUnboundGenericType || type.IsDefinition)
        {
            return GetTypeId(type);
        }

        var display = type.ToDisplayString(ConstructedTypeIdFormat);
        var fileTag = GetFileLocalTag(type.OriginalDefinition);
        return fileTag is null ? display : $"{display}@{fileTag}";
    }

    public static string GetMethodId(IMethodSymbol method)
    {
        var original = method.ReducedFrom?.OriginalDefinition ?? method.OriginalDefinition;
        if (original.MethodKind == MethodKind.StaticConstructor && original.ContainingType is not null)
        {
            return $"{GetTypeId(original.ContainingType)}.cctor()";
        }

        var raw = original.ToDisplayString(MethodIdFormat);
        if (original.ContainingType is { } ct && GetFileLocalTag(ct.OriginalDefinition) is { } fileTag)
        {
            var rawOwner = ct.OriginalDefinition.ToDisplayString(TypeIdFormat);
            if (raw.StartsWith(rawOwner + ".", StringComparison.Ordinal))
            {
                return $"{rawOwner}@{fileTag}{raw[rawOwner.Length..]}";
            }
        }
        return raw;
    }

    private static string? GetFileLocalTag(INamedTypeSymbol type)
    {
        for (var cur = type; cur is not null; cur = cur.ContainingType)
        {
            if (cur.IsFileLocal)
            {
                var fp = cur.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree.FilePath;
                if (!string.IsNullOrWhiteSpace(fp))
                {
                    var name = Path.GetFileNameWithoutExtension(fp);
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(fp).ToLowerInvariant())))[..6];
                    return $"{name}:{hash}";
                }
                return "file";
            }
        }
        return null;
    }

    public static IMethodSymbol ResolveImplementation(IMethodSymbol method) =>
        method.PartialImplementationPart ?? method;

    public static bool IsTrackableMethod(IMethodSymbol method)
    {
        if (method.IsImplicitlyDeclared)
        {
            return false;
        }

        if (method.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.ExplicitInterfaceImplementation or MethodKind.UserDefinedOperator or MethodKind.Conversion))
        {
            return false;
        }

        var impl = ResolveImplementation(method);
        return impl.DeclaringSyntaxReferences.Length > 0;
    }

    public static (int LineCount, int TotalCyclomatic, int MaxMethodCyclomatic, int TotalCognitive, int MaxMethodCognitive) ComputeTypeSyntaxStats(
        INamedTypeSymbol type,
        bool includeGenerated = false,
        Func<SyntaxTree, SemanticModel>? getSemanticModel = null)
    {
        var totalLines = 0;
        var totalCyclomatic = 0;
        var maxCyclomatic = 0;
        var totalCognitive = 0;
        var maxCognitive = 0;

        foreach (var syntaxRef in type.DeclaringSyntaxReferences)
        {
            if (!includeGenerated && GeneratedCodeClassifier.IsGeneratedSyntaxTree(syntaxRef.SyntaxTree))
            {
                continue;
            }

            var syntax = syntaxRef.GetSyntax();
            if (!includeGenerated && GeneratedCodeClassifier.IsGeneratedSyntaxNode(syntax))
            {
                continue;
            }

            var span = syntax.GetLocation().GetLineSpan();
            var endLine = span.EndLinePosition.Line;
            if (syntax is CompilationUnitSyntax && span.EndLinePosition.Character == 0 && endLine > span.StartLinePosition.Line)
            {
                endLine--;
            }
            totalLines += Math.Max(1, endLine - span.StartLinePosition.Line + 1);

            if (syntax is CompilationUnitSyntax compilationUnit)
            {
                // Top-level statements live in GlobalStatementSyntax nodes rather than a method declaration.
                var globalStatements = compilationUnit.Members.OfType<GlobalStatementSyntax>().ToList();
                if (globalStatements.Count > 0)
                {
                    var topLevelCc = 1;
                    var topLevelCog = 0;
                    foreach (var gs in globalStatements)
                    {
                        if (!includeGenerated && GeneratedCodeClassifier.IsGeneratedSyntaxNode(gs)) continue;
                        topLevelCc += CyclomaticComplexityWalker.Calculate(gs) - 1;
                        topLevelCog += CognitiveComplexityWalker.Calculate(gs);
                    }

                    totalCyclomatic += topLevelCc;
                    totalCognitive += topLevelCog;
                    if (topLevelCc > maxCyclomatic) maxCyclomatic = topLevelCc;
                    if (topLevelCog > maxCognitive) maxCognitive = topLevelCog;
                }
                continue;
            }

            // Do not descend into nested type declarations so their complexity is not double-counted.
            // Skip bodyless auto-property accessors (get; set; / get; init;) so plain DTOs/records are not inflated.
            var executables = syntax
                .DescendantNodes(descendIntoChildren: n => ReferenceEquals(n, syntax) || n is not BaseTypeDeclarationSyntax)
                .Where(n => n is BaseMethodDeclarationSyntax
                         or AccessorDeclarationSyntax { Body: not null }
                         or AccessorDeclarationSyntax { ExpressionBody: not null }
                         or PropertyDeclarationSyntax { ExpressionBody: not null }
                         or IndexerDeclarationSyntax { ExpressionBody: not null })
                .ToList();

            var semModel = getSemanticModel?.Invoke(syntaxRef.SyntaxTree);
            foreach (var execNode in executables)
            {
                if (!includeGenerated && GeneratedCodeClassifier.IsGeneratedSyntaxNode(execNode))
                {
                    continue;
                }

                var target = execNode switch
                {
                    PropertyDeclarationSyntax prop => (SyntaxNode)prop.ExpressionBody!,
                    IndexerDeclarationSyntax idx => idx.ExpressionBody!,
                    _ => execNode
                };
                var cc = CyclomaticComplexityWalker.Calculate(target);
                var cog = CognitiveComplexityWalker.Calculate(target, semanticModel: semModel);
                totalCyclomatic += cc;
                totalCognitive += cog;
                if (cc > maxCyclomatic) maxCyclomatic = cc;
                if (cog > maxCognitive) maxCognitive = cog;
            }
        }

        return (totalLines, totalCyclomatic, maxCyclomatic, totalCognitive, maxCognitive);
    }

    public static (int LineCount, int Cyclomatic, int Cognitive) ComputeMethodSyntaxStats(
        IMethodSymbol method,
        bool includeGenerated = false,
        Func<SyntaxTree, SemanticModel>? getSemanticModel = null)
    {
        var impl = ResolveImplementation(method);
        var lines = 0;
        var cc = 0;
        var cog = 0;

        foreach (var syntaxRef in impl.DeclaringSyntaxReferences)
        {
            if (!includeGenerated && GeneratedCodeClassifier.IsGeneratedSyntaxTree(syntaxRef.SyntaxTree))
            {
                continue;
            }

            var syntax = syntaxRef.GetSyntax();
            if (!includeGenerated && GeneratedCodeClassifier.IsGeneratedSyntaxNode(syntax))
            {
                continue;
            }

            var semModel = getSemanticModel?.Invoke(syntaxRef.SyntaxTree);
            if (syntax is TypeDeclarationSyntax typeDecl)
            {
                // C# 12+ primary constructor: only count parameter list + base args + member initializers
                if (typeDecl.ParameterList is not null)
                {
                    var pSpan = typeDecl.ParameterList.GetLocation().GetLineSpan();
                    lines += Math.Max(1, pSpan.EndLinePosition.Line - pSpan.StartLinePosition.Line + 1);
                }

                var initNodes = GetPrimaryConstructorNodes(typeDecl).ToList();
                var initCc = 1;
                foreach (var node in initNodes)
                {
                    initCc += CyclomaticComplexityWalker.Calculate(node) - 1;
                    cog += CognitiveComplexityWalker.Calculate(node, semanticModel: semModel);
                }
                cc += Math.Max(1, initCc);
            }
            else if (syntax is CompilationUnitSyntax compilationUnit)
            {
                var globalStatements = compilationUnit.Members.OfType<GlobalStatementSyntax>().ToList();
                if (globalStatements.Count > 0)
                {
                    var firstSpan = globalStatements[0].GetLocation().GetLineSpan();
                    var lastSpan = globalStatements[^1].GetLocation().GetLineSpan();
                    lines += Math.Max(1, lastSpan.EndLinePosition.Line - firstSpan.StartLinePosition.Line + 1);
                    var topLevelCc = 1;
                    foreach (var gs in globalStatements)
                    {
                        topLevelCc += CyclomaticComplexityWalker.Calculate(gs) - 1;
                        cog += CognitiveComplexityWalker.Calculate(gs, semanticModel: semModel);
                    }
                    cc += Math.Max(1, topLevelCc);
                }
            }
            else
            {
                var span = syntax.GetLocation().GetLineSpan();
                lines += Math.Max(1, span.EndLinePosition.Line - span.StartLinePosition.Line + 1);
                cc += CyclomaticComplexityWalker.Calculate(syntax);
                cog += CognitiveComplexityWalker.Calculate(syntax, impl.Name, impl.Parameters.Length, semModel, impl);
            }
        }

        return (Math.Max(1, lines), Math.Max(1, cc), cog);
    }

    [ThreadStatic]
    private static Dictionary<string, string>? s_projectByAssemblyName;

    public static void ExtractTypeDependencies(
        INamedTypeSymbol sourceType,
        string sourceId,
        HashSet<string> knownTypeIds,
        string rootDir,
        Func<SyntaxTree, SemanticModel> getSemanticModel,
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> aggregator,
        bool includeGenerated = false,
        Dictionary<string, string>? projectNameByAssemblyName = null)
    {
        var prevMap = s_projectByAssemblyName;
        s_projectByAssemblyName = projectNameByAssemblyName;
        try
        {
            ExtractTypeDependenciesCore(sourceType, sourceId, knownTypeIds, rootDir, getSemanticModel, aggregator, includeGenerated);
        }
        finally
        {
            s_projectByAssemblyName = prevMap;
        }
    }

    private static void ExtractTypeDependenciesCore(
        INamedTypeSymbol sourceType,
        string sourceId,
        HashSet<string> knownTypeIds,
        string rootDir,
        Func<SyntaxTree, SemanticModel> getSemanticModel,
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> aggregator,
        bool includeGenerated)
    {
        var primaryLoc = GetPrimaryLocation(sourceType, rootDir, includeGenerated);

        if (sourceType.BaseType is not null)
        {
            var baseLoc = GetBaseListLocation(sourceType, sourceType.BaseType, rootDir, includeGenerated) ?? primaryLoc;
            RecordReferencedType(sourceId, sourceType.BaseType, DependencyKind.Inherits, baseLoc, knownTypeIds, aggregator);
        }

        foreach (var iface in sourceType.Interfaces)
        {
            var ifaceLoc = GetBaseListLocation(sourceType, iface, rootDir, includeGenerated) ?? primaryLoc;
            RecordReferencedType(sourceId, iface, DependencyKind.Implements, ifaceLoc, knownTypeIds, aggregator);
        }

        foreach (var attr in sourceType.GetAttributes())
        {
            if (attr.AttributeClass is not null)
            {
                RecordReferencedType(sourceId, attr.AttributeClass, DependencyKind.Attribute, primaryLoc, knownTypeIds, aggregator);
            }
        }

        ExtractMemberSignatureDependencies(sourceType, sourceId, primaryLoc, knownTypeIds, rootDir, aggregator, includeGenerated);
        ExtractTypeBodyDependencies(sourceType, sourceId, knownTypeIds, rootDir, getSemanticModel, aggregator, includeGenerated);
    }

    public static void ExtractMethodDependencies(
        IMethodSymbol sourceMethod,
        string sourceMethodId,
        HashSet<string> knownMethodIds,
        string rootDir,
        Func<SyntaxTree, SemanticModel> getSemanticModel,
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> aggregator)
    {
        var impl = ResolveImplementation(sourceMethod);

        foreach (var syntaxRef in impl.DeclaringSyntaxReferences)
        {
            var declSyntax = syntaxRef.GetSyntax();
            var semanticModel = getSemanticModel(syntaxRef.SyntaxTree);

            IEnumerable<SyntaxNode> roots = declSyntax is TypeDeclarationSyntax typeDecl
                ? GetPrimaryConstructorNodes(typeDecl)
                : [declSyntax];

            foreach (var root in roots)
            {
                WalkMethodBodyDependencies(root, sourceMethodId, knownMethodIds, rootDir, semanticModel, aggregator);
            }
        }
    }

    public static HashSet<string> CollectMethodsCalledFromPropertiesAndInitializers(
        INamedTypeSymbol type,
        HashSet<string> knownMethodIds,
        Func<SyntaxTree, SemanticModel> getSemanticModel)
    {
        var called = new HashSet<string>(StringComparer.Ordinal);
        foreach (var syntaxRef in type.DeclaringSyntaxReferences)
        {
            var declNode = syntaxRef.GetSyntax();
            var semanticModel = getSemanticModel(syntaxRef.SyntaxTree);

            // Also record methods referenced via nameof(...) anywhere in the type (or top-level statements)
            // so method-scope dead-code analysis treats them as compile-time live without emitting false call edges.
            IEnumerable<SyntaxNode> nameofRoots = declNode switch
            {
                TypeDeclarationSyntax td => [td],
                CompilationUnitSyntax cu => cu.Members.OfType<GlobalStatementSyntax>(),
                _ => []
            };
            foreach (var nRoot in nameofRoots)
            {
                foreach (var nameofInv in nRoot.DescendantNodes(descendIntoChildren: n =>
                             ReferenceEquals(n, nRoot) || n is not BaseTypeDeclarationSyntax)
                             .OfType<InvocationExpressionSyntax>()
                             .Where(IsNameofInvocation))
                {
                    foreach (var arg in nameofInv.ArgumentList.Arguments)
                    {
                        var info = semanticModel.GetSymbolInfo(arg.Expression);
                        var targets = info.Symbol is IMethodSymbol single
                            ? [single]
                            : info.CandidateSymbols.OfType<IMethodSymbol>().ToList();
                        foreach (var m in targets)
                        {
                            var id = GetMethodId(m);
                            if (knownMethodIds.Contains(id))
                            {
                                called.Add(id);
                            }
                        }
                    }
                }
            }

            if (declNode is not TypeDeclarationSyntax typeDecl)
            {
                continue;
            }

            foreach (var member in typeDecl.Members)
            {
                IEnumerable<SyntaxNode> roots = member switch
                {
                    PropertyDeclarationSyntax prop => [prop],
                    EventDeclarationSyntax ev => [ev],
                    FieldDeclarationSyntax field => field.Declaration.Variables
                        .Where(v => v.Initializer is not null)
                        .Select(v => (SyntaxNode)v.Initializer!),
                    _ => []
                };

                foreach (var root in roots)
                {
                    foreach (var node in root.DescendantNodes(descendIntoChildren: n =>
                                 n is not BaseTypeDeclarationSyntax && !IsNameofInvocation(n)))
                    {
                        IMethodSymbol? target = node switch
                        {
                            InvocationExpressionSyntax inv when !IsNameofInvocation(inv) =>
                                semanticModel.GetSymbolInfo(inv).Symbol as IMethodSymbol
                                ?? semanticModel.GetSymbolInfo(inv).CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault(),
                            ObjectCreationExpressionSyntax create => semanticModel.GetSymbolInfo(create).Symbol as IMethodSymbol,
                            ImplicitObjectCreationExpressionSyntax implCreate => semanticModel.GetSymbolInfo(implCreate).Symbol as IMethodSymbol,
                            SimpleNameSyntax name when !IsDirectInvocationCallee(name) => semanticModel.GetSymbolInfo(name).Symbol as IMethodSymbol,
                            _ => null
                        };

                        if (target is not null)
                        {
                            var id = GetMethodId(target);
                            if (knownMethodIds.Contains(id))
                            {
                                called.Add(id);
                            }
                        }
                    }
                }
            }
        }

        return called;
    }

    private static IEnumerable<SyntaxNode> GetPrimaryConstructorNodes(TypeDeclarationSyntax typeDecl)
    {
        if (typeDecl.ParameterList is not null)
        {
            yield return typeDecl.ParameterList;
        }

        if (typeDecl.BaseList is not null)
        {
            foreach (var baseType in typeDecl.BaseList.Types.OfType<PrimaryConstructorBaseTypeSyntax>())
            {
                yield return baseType;
            }
        }

        foreach (var member in typeDecl.Members)
        {
            if (member is FieldDeclarationSyntax field)
            {
                foreach (var variable in field.Declaration.Variables)
                {
                    if (variable.Initializer is not null)
                    {
                        yield return variable.Initializer;
                    }
                }
            }
            else if (member is PropertyDeclarationSyntax prop && prop.Initializer is not null)
            {
                yield return prop.Initializer;
            }
        }
    }

    private static void WalkMethodBodyDependencies(
        SyntaxNode root,
        string sourceMethodId,
        HashSet<string> knownMethodIds,
        string rootDir,
        SemanticModel semanticModel,
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> aggregator)
    {
        foreach (var node in root.DescendantNodesAndSelf(descendIntoChildren: n =>
                     (ReferenceEquals(n, root) || n is not BaseTypeDeclarationSyntax) && !IsNameofInvocation(n)))
        {
            switch (node)
            {
                case InvocationExpressionSyntax invocation when !IsNameofInvocation(invocation):
                    var invInfo = semanticModel.GetSymbolInfo(invocation);
                    var invoked = invInfo.Symbol as IMethodSymbol
                        ?? invInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                    if (invoked is not null)
                    {
                        var invLoc = ToSourceLocation(invocation.GetLocation(), rootDir);
                        RecordMethodTarget(sourceMethodId, invoked, DependencyKind.MethodCall,
                            invLoc, knownMethodIds, aggregator);
                        TryRecordReflectedMethodTarget(invocation, invoked, sourceMethodId, invLoc, semanticModel, knownMethodIds, aggregator);
                    }
                    break;

                case BinaryExpressionSyntax binExpr:
                    if (semanticModel.GetSymbolInfo(binExpr).Symbol is IMethodSymbol binOp &&
                        binOp.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion)
                    {
                        RecordMethodTarget(sourceMethodId, binOp, DependencyKind.MethodCall,
                            ToSourceLocation(binExpr.GetLocation(), rootDir), knownMethodIds, aggregator);
                    }
                    break;

                case PrefixUnaryExpressionSyntax prefixExpr:
                    if (semanticModel.GetSymbolInfo(prefixExpr).Symbol is IMethodSymbol prefixOp &&
                        prefixOp.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion)
                    {
                        RecordMethodTarget(sourceMethodId, prefixOp, DependencyKind.MethodCall,
                            ToSourceLocation(prefixExpr.GetLocation(), rootDir), knownMethodIds, aggregator);
                    }
                    break;

                case PostfixUnaryExpressionSyntax postfixExpr:
                    if (semanticModel.GetSymbolInfo(postfixExpr).Symbol is IMethodSymbol postfixOp &&
                        postfixOp.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion)
                    {
                        RecordMethodTarget(sourceMethodId, postfixOp, DependencyKind.MethodCall,
                            ToSourceLocation(postfixExpr.GetLocation(), rootDir), knownMethodIds, aggregator);
                    }
                    break;

                case AssignmentExpressionSyntax assignExpr when assignExpr.Kind() != SyntaxKind.SimpleAssignmentExpression:
                    if (semanticModel.GetSymbolInfo(assignExpr).Symbol is IMethodSymbol assignOp &&
                        assignOp.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion)
                    {
                        RecordMethodTarget(sourceMethodId, assignOp, DependencyKind.MethodCall,
                            ToSourceLocation(assignExpr.GetLocation(), rootDir), knownMethodIds, aggregator);
                    }
                    break;

                case ConstructorInitializerSyntax ctorInit:
                    if (semanticModel.GetSymbolInfo(ctorInit).Symbol is IMethodSymbol chainedCtor)
                    {
                        RecordMethodTarget(sourceMethodId, chainedCtor, DependencyKind.MethodCall,
                            ToSourceLocation(ctorInit.GetLocation(), rootDir), knownMethodIds, aggregator);
                    }
                    break;

                case PrimaryConstructorBaseTypeSyntax primaryBase:
                    if (semanticModel.GetSymbolInfo(primaryBase).Symbol is IMethodSymbol baseCtor)
                    {
                        RecordMethodTarget(sourceMethodId, baseCtor, DependencyKind.MethodCall,
                            ToSourceLocation(primaryBase.GetLocation(), rootDir), knownMethodIds, aggregator);
                    }
                    break;

                case ObjectCreationExpressionSyntax creation:
                    if (semanticModel.GetSymbolInfo(creation).Symbol is IMethodSymbol ctor)
                    {
                        RecordMethodTarget(sourceMethodId, ctor, DependencyKind.ObjectCreation,
                            ToSourceLocation(creation.GetLocation(), rootDir), knownMethodIds, aggregator);
                    }
                    break;

                case ImplicitObjectCreationExpressionSyntax implicitCreation:
                    if (semanticModel.GetSymbolInfo(implicitCreation).Symbol is IMethodSymbol implicitCtor)
                    {
                        RecordMethodTarget(sourceMethodId, implicitCtor, DependencyKind.ObjectCreation,
                            ToSourceLocation(implicitCreation.GetLocation(), rootDir), knownMethodIds, aggregator);
                    }
                    break;

                case SimpleNameSyntax simpleName when !IsDirectInvocationCallee(simpleName):
                    if (semanticModel.GetSymbolInfo(simpleName).Symbol is IMethodSymbol methodGroup)
                    {
                        RecordMethodTarget(sourceMethodId, methodGroup, DependencyKind.MethodCall,
                            ToSourceLocation(simpleName.GetLocation(), rootDir), knownMethodIds, aggregator);
                    }
                    break;
            }
        }
    }

    private static bool IsNameofInvocation(SyntaxNode node) =>
        node is InvocationExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.ValueText: "nameof" } };

    private static bool IsDirectInvocationCallee(SimpleNameSyntax name)
    {
        if (name.Parent is InvocationExpressionSyntax inv && inv.Expression == name)
        {
            return true;
        }

        if (name.Parent is MemberAccessExpressionSyntax ma &&
            ma.Name == name &&
            ma.Parent is InvocationExpressionSyntax parentInv &&
            parentInv.Expression == ma)
        {
            return true;
        }

        if (name.Parent is MemberBindingExpressionSyntax mb &&
            mb.Name == name &&
            mb.Parent is InvocationExpressionSyntax conditionalInv &&
            conditionalInv.Expression == mb)
        {
            return true;
        }

        return false;
    }

    private static void RecordMethodTarget(
        string sourceMethodId,
        IMethodSymbol targetMethod,
        DependencyKind kind,
        SourceLocation? location,
        HashSet<string> knownMethodIds,
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> aggregator)
    {
        var targetId = GetMethodId(targetMethod);
        if (!string.Equals(sourceMethodId, targetId, StringComparison.Ordinal) && knownMethodIds.Contains(targetId))
        {
            AddEdge(aggregator, sourceMethodId, targetId, kind, location);
        }
    }

    private static void TryRecordReflectedMethodTarget(
        InvocationExpressionSyntax invocation,
        IMethodSymbol invoked,
        string sourceMethodId,
        SourceLocation? invLoc,
        SemanticModel semanticModel,
        HashSet<string> knownMethodIds,
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> aggregator)
    {
        if (invoked.Name is not ("GetMethod" or "GetRuntimeMethod" or "GetMember") ||
            invoked.ContainingType?.ToDisplayString() is not ("System.Type" or "System.Reflection.RuntimeReflectionExtensions") ||
            invocation.ArgumentList.Arguments.Count == 0 ||
            invocation.Expression is not MemberAccessExpressionSyntax ma)
        {
            return;
        }

        INamedTypeSymbol? reflectedType = null;
        if (ma.Expression is TypeOfExpressionSyntax typeofExpr)
        {
            reflectedType = semanticModel.GetTypeInfo(typeofExpr.Type).Type as INamedTypeSymbol;
        }
        else if (ma.Expression is IdentifierNameSyntax idExpr &&
                 semanticModel.GetSymbolInfo(idExpr).Symbol is ILocalSymbol local &&
                 local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax decl &&
                 decl.Initializer?.Value is TypeOfExpressionSyntax localTypeOf)
        {
            reflectedType = semanticModel.GetTypeInfo(localTypeOf.Type).Type as INamedTypeSymbol;
        }

        if (reflectedType is null)
        {
            return;
        }

        var nameArgExpr = invocation.ArgumentList.Arguments[0].Expression;
        var constName = semanticModel.GetConstantValue(nameArgExpr);
        if (!constName.HasValue || constName.Value is not string methodName || string.IsNullOrWhiteSpace(methodName))
        {
            return;
        }

        for (var cur = reflectedType.OriginalDefinition; cur is not null; cur = cur.BaseType?.OriginalDefinition)
        {
            var matched = false;
            foreach (var member in cur.GetMembers(methodName).OfType<IMethodSymbol>())
            {
                RecordMethodTarget(sourceMethodId, member, DependencyKind.MethodCall, invLoc, knownMethodIds, aggregator);
                matched = true;
            }
            if (matched)
            {
                break;
            }
        }
    }

    private static SourceLocation? GetBaseListLocation(
        INamedTypeSymbol sourceType,
        INamedTypeSymbol targetType,
        string rootDir,
        bool includeGenerated)
    {
        SourceLocation? anyBaseListLoc = null;
        var targetShortName = targetType.Name;
        foreach (var syntaxRef in sourceType.DeclaringSyntaxReferences)
        {
            if (!includeGenerated && GeneratedCodeClassifier.IsGeneratedSyntaxTree(syntaxRef.SyntaxTree))
            {
                continue;
            }

            if (syntaxRef.GetSyntax() is TypeDeclarationSyntax td && td.BaseList is not null)
            {
                var baseListLoc = ToSourceLocation(td.BaseList.GetLocation(), rootDir);
                anyBaseListLoc ??= baseListLoc;
                foreach (var bt in td.BaseList.Types)
                {
                    if (bt.Type.ToString().Contains(targetShortName, StringComparison.Ordinal))
                    {
                        return ToSourceLocation(bt.GetLocation(), rootDir) ?? baseListLoc;
                    }
                }
            }
        }

        return anyBaseListLoc;
    }

    private static void ExtractMemberSignatureDependencies(
        INamedTypeSymbol sourceType,
        string sourceId,
        SourceLocation? primaryLoc,
        HashSet<string> knownTypeIds,
        string rootDir,
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> aggregator,
        bool includeGenerated = false)
    {
        foreach (var member in sourceType.GetMembers())
        {
            if (member.IsImplicitlyDeclared)
            {
                continue;
            }

            if (!includeGenerated &&
                member.DeclaringSyntaxReferences.Length > 0 &&
                member.DeclaringSyntaxReferences.All(r =>
                    GeneratedCodeClassifier.IsGeneratedSyntaxTree(r.SyntaxTree) &&
                    !GeneratedCodeClassifier.IsRazorGeneratedFilePath(r.SyntaxTree.FilePath)))
            {
                continue;
            }

            var memberLoc = GetPrimaryLocation(member, rootDir, includeGenerated) ?? primaryLoc;

            foreach (var attr in member.GetAttributes())
            {
                if (attr.AttributeClass is not null)
                {
                    RecordReferencedType(sourceId, attr.AttributeClass, DependencyKind.Attribute, memberLoc, knownTypeIds, aggregator);
                }
            }

            switch (member)
            {
                case IFieldSymbol field:
                    RecordReferencedType(sourceId, field.Type, DependencyKind.FieldOrProperty, memberLoc, knownTypeIds, aggregator);
                    break;
                case IPropertySymbol prop:
                    RecordReferencedType(sourceId, prop.Type, DependencyKind.FieldOrProperty, memberLoc, knownTypeIds, aggregator);
                    foreach (var param in prop.Parameters)
                    {
                        RecordReferencedType(sourceId, param.Type, DependencyKind.Parameter, memberLoc, knownTypeIds, aggregator);
                        foreach (var attr in param.GetAttributes())
                        {
                            if (attr.AttributeClass is not null)
                            {
                                RecordReferencedType(sourceId, attr.AttributeClass, DependencyKind.Attribute, memberLoc, knownTypeIds, aggregator);
                            }
                        }
                    }
                    break;
                case IEventSymbol ev:
                    RecordReferencedType(sourceId, ev.Type, DependencyKind.FieldOrProperty, memberLoc, knownTypeIds, aggregator);
                    break;
                case IMethodSymbol method:
                    if (!method.ReturnsVoid)
                    {
                        RecordReferencedType(sourceId, method.ReturnType, DependencyKind.ReturnType, memberLoc, knownTypeIds, aggregator);
                    }
                    foreach (var attr in method.GetReturnTypeAttributes())
                    {
                        if (attr.AttributeClass is not null)
                        {
                            RecordReferencedType(sourceId, attr.AttributeClass, DependencyKind.Attribute, memberLoc, knownTypeIds, aggregator);
                        }
                    }
                    foreach (var param in method.Parameters)
                    {
                        RecordReferencedType(sourceId, param.Type, DependencyKind.Parameter, memberLoc, knownTypeIds, aggregator);
                        foreach (var attr in param.GetAttributes())
                        {
                            if (attr.AttributeClass is not null)
                            {
                                RecordReferencedType(sourceId, attr.AttributeClass, DependencyKind.Attribute, memberLoc, knownTypeIds, aggregator);
                            }
                        }
                    }
                    break;
            }
        }
    }

    private static void ExtractTypeBodyDependencies(
        INamedTypeSymbol sourceType,
        string sourceId,
        HashSet<string> knownTypeIds,
        string rootDir,
        Func<SyntaxTree, SemanticModel> getSemanticModel,
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> aggregator,
        bool includeGenerated = false)
    {
        foreach (var syntaxRef in sourceType.DeclaringSyntaxReferences)
        {
            var isRazorGen = GeneratedCodeClassifier.IsRazorGeneratedFilePath(syntaxRef.SyntaxTree.FilePath);
            if (!includeGenerated && !isRazorGen && GeneratedCodeClassifier.IsGeneratedSyntaxTree(syntaxRef.SyntaxTree))
            {
                continue;
            }

            var declSyntax = syntaxRef.GetSyntax();
            if (!includeGenerated && !isRazorGen && GeneratedCodeClassifier.IsGeneratedSyntaxNode(declSyntax))
            {
                continue;
            }

            var semanticModel = getSemanticModel(syntaxRef.SyntaxTree);

            foreach (var node in declSyntax.DescendantNodes(descendIntoChildren: n =>
                         ReferenceEquals(n, declSyntax) || n is not BaseTypeDeclarationSyntax))
            {
                switch (node)
                {
                    case AttributeSyntax attrSyntax:
                        var attrType = semanticModel.GetTypeInfo(attrSyntax).Type
                            ?? semanticModel.GetSymbolInfo(attrSyntax).Symbol?.ContainingType;
                        if (attrType is not null)
                        {
                            RecordReferencedType(sourceId, attrType, DependencyKind.Attribute,
                                ToSourceLocation(attrSyntax.GetLocation(), rootDir), knownTypeIds, aggregator);
                        }
                        break;

                    case ObjectCreationExpressionSyntax creation:
                        var createdType = semanticModel.GetTypeInfo(creation).Type;
                        if (createdType is not null)
                        {
                            RecordReferencedType(sourceId, createdType, DependencyKind.ObjectCreation,
                                ToSourceLocation(creation.GetLocation(), rootDir), knownTypeIds, aggregator);
                        }
                        break;

                    case ImplicitObjectCreationExpressionSyntax implicitCreation:
                        var implicitType = semanticModel.GetTypeInfo(implicitCreation).Type;
                        if (implicitType is not null)
                        {
                            RecordReferencedType(sourceId, implicitType, DependencyKind.ObjectCreation,
                                ToSourceLocation(implicitCreation.GetLocation(), rootDir), knownTypeIds, aggregator);
                        }
                        break;

                    case BinaryExpressionSyntax binExpr:
                        if (semanticModel.GetSymbolInfo(binExpr).Symbol is IMethodSymbol binOp &&
                            binOp.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion &&
                            binOp.ContainingType is not null)
                        {
                            RecordReferencedType(sourceId, binOp.ContainingType, DependencyKind.MethodCall,
                                ToSourceLocation(binExpr.GetLocation(), rootDir), knownTypeIds, aggregator);
                        }
                        break;

                    case PrefixUnaryExpressionSyntax prefixExpr:
                        if (semanticModel.GetSymbolInfo(prefixExpr).Symbol is IMethodSymbol prefixOp &&
                            prefixOp.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion &&
                            prefixOp.ContainingType is not null)
                        {
                            RecordReferencedType(sourceId, prefixOp.ContainingType, DependencyKind.MethodCall,
                                ToSourceLocation(prefixExpr.GetLocation(), rootDir), knownTypeIds, aggregator);
                        }
                        break;

                    case PostfixUnaryExpressionSyntax postfixExpr:
                        if (semanticModel.GetSymbolInfo(postfixExpr).Symbol is IMethodSymbol postfixOp &&
                            postfixOp.MethodKind is MethodKind.UserDefinedOperator or MethodKind.Conversion &&
                            postfixOp.ContainingType is not null)
                        {
                            RecordReferencedType(sourceId, postfixOp.ContainingType, DependencyKind.MethodCall,
                                ToSourceLocation(postfixExpr.GetLocation(), rootDir), knownTypeIds, aggregator);
                        }
                        break;

                    case InvocationExpressionSyntax invocation:
                        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                        var methodSymbol = symbolInfo.Symbol as IMethodSymbol
                            ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                        if (methodSymbol is not null)
                        {
                            var invLoc = ToSourceLocation(invocation.GetLocation(), rootDir);
                            if (methodSymbol.ContainingType is not null)
                            {
                                RecordReferencedType(sourceId, methodSymbol.ContainingType, DependencyKind.MethodCall,
                                    invLoc, knownTypeIds, aggregator);
                            }

                            foreach (var typeArg in methodSymbol.TypeArguments)
                            {
                                RecordReferencedType(sourceId, typeArg, DependencyKind.GenericArgument,
                                    invLoc, knownTypeIds, aggregator);
                            }
                        }
                        break;

                    case MemberAccessExpressionSyntax memberAccess:
                    {
                        var exprSymbol = semanticModel.GetSymbolInfo(memberAccess.Expression).Symbol;
                        if (exprSymbol is INamedTypeSymbol receiverType)
                        {
                            // Captures static member / const / enum / nested-type access like `Registry.Instance`,
                            // `TaxRate.Value`, `Ns.Registry.Instance`, and `Generic<T>.Member`.
                            var isDirectSameTypeInvocation =
                                memberAccess.Parent is InvocationExpressionSyntax parentInv &&
                                parentInv.Expression == memberAccess &&
                                !receiverType.IsGenericType;
                            if (!isDirectSameTypeInvocation)
                            {
                                RecordReferencedType(sourceId, receiverType, DependencyKind.TypeReference,
                                    ToSourceLocation(memberAccess.Expression.GetLocation(), rootDir), knownTypeIds, aggregator);
                            }
                        }
                        else if (memberAccess.Parent is not InvocationExpressionSyntax)
                        {
                            var memberSymbol = semanticModel.GetSymbolInfo(memberAccess).Symbol;
                            if (memberSymbol is IFieldSymbol or IPropertySymbol or IEventSymbol &&
                                memberSymbol.ContainingType is not null)
                            {
                                RecordReferencedType(sourceId, memberSymbol.ContainingType, DependencyKind.FieldOrProperty,
                                    ToSourceLocation(memberAccess.GetLocation(), rootDir), knownTypeIds, aggregator);
                            }
                        }
                        break;
                    }

                    case QualifiedNameSyntax qualifiedName when node.Parent is not QualifiedNameSyntax:
                        if (semanticModel.GetSymbolInfo(qualifiedName).Symbol is INamedTypeSymbol qualifiedType)
                        {
                            RecordReferencedType(sourceId, qualifiedType, DependencyKind.TypeReference,
                                ToSourceLocation(qualifiedName.GetLocation(), rootDir), knownTypeIds, aggregator);
                        }
                        break;

                    case GenericNameSyntax genericName when node.Parent is not QualifiedNameSyntax and not MemberAccessExpressionSyntax:
                        if (semanticModel.GetSymbolInfo(genericName).Symbol is INamedTypeSymbol genericType)
                        {
                            RecordReferencedType(sourceId, genericType, DependencyKind.TypeReference,
                                ToSourceLocation(genericName.GetLocation(), rootDir), knownTypeIds, aggregator);
                        }
                        break;

                    case IdentifierNameSyntax identifier when node.Parent is not QualifiedNameSyntax and not MemberAccessExpressionSyntax:
                        var idSymbol = semanticModel.GetSymbolInfo(identifier).Symbol;
                        if (idSymbol is INamedTypeSymbol referencedType)
                        {
                            RecordReferencedType(sourceId, referencedType, DependencyKind.TypeReference,
                                ToSourceLocation(identifier.GetLocation(), rootDir), knownTypeIds, aggregator);
                        }
                        else if (idSymbol is IFieldSymbol or IPropertySymbol or IEventSymbol &&
                                 idSymbol.IsStatic &&
                                 idSymbol.ContainingType is not null)
                        {
                            // Captures `using static TaxRate;` followed by `Value`
                            RecordReferencedType(sourceId, idSymbol.ContainingType, DependencyKind.TypeReference,
                                ToSourceLocation(identifier.GetLocation(), rootDir), knownTypeIds, aggregator);
                        }
                        break;
                }
            }
        }
    }

    public static void RecordReferencedType(
        string sourceId,
        ITypeSymbol targetType,
        DependencyKind kind,
        SourceLocation? location,
        HashSet<string> knownTypeIds,
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> aggregator)
    {
        if (targetType is IArrayTypeSymbol arrayType)
        {
            RecordReferencedType(sourceId, arrayType.ElementType, kind, location, knownTypeIds, aggregator);
            return;
        }

        if (targetType is INamedTypeSymbol named)
        {
            var original = named.OriginalDefinition;
            var rawTargetId = GetTypeId(original);
            var targetId = rawTargetId;
            if (!knownTypeIds.Contains(targetId) &&
                s_projectByAssemblyName is not null &&
                original.ContainingAssembly?.Name is { } asmName &&
                s_projectByAssemblyName.TryGetValue(asmName, out var targetProj))
            {
                var qualified = $"{targetProj}::{rawTargetId}";
                if (knownTypeIds.Contains(qualified))
                {
                    targetId = qualified;
                }
            }

            if (!string.Equals(sourceId, targetId, StringComparison.Ordinal) && knownTypeIds.Contains(targetId))
            {
                AddEdge(aggregator, sourceId, targetId, kind, location);
            }

            foreach (var typeArg in named.TypeArguments)
            {
                RecordReferencedType(sourceId, typeArg, DependencyKind.GenericArgument, location, knownTypeIds, aggregator);
            }
        }
    }

    public static void AddEdge(
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> aggregator,
        string fromId,
        string toId,
        DependencyKind kind,
        SourceLocation? location)
    {
        var key = (fromId, toId, kind);
        if (aggregator.TryGetValue(key, out var existing))
        {
            aggregator[key] = (existing.Weight + 1, existing.Location ?? location);
        }
        else
        {
            aggregator[key] = (1, location);
        }
    }

    public static SourceLocation? GetPrimaryLocation(ISymbol symbol, string rootDir, bool includeGenerated = false)
    {
        Location? loc = null;
        if (!includeGenerated)
        {
            loc = symbol.Locations.FirstOrDefault(l => l.IsInSource && !GeneratedCodeClassifier.IsGeneratedSyntaxTree(l.SourceTree));
        }

        loc ??= symbol.Locations.FirstOrDefault(l => l.IsInSource);
        return loc is null ? null : ToSourceLocation(loc, rootDir);
    }

    public static SourceLocation? ToSourceLocation(Location location, string rootDir)
    {
        if (!location.IsInSource)
        {
            return null;
        }

        // For Razor generated trees, prefer mapped #line span so locations point to the .razor file
        var lineSpan = GeneratedCodeClassifier.IsRazorGeneratedFilePath(location.SourceTree?.FilePath)
            ? location.GetMappedLineSpan()
            : location.GetLineSpan();
        if (!lineSpan.IsValid || string.IsNullOrWhiteSpace(lineSpan.Path))
        {
            lineSpan = location.GetLineSpan();
        }

        var relPath = MakeRelativePath(rootDir, lineSpan.Path);
        return new SourceLocation(
            relPath,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1);
    }

    public static string MakeRelativePath(string rootDir, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            return Path.GetRelativePath(rootDir, path).Replace('\\', '/');
        }
        catch
        {
            return path.Replace('\\', '/');
        }
    }
}
