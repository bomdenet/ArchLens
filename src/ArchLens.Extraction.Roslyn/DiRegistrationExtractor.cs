using ArchLens.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchLens.Extraction.Roslyn;

/// <summary>
/// Extracts ASP.NET / Microsoft.Extensions.DependencyInjection registrations (AddSingleton/AddScoped/AddTransient/AddHostedService)
/// and Minimal API handler references (MapGet/MapPost/etc.) from Roslyn compilations.
/// </summary>
internal static class DiRegistrationExtractor
{
    private static readonly Dictionary<string, ServiceLifetimeKind> LifetimeByMethod = new(StringComparer.Ordinal)
    {
        ["AddSingleton"] = ServiceLifetimeKind.Singleton,
        ["TryAddSingleton"] = ServiceLifetimeKind.Singleton,
        ["AddKeyedSingleton"] = ServiceLifetimeKind.Singleton,
        ["TryAddKeyedSingleton"] = ServiceLifetimeKind.Singleton,
        ["AddHostedService"] = ServiceLifetimeKind.Singleton,
        ["AddScoped"] = ServiceLifetimeKind.Scoped,
        ["TryAddScoped"] = ServiceLifetimeKind.Scoped,
        ["AddKeyedScoped"] = ServiceLifetimeKind.Scoped,
        ["TryAddKeyedScoped"] = ServiceLifetimeKind.Scoped,
        ["AddTransient"] = ServiceLifetimeKind.Transient,
        ["TryAddTransient"] = ServiceLifetimeKind.Transient,
        ["AddKeyedTransient"] = ServiceLifetimeKind.Transient,
        ["TryAddKeyedTransient"] = ServiceLifetimeKind.Transient
    };

    private static readonly HashSet<string> MinimalApiMethods = new(StringComparer.Ordinal)
    {
        "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch", "MapMethods", "MapFallback", "MapHub", "MapGrpcService"
    };

    public static (List<DiRegistration> Registrations, HashSet<string> MinimalApiHandlerIds) Extract(
        Compilation compilation,
        string projectName,
        string rootDir,
        Func<SyntaxTree, SemanticModel> getSemanticModel,
        bool includeGenerated)
    {
        var registrations = new List<DiRegistration>();
        var minimalApiHandlers = new HashSet<string>(StringComparer.Ordinal);

        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!includeGenerated && IsGeneratedPath(tree.FilePath))
            {
                continue;
            }

            var root = tree.GetRoot();
            var semanticModel = getSemanticModel(tree);

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var methodName = GetInvokedMethodName(invocation);
                if (methodName is null)
                {
                    continue;
                }

