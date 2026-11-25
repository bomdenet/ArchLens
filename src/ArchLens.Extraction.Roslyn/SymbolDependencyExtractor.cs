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

    public static string GetTypeId(INamedTypeSymbol type) =>
        type.OriginalDefinition.ToDisplayString(TypeIdFormat);

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

        return type.ToDisplayString(ConstructedTypeIdFormat);
    }

    public static string GetMethodId(IMethodSymbol method)
    {
        var original = method.ReducedFrom?.OriginalDefinition ?? method.OriginalDefinition;
        if (original.MethodKind == MethodKind.StaticConstructor && original.ContainingType is not null)
        {
            return $"{GetTypeId(original.ContainingType)}.cctor()";
        }

        return original.ToDisplayString(MethodIdFormat);
    }

    public static IMethodSymbol ResolveImplementation(IMethodSymbol method) =>
        method.PartialImplementationPart ?? method;

    public static bool IsTrackableMethod(IMethodSymbol method)
    {
        if (method.IsImplicitlyDeclared)
        {
            return false;
        }

        if (method.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.ExplicitInterfaceImplementation))
        {
            return false;
        }

        var impl = ResolveImplementation(method);
        return impl.DeclaringSyntaxReferences.Length > 0;
    }

    public static (int LineCount, int TotalCyclomatic, int MaxMethodCyclomatic, int TotalCognitive, int MaxMethodCognitive) ComputeTypeSyntaxStats(
        INamedTypeSymbol type)
    {
        var totalLines = 0;
        var totalCyclomatic = 0;
        var maxCyclomatic = 0;
        var totalCognitive = 0;
        var maxCognitive = 0;

        foreach (var syntaxRef in type.DeclaringSyntaxReferences)
        {
            var syntax = syntaxRef.GetSyntax();
            var span = syntax.GetLocation().GetLineSpan();
            totalLines += Math.Max(1, span.EndLinePosition.Line - span.StartLinePosition.Line + 1);

            // Do not descend into nested type declarations so their complexity is not double-counted.
            // Skip bodyless auto-property accessors (get; set; / get; init;) so plain DTOs/records are not inflated.
            var executables = syntax
                .DescendantNodes(descendIntoChildren: n => ReferenceEquals(n, syntax) || n is not BaseTypeDeclarationSyntax)
                .Where(n => n is BaseMethodDeclarationSyntax
                         or AccessorDeclarationSyntax { Body: not null }
                         or AccessorDeclarationSyntax { ExpressionBody: not null }
                         or PropertyDeclarationSyntax { ExpressionBody: not null })
                .ToList();

            foreach (var execNode in executables)
            {
                var target = execNode is PropertyDeclarationSyntax prop ? (SyntaxNode)prop.ExpressionBody! : execNode;
                var cc = CyclomaticComplexityWalker.Calculate(target);
                var cog = CognitiveComplexityWalker.Calculate(target);
                totalCyclomatic += cc;
                totalCognitive += cog;
                if (cc > maxCyclomatic) maxCyclomatic = cc;
                if (cog > maxCognitive) maxCognitive = cog;
            }
        }

        return (totalLines, totalCyclomatic, maxCyclomatic, totalCognitive, maxCognitive);
    }

    public static (int LineCount, int Cyclomatic, int Cognitive) ComputeMethodSyntaxStats(IMethodSymbol method)
    {
        var impl = ResolveImplementation(method);
        var lines = 0;
        var cc = 0;
        var cog = 0;

        foreach (var syntaxRef in impl.DeclaringSyntaxReferences)
        {
            var syntax = syntaxRef.GetSyntax();
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
                    cog += CognitiveComplexityWalker.Calculate(node);
                }
                cc += Math.Max(1, initCc);
            }
            else
            {
                var span = syntax.GetLocation().GetLineSpan();
                lines += Math.Max(1, span.EndLinePosition.Line - span.StartLinePosition.Line + 1);
                cc += CyclomaticComplexityWalker.Calculate(syntax);
                cog += CognitiveComplexityWalker.Calculate(syntax, impl.Name, impl.Parameters.Length);
            }
        }

        return (Math.Max(1, lines), Math.Max(1, cc), cog);
    }

    public static void ExtractTypeDependencies(
        INamedTypeSymbol sourceType,
        string sourceId,
        HashSet<string> knownTypeIds,
        string rootDir,
        Func<SyntaxTree, SemanticModel> getSemanticModel,
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> aggregator)
    {
        var primaryLoc = GetPrimaryLocation(sourceType, rootDir);

        if (sourceType.BaseType is not null)
        {
            RecordReferencedType(sourceId, sourceType.BaseType, DependencyKind.Inherits, primaryLoc, knownTypeIds, aggregator);
        }

        foreach (var iface in sourceType.Interfaces)
        {
            RecordReferencedType(sourceId, iface, DependencyKind.Implements, primaryLoc, knownTypeIds, aggregator);
        }

        foreach (var attr in sourceType.GetAttributes())
        {
            if (attr.AttributeClass is not null)
            {
                RecordReferencedType(sourceId, attr.AttributeClass, DependencyKind.Attribute, primaryLoc, knownTypeIds, aggregator);
            }
        }

        ExtractMemberSignatureDependencies(sourceType, sourceId, primaryLoc, knownTypeIds, rootDir, aggregator);
        ExtractTypeBodyDependencies(sourceType, sourceId, knownTypeIds, rootDir, getSemanticModel, aggregator);
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
            if (syntaxRef.GetSyntax() is not TypeDeclarationSyntax typeDecl)
            {
                continue;
            }

            var semanticModel = getSemanticModel(syntaxRef.SyntaxTree);
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
                        RecordMethodTarget(sourceMethodId, invoked, DependencyKind.MethodCall,
                            ToSourceLocation(invocation.GetLocation(), rootDir), knownMethodIds, aggregator);
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

    private static void ExtractMemberSignatureDependencies(
        INamedTypeSymbol sourceType,
        string sourceId,
        SourceLocation? primaryLoc,
        HashSet<string> knownTypeIds,
        string rootDir,
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> aggregator)
    {
        foreach (var member in sourceType.GetMembers())
        {
            if (member.IsImplicitlyDeclared)
            {
                continue;
            }

            var memberLoc = GetPrimaryLocation(member, rootDir) ?? primaryLoc;

            switch (member)
            {
                case IFieldSymbol field:
                    RecordReferencedType(sourceId, field.Type, DependencyKind.FieldOrProperty, memberLoc, knownTypeIds, aggregator);
                    break;
                case IPropertySymbol prop:
                    RecordReferencedType(sourceId, prop.Type, DependencyKind.FieldOrProperty, memberLoc, knownTypeIds, aggregator);
                    break;
                case IEventSymbol ev:
                    RecordReferencedType(sourceId, ev.Type, DependencyKind.FieldOrProperty, memberLoc, knownTypeIds, aggregator);
                    break;
                case IMethodSymbol method:
                    if (!method.ReturnsVoid)
                    {
                        RecordReferencedType(sourceId, method.ReturnType, DependencyKind.ReturnType, memberLoc, knownTypeIds, aggregator);
                    }
                    foreach (var param in method.Parameters)
                    {
                        RecordReferencedType(sourceId, param.Type, DependencyKind.Parameter, memberLoc, knownTypeIds, aggregator);
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
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> aggregator)
    {
        foreach (var syntaxRef in sourceType.DeclaringSyntaxReferences)
        {
            var declSyntax = syntaxRef.GetSyntax();
            var semanticModel = getSemanticModel(syntaxRef.SyntaxTree);

            foreach (var node in declSyntax.DescendantNodes(descendIntoChildren: n =>
                         ReferenceEquals(n, declSyntax) || n is not BaseTypeDeclarationSyntax))
            {
                switch (node)
                {
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
            var targetId = GetTypeId(original);
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

    public static SourceLocation? GetPrimaryLocation(ISymbol symbol, string rootDir)
    {
        var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        return loc is null ? null : ToSourceLocation(loc, rootDir);
    }

    public static SourceLocation? ToSourceLocation(Location location, string rootDir)
    {
        if (!location.IsInSource)
        {
            return null;
        }

        var lineSpan = location.GetLineSpan();
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