                if (LifetimeByMethod.TryGetValue(methodName, out var lifetime))
                {
                    var reg = TryExtractDiRegistration(invocation, methodName, lifetime, projectName, semanticModel, rootDir);
                    if (reg is not null)
                    {
                        registrations.Add(reg);
                    }
                }
                else if (MinimalApiMethods.Contains(methodName))
                {
                    CollectMinimalApiHandlers(invocation, semanticModel, minimalApiHandlers);
                }
            }
        }

        return (registrations, minimalApiHandlers);
    }

    private static DiRegistration? TryExtractDiRegistration(
        InvocationExpressionSyntax invocation,
        string methodName,
        ServiceLifetimeKind lifetime,
        string projectName,
        SemanticModel semanticModel,
        string rootDir)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
        var methodSymbol = symbolInfo.Symbol as IMethodSymbol
            ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

        if (methodSymbol is not null && !IsServiceCollectionCall(methodSymbol, invocation, semanticModel))
        {
            return null;
        }

        INamedTypeSymbol? serviceType = null;
        INamedTypeSymbol? implType = null;

        var typeArgs = GetGenericTypeArguments(invocation, methodSymbol, semanticModel);
        if (typeArgs.Count >= 2)
        {
            serviceType = typeArgs[0];
            implType = typeArgs[1];
        }
        else if (typeArgs.Count == 1)
        {
            serviceType = typeArgs[0];
            implType = TryInferImplementationFromArguments(invocation, methodName, serviceType, allowTypeOf: true, semanticModel) ?? serviceType;
        }
        else
        {
            (serviceType, implType) = TryExtractNonGenericTypes(invocation, methodName, semanticModel);
        }

        if (serviceType is null || implType is null)
        {
            return null;
        }

        var containingType = semanticModel.GetEnclosingSymbol(invocation.SpanStart)?.ContainingType;
        var registeredBy = containingType is not null && !containingType.Name.StartsWith("<", StringComparison.Ordinal)
            ? SymbolDependencyExtractor.GetTypeId(containingType)
            : null;

        var implTypeId = implType.TypeKind == Microsoft.CodeAnalysis.TypeKind.Delegate ||
                         (implType.IsGenericType && !implType.IsUnboundGenericType && !implType.Locations.Any(l => l.IsInSource))
            ? SymbolDependencyExtractor.GetConstructedTypeId(implType)
            : SymbolDependencyExtractor.GetTypeId(implType);

        return new DiRegistration(
            ServiceTypeId: SymbolDependencyExtractor.GetConstructedTypeId(serviceType),
            ImplementationTypeId: implTypeId,
            Lifetime: lifetime,
            RegisteredByTypeId: registeredBy,
            Location: SymbolDependencyExtractor.ToSourceLocation(invocation.GetLocation(), rootDir),
            ProjectName: projectName);
    }

    private static bool IsServiceCollectionCall(IMethodSymbol method, InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        var receiver = method.ReceiverType;
        if (receiver is not null && IsServiceCollectionType(receiver))
        {
            return true;
        }

        if (method.Parameters.Length > 0 && IsServiceCollectionType(method.Parameters[0].Type))
        {
            return true;
        }

        if (invocation.Expression is MemberAccessExpressionSyntax ma)
        {
            var exprType = semanticModel.GetTypeInfo(ma.Expression).Type;
            if (exprType is not null && IsServiceCollectionType(exprType))
            {
                return true;
            }
        }

        return method.ContainingNamespace?.ToDisplayString().Contains("DependencyInjection", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool IsServiceCollectionType(ITypeSymbol type) =>
        type.Name is "IServiceCollection" or "ServiceCollection" ||
        type.AllInterfaces.Any(i => i.Name == "IServiceCollection");

    private static List<INamedTypeSymbol> GetGenericTypeArguments(
        InvocationExpressionSyntax invocation,
        IMethodSymbol? methodSymbol,
        SemanticModel semanticModel)
    {
        var result = new List<INamedTypeSymbol>();

        if (methodSymbol is { TypeArguments.Length: > 0 })
        {
            foreach (var ta in methodSymbol.TypeArguments)
            {
                if (ta is INamedTypeSymbol named && ta.TypeKind != Microsoft.CodeAnalysis.TypeKind.Error)
                {
                    result.Add(named);
                }
            }
            if (result.Count > 0)
            {
                return result;
            }
        }

        var genericName = invocation.Expression switch
        {
            GenericNameSyntax gn => gn,
            MemberAccessExpressionSyntax { Name: GenericNameSyntax mgn } => mgn,
            _ => null
        };

        if (genericName is not null)
        {
            foreach (var argSyntax in genericName.TypeArgumentList.Arguments)
            {
                if (semanticModel.GetTypeInfo(argSyntax).Type is INamedTypeSymbol named)
                {
                    result.Add(named);
                }
            }
        }

        return result;
    }

    private static INamedTypeSymbol? TryInferImplementationFromArguments(
        InvocationExpressionSyntax invocation,
        string methodName,
        INamedTypeSymbol? serviceType,
        bool allowTypeOf,
        SemanticModel semanticModel)
    {
        var isKeyed = methodName.Contains("Keyed", StringComparison.Ordinal);
        var args = invocation.ArgumentList.Arguments;

        for (var i = 0; i < args.Count; i++)
        {
            var expr = args[i].Expression;

            switch (expr)
            {
                case LambdaExpressionSyntax lambda:
                    var returnType = TryGetLambdaReturnedType(lambda, semanticModel);
                    if (returnType is not null)
                    {
                        return returnType;
                    }
                    break;

                case ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax:
                    if (semanticModel.GetTypeInfo(expr).Type is INamedTypeSymbol created)
                    {
                        return created;
                    }
                    break;

                case TypeOfExpressionSyntax typeOfExpr when allowTypeOf:
                    if (semanticModel.GetTypeInfo(typeOfExpr.Type).Type is INamedTypeSymbol t)
                    {
                        return t;
                    }
                    break;

                case IdentifierNameSyntax or MemberAccessExpressionSyntax:
                    // Skip the serviceKey argument in AddKeyed*(key, ...) when it's arg 0 (generic) or arg 1 (non-generic)
                    if (isKeyed && ((allowTypeOf && i == 0) || (!allowTypeOf && i == 1)))
                    {
                        continue;
                    }
                    if (semanticModel.GetTypeInfo(expr).Type is INamedTypeSymbol varType &&
                        !varType.IsAbstract &&
                        varType.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class &&
                        varType.SpecialType == SpecialType.None &&
                        !IsServiceCollectionType(varType) &&
                        (serviceType is null || !SymbolEqualityComparer.Default.Equals(varType, serviceType)))
                    {
                        return varType;
                    }
                    break;
            }
        }

        return null;
    }

    private static INamedTypeSymbol? TryGetLambdaReturnedType(LambdaExpressionSyntax lambda, SemanticModel semanticModel)
    {
        if (lambda.ExpressionBody is not null)
        {
            return semanticModel.GetTypeInfo(lambda.ExpressionBody).Type as INamedTypeSymbol;
        }

        if (lambda.Block is not null)
        {
            var ret = lambda.Block.DescendantNodes().OfType<ReturnStatementSyntax>().FirstOrDefault(r => r.Expression is not null);
            if (ret?.Expression is not null)
            {
                return semanticModel.GetTypeInfo(ret.Expression).Type as INamedTypeSymbol;
            }
        }

        return null;
    }

    private static (INamedTypeSymbol? Service, INamedTypeSymbol? Impl) TryExtractNonGenericTypes(
        InvocationExpressionSyntax invocation,
        string methodName,
        SemanticModel semanticModel)
    {
        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0)
        {
            return (null, null);
        }

        var typeofArgs = args
            .Select(a => a.Expression as TypeOfExpressionSyntax)
            .Where(t => t is not null)
            .Select(t => semanticModel.GetTypeInfo(t!.Type).Type as INamedTypeSymbol)
            .Where(t => t is not null)
            .Cast<INamedTypeSymbol>()
            .ToList();

        if (typeofArgs.Count >= 2)
        {
            return (typeofArgs[0], typeofArgs[1]);
        }

        if (typeofArgs.Count == 1)
        {
            // e.g. AddSingleton(typeof(ISvc), sp => new Impl()) or AddSingleton(typeof(ISvc), new Impl()) or AddSingleton(typeof(Impl))
            // allowTypeOf = false so args[0] (typeof(ISvc)) is NOT returned as the implementation!
            var inferred = TryInferImplementationFromArguments(invocation, methodName, typeofArgs[0], allowTypeOf: false, semanticModel);
            return (typeofArgs[0], inferred ?? typeofArgs[0]);
        }

        // e.g. AddSingleton(new MyInstance()) or AddKeyedSingleton("key", new MyInstance())
        var startIdx = methodName.Contains("Keyed", StringComparison.Ordinal) && args.Count >= 2 ? 1 : 0;
        if (startIdx < args.Count && semanticModel.GetTypeInfo(args[startIdx].Expression).Type is INamedTypeSymbol instType)
        {
            return (instType, instType);
        }

        return (null, null);
    }

    private static void CollectMinimalApiHandlers(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        HashSet<string> handlerIds)
    {
        var genericName = invocation.Expression switch
        {
            GenericNameSyntax gn => gn,
            MemberAccessExpressionSyntax { Name: GenericNameSyntax mgn } => mgn,
            _ => null
        };
        if (genericName is not null)
        {
            foreach (var ta in genericName.TypeArgumentList.Arguments)
            {
                if (semanticModel.GetTypeInfo(ta).Type is INamedTypeSymbol named)
                {
                    handlerIds.Add(SymbolDependencyExtractor.GetTypeId(named));
                }
            }
        }

        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            var symInfo = semanticModel.GetSymbolInfo(arg.Expression);
            var sym = symInfo.Symbol ?? symInfo.CandidateSymbols.FirstOrDefault();
            if (sym is IMethodSymbol handlerMethod)
            {
                handlerIds.Add(SymbolDependencyExtractor.GetMethodId(handlerMethod));
                if (handlerMethod.ContainingType is not null)
                {
                    handlerIds.Add(SymbolDependencyExtractor.GetTypeId(handlerMethod.ContainingType));
                }
            }
            else if (arg.Expression is LambdaExpressionSyntax lambda)
            {
                foreach (var node in lambda.DescendantNodes())
                {
                    switch (node)
                    {
                        case InvocationExpressionSyntax innerInv:
                            var innerInfo = semanticModel.GetSymbolInfo(innerInv);
                            var m = innerInfo.Symbol as IMethodSymbol
                                ?? innerInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                            if (m is not null)
                            {
                                handlerIds.Add(SymbolDependencyExtractor.GetMethodId(m));
                                if (m.ContainingType is not null)
                                {
                                    handlerIds.Add(SymbolDependencyExtractor.GetTypeId(m.ContainingType));
                                }
                            }
                            break;

                        case ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax:
                            if (semanticModel.GetTypeInfo(node).Type is INamedTypeSymbol created)
                            {
                                handlerIds.Add(SymbolDependencyExtractor.GetTypeId(created));
                            }
                            break;

                        case ParameterSyntax paramSyntax when paramSyntax.Type is not null:
                            if (semanticModel.GetTypeInfo(paramSyntax.Type).Type is INamedTypeSymbol paramType)
                            {
                                handlerIds.Add(SymbolDependencyExtractor.GetTypeId(paramType));
                            }
                            break;
                    }
                }
            }
        }
    }

    private static string? GetInvokedMethodName(InvocationExpressionSyntax invocation) => invocation.Expression switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        GenericNameSyntax gen => gen.Identifier.ValueText,
        MemberAccessExpressionSyntax { Name: SimpleNameSyntax sn } => sn.Identifier.ValueText,
        _ => null
    };

    private static bool IsGeneratedPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase);
}
