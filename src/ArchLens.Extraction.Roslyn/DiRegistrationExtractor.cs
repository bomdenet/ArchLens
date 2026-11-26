using System.Globalization;
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
        ["AddExceptionHandler"] = ServiceLifetimeKind.Singleton,
        ["AddDbContextFactory"] = ServiceLifetimeKind.Singleton,
        ["AddScoped"] = ServiceLifetimeKind.Scoped,
        ["TryAddScoped"] = ServiceLifetimeKind.Scoped,
        ["AddKeyedScoped"] = ServiceLifetimeKind.Scoped,
        ["TryAddKeyedScoped"] = ServiceLifetimeKind.Scoped,
        ["AddDbContext"] = ServiceLifetimeKind.Scoped,
        ["AddDbContextPool"] = ServiceLifetimeKind.Scoped,
        ["AddClaimsPrincipalFactory"] = ServiceLifetimeKind.Scoped,
        ["AddTransient"] = ServiceLifetimeKind.Transient,
        ["TryAddTransient"] = ServiceLifetimeKind.Transient,
        ["AddKeyedTransient"] = ServiceLifetimeKind.Transient,
        ["TryAddKeyedTransient"] = ServiceLifetimeKind.Transient,
        ["AddHttpClient"] = ServiceLifetimeKind.Transient
    };

    private static readonly HashSet<string> SelfRegisteredGenericMethods = new(StringComparer.Ordinal)
    {
        "AddDbContext", "AddDbContextPool", "AddDbContextFactory", "AddExceptionHandler", "AddClaimsPrincipalFactory"
    };

    private static readonly HashSet<string> MinimalApiMethods = new(StringComparer.Ordinal)
    {
        "Map", "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch", "MapMethods", "MapFallback",
        "MapHub", "MapGrpcService", "UseMiddleware"
    };

    private static readonly HashSet<string> DescriptorContainerMethods = new(StringComparer.Ordinal)
    {
        "Add", "TryAdd", "Replace", "TryAddEnumerable"
    };

    private static readonly Dictionary<string, ServiceLifetimeKind> DescriptorFactoryLifetimes = new(StringComparer.Ordinal)
    {
        ["Singleton"] = ServiceLifetimeKind.Singleton,
        ["KeyedSingleton"] = ServiceLifetimeKind.Singleton,
        ["Scoped"] = ServiceLifetimeKind.Scoped,
        ["KeyedScoped"] = ServiceLifetimeKind.Scoped,
        ["Transient"] = ServiceLifetimeKind.Transient,
        ["KeyedTransient"] = ServiceLifetimeKind.Transient,
        ["Describe"] = ServiceLifetimeKind.Transient,
        ["DescribeKeyed"] = ServiceLifetimeKind.Transient
    };

    public static (List<DiRegistration> Registrations, HashSet<string> MinimalApiHandlerIds) Extract(
        Compilation compilation,
        string projectName,
        string rootDir,
        Func<SyntaxTree, SemanticModel> getSemanticModel,
        bool includeGenerated)
    {
        var registrations = new List<DiRegistration>();
        var seenRegistrations = new HashSet<(string Service, string Impl, ServiceLifetimeKind Lifetime, string? ServiceKey, string? ContainerScope, string? BranchScope)>();
        var registeredBranchesByService = new Dictionary<(string Service, string? ServiceKey, string? ContainerScope), List<string>>();
        var minimalApiHandlers = new HashSet<string>(StringComparer.Ordinal);

        void RecordRegistration(DiRegistration reg, string branchScope, bool isTryAdd, bool isReplace)
        {
            var effectiveBranch = branchScope.Contains('|', StringComparison.Ordinal) ? branchScope : null;
            var scopedReg = effectiveBranch is not null ? reg with { BranchScope = effectiveBranch } : reg;
            var svcKey = (scopedReg.ServiceTypeId, scopedReg.ServiceKey, scopedReg.ContainerScope);
            if (isTryAdd && registeredBranchesByService.TryGetValue(svcKey, out var priorScopes) &&
                priorScopes.Any(prior => IsSameOrEnclosingBranch(prior, branchScope)))
            {
                return;
            }

            if (isReplace)
            {
                registrations.RemoveAll(r =>
                    string.Equals(r.ServiceTypeId, scopedReg.ServiceTypeId, StringComparison.Ordinal) &&
                    string.Equals(r.ServiceKey, scopedReg.ServiceKey, StringComparison.Ordinal) &&
                    string.Equals(r.ContainerScope, scopedReg.ContainerScope, StringComparison.Ordinal) &&
                    (effectiveBranch is null || string.Equals(r.BranchScope, effectiveBranch, StringComparison.Ordinal)));
            }

            if (seenRegistrations.Add((scopedReg.ServiceTypeId, scopedReg.ImplementationTypeId, scopedReg.Lifetime, scopedReg.ServiceKey, scopedReg.ContainerScope, scopedReg.BranchScope)) || isReplace)
            {
                registrations.Add(scopedReg);
                if (!registeredBranchesByService.TryGetValue(svcKey, out var scopes))
                {
                    scopes = [];
                    registeredBranchesByService[svcKey] = scopes;
                }
                scopes.Add(branchScope);
            }
        }

        foreach (var tree in compilation.SyntaxTrees)
        {
            if (!includeGenerated && GeneratedCodeClassifier.IsGeneratedSyntaxTree(tree))
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
                    var extractedRegs = TryExtractDiRegistrations(invocation, methodName, lifetime, projectName, semanticModel, rootDir);
                    if (extractedRegs.Count == 0)
                    {
                        continue;
                    }

                    var isTryAdd = methodName.StartsWith("TryAdd", StringComparison.Ordinal);
                    var branchScope = GetBranchScopeKey(tree.FilePath, invocation);
                    foreach (var reg in extractedRegs)
                    {
                        RecordRegistration(reg, branchScope, isTryAdd, isReplace: false);
                    }
                }
                else if (DescriptorContainerMethods.Contains(methodName))
                {
                    var descriptorReg = TryExtractServiceDescriptorCall(invocation, methodName, projectName, semanticModel, rootDir);
                    if (descriptorReg is not null)
                    {
                        var isTryAdd = methodName.Equals("TryAdd", StringComparison.Ordinal);
                        var isReplace = methodName.Equals("Replace", StringComparison.Ordinal);
                        var branchScope = GetBranchScopeKey(tree.FilePath, invocation);
                        RecordRegistration(descriptorReg, branchScope, isTryAdd, isReplace);
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

    private static string GetBranchScopeKey(string filePath, SyntaxNode node)
    {
        var segments = new List<string>();
        for (SyntaxNode? cur = node; cur is not null; cur = cur.Parent)
        {
            switch (cur)
            {
                case ElseClauseSyntax elseClause:
                    segments.Add($"if:{elseClause.Parent?.SpanStart ?? elseClause.SpanStart}:E");
                    break;
                case IfStatementSyntax ifStmt when node.SpanStart >= ifStmt.Statement.SpanStart && node.SpanStart <= ifStmt.Statement.Span.End:
                    segments.Add($"if:{ifStmt.SpanStart}:T");
                    break;
                case SwitchSectionSyntax swSec:
                    segments.Add($"sw:{swSec.SpanStart}");
                    break;
                case SwitchExpressionArmSyntax swArm:
                    segments.Add($"swa:{swArm.SpanStart}");
                    break;
                case ConditionalExpressionSyntax cond:
                    var side = node.SpanStart >= cond.WhenTrue.SpanStart && node.SpanStart <= cond.WhenTrue.Span.End ? "T" : "F";
                    segments.Add($"cond:{cond.SpanStart}:{side}");
                    break;
            }
        }

        segments.Reverse();
        return segments.Count == 0 ? filePath : $"{filePath}|{string.Join("/", segments)}";
    }

    private static bool IsSameOrEnclosingBranch(string priorScope, string currentScope)
    {
        if (string.Equals(priorScope, currentScope, StringComparison.Ordinal))
        {
            return true;
        }

        // Unconditional prior registration in the same file (no branch suffix) or an outer branch shadows inner branches.
        var priorBar = priorScope.IndexOf('|');
        var currBar = currentScope.IndexOf('|');
        var priorFile = priorBar >= 0 ? priorScope[..priorBar] : priorScope;
        var currFile = currBar >= 0 ? currentScope[..currBar] : currentScope;
        if (!string.Equals(priorFile, currFile, StringComparison.Ordinal))
        {
            // Different files: unconditional registration only shadows unconditional TryAdd when not inside a conditional branch
            return priorBar < 0 && currBar < 0;
        }

        if (priorBar < 0)
        {
            return true;
        }

        if (currBar < 0)
        {
            return false;
        }

        return currentScope.StartsWith(priorScope + "/", StringComparison.Ordinal);
    }

    private static DiRegistration? TryExtractServiceDescriptorCall(
        InvocationExpressionSyntax invocation,
        string containerMethodName,
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

        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            if (arg.Expression is InvocationExpressionSyntax factoryInv)
            {
                var reg = TryExtractFromServiceDescriptorFactory(factoryInv, invocation, projectName, semanticModel, rootDir);
                if (reg is not null)
                {
                    return reg;
                }
            }
            else if (arg.Expression is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax)
            {
                var reg = TryExtractFromServiceDescriptorCtor(arg.Expression, invocation, projectName, semanticModel, rootDir);
                if (reg is not null)
                {
                    return reg;
                }
            }
        }

        return null;
    }

    private static DiRegistration? TryExtractFromServiceDescriptorFactory(
        InvocationExpressionSyntax factoryInv,
        InvocationExpressionSyntax outerInvocation,
        string projectName,
        SemanticModel semanticModel,
        string rootDir)
    {
        var factoryName = GetInvokedMethodName(factoryInv);
        if (factoryName is null || !DescriptorFactoryLifetimes.TryGetValue(factoryName, out var defaultLifetime))
        {
            return null;
        }

        var symInfo = semanticModel.GetSymbolInfo(factoryInv);
        var sym = symInfo.Symbol as IMethodSymbol ?? symInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        var receiverText = factoryInv.Expression is MemberAccessExpressionSyntax ma ? ma.Expression.ToString() : string.Empty;
        var isServiceDescriptor = sym?.ContainingType?.Name == "ServiceDescriptor" ||
                                  receiverText.EndsWith("ServiceDescriptor", StringComparison.Ordinal);
        if (!isServiceDescriptor)
        {
            return null;
        }

        var lifetime = defaultLifetime;
        if (factoryName is "Describe" or "DescribeKeyed")
        {
            lifetime = TryExtractLifetimeFromArguments(factoryInv.ArgumentList.Arguments, semanticModel) ?? defaultLifetime;
        }

        INamedTypeSymbol? serviceType = null;
        INamedTypeSymbol? implType = null;
        var typeArgs = GetGenericTypeArguments(factoryInv, sym, semanticModel);
        if (typeArgs.Count >= 2)
        {
            serviceType = typeArgs[0];
            implType = typeArgs[1];
        }
        else if (typeArgs.Count == 1)
        {
            serviceType = typeArgs[0];
            implType = TryInferImplementationFromArguments(factoryInv, factoryName, serviceType, allowTypeOf: true, semanticModel) ?? serviceType;
        }
        else
        {
            (serviceType, implType) = TryExtractNonGenericTypes(factoryInv, factoryName, semanticModel);
        }

        if (serviceType is null || implType is null)
        {
            return null;
        }

        var serviceKey = TryExtractServiceKey(factoryInv, factoryName, typeArgs.Count > 0, semanticModel);
        return BuildRegistration(outerInvocation, serviceType, implType, lifetime, serviceKey, projectName, semanticModel, rootDir);
    }

    private static DiRegistration? TryExtractFromServiceDescriptorCtor(
        ExpressionSyntax creationExpr,
        InvocationExpressionSyntax outerInvocation,
        string projectName,
        SemanticModel semanticModel,
        string rootDir)
    {
        var createdType = semanticModel.GetTypeInfo(creationExpr).Type as INamedTypeSymbol;
        if (createdType?.Name != "ServiceDescriptor")
        {
            return null;
        }

        var argList = creationExpr switch
        {
            ObjectCreationExpressionSyntax oc => oc.ArgumentList,
            ImplicitObjectCreationExpressionSyntax ioc => ioc.ArgumentList,
            _ => null
        };
        if (argList is null || argList.Arguments.Count < 2)
        {
            return null;
        }

        var typeofArgs = argList.Arguments
            .Select(a => a.Expression as TypeOfExpressionSyntax)
            .Where(t => t is not null)
            .Select(t => semanticModel.GetTypeInfo(t!.Type).Type as INamedTypeSymbol)
            .Where(t => t is not null)
            .Cast<INamedTypeSymbol>()
            .ToList();

        if (typeofArgs.Count == 0)
        {
            return null;
        }

        var serviceType = typeofArgs[0];
        var implType = typeofArgs.Count >= 2
            ? typeofArgs[1]
            : TryInferImplementationFromArguments(outerInvocation, "ServiceDescriptor", serviceType, allowTypeOf: false, semanticModel) ?? serviceType;
        var lifetime = TryExtractLifetimeFromArguments(argList.Arguments, semanticModel) ?? ServiceLifetimeKind.Transient;

        // Keyed ServiceDescriptor ctors have signature (Type serviceType, object? serviceKey, Type/Func/object impl, ServiceLifetime)
        string? serviceKey = null;
        if (argList.Arguments.Count >= 4 && argList.Arguments[0].Expression is TypeOfExpressionSyntax)
        {
            var keyExpr = argList.Arguments[1].Expression;
            if (IsKeyedServiceAnyKey(keyExpr, semanticModel))
            {
                serviceKey = "<any-key>";
            }
            else
            {
                var c = semanticModel.GetConstantValue(keyExpr);
                var keyType = semanticModel.GetTypeInfo(keyExpr).Type;
                serviceKey = c.HasValue ? FormatServiceKeyConstant(c.Value, keyType) : keyExpr.ToString().Trim();
            }
        }

        return BuildRegistration(outerInvocation, serviceType, implType, lifetime, serviceKey, projectName, semanticModel, rootDir);
    }

    private static ServiceLifetimeKind? TryExtractLifetimeFromArguments(
        SeparatedSyntaxList<ArgumentSyntax> args,
        SemanticModel semanticModel)
    {
        foreach (var arg in args)
        {
            var text = arg.Expression.ToString();
            if (text.EndsWith(".Singleton", StringComparison.Ordinal) || text.Equals("Singleton", StringComparison.Ordinal))
                return ServiceLifetimeKind.Singleton;
            if (text.EndsWith(".Scoped", StringComparison.Ordinal) || text.Equals("Scoped", StringComparison.Ordinal))
                return ServiceLifetimeKind.Scoped;
            if (text.EndsWith(".Transient", StringComparison.Ordinal) || text.Equals("Transient", StringComparison.Ordinal))
                return ServiceLifetimeKind.Transient;

            var c = semanticModel.GetConstantValue(arg.Expression);
            if (c.HasValue && c.Value is int iv && iv is >= 0 and <= 2)
            {
                return iv switch
                {
                    0 => ServiceLifetimeKind.Singleton,
                    1 => ServiceLifetimeKind.Scoped,
                    2 => ServiceLifetimeKind.Transient,
                    _ => null
                };
            }
        }
        return null;
    }

    private static string? GetCanonicalRegisteredByTypeId(INamedTypeSymbol? containingType, string projectName)
    {
        if (containingType is null || containingType.Name.StartsWith("<", StringComparison.Ordinal))
        {
            return null;
        }

        var rawId = SymbolDependencyExtractor.GetTypeId(containingType);
        if (containingType.ContainingNamespace is { IsGlobalNamespace: true } &&
            !string.IsNullOrWhiteSpace(projectName) &&
            !rawId.Contains('.', StringComparison.Ordinal))
        {
            return $"{projectName}.{rawId}";
        }
        return rawId;
    }

    private static DiRegistration BuildRegistration(
        InvocationExpressionSyntax invocation,
        INamedTypeSymbol serviceType,
        INamedTypeSymbol implType,
        ServiceLifetimeKind lifetime,
        string? serviceKey,
        string projectName,
        SemanticModel semanticModel,
        string rootDir,
        string? methodName = null)
    {
        var containingType = semanticModel.GetEnclosingSymbol(invocation.SpanStart)?.ContainingType;
        var registeredBy = GetCanonicalRegisteredByTypeId(containingType, projectName);

        var implTypeId = implType.TypeKind == Microsoft.CodeAnalysis.TypeKind.Delegate ||
                         (implType.IsGenericType && !implType.IsUnboundGenericType && !implType.Locations.Any(l => l.IsInSource))
            ? SymbolDependencyExtractor.GetConstructedTypeId(implType)
            : SymbolDependencyExtractor.GetTypeId(implType);

        var serviceTypeId = methodName == "AddHostedService"
            ? "Microsoft.Extensions.Hosting.IHostedService"
            : SymbolDependencyExtractor.GetConstructedTypeId(serviceType);
        var containerScope = TryExtractContainerScope(invocation, semanticModel);

        return new DiRegistration(
            ServiceTypeId: serviceTypeId,
            ImplementationTypeId: implTypeId,
            Lifetime: lifetime,
            RegisteredByTypeId: registeredBy,
            Location: SymbolDependencyExtractor.ToSourceLocation(invocation.GetLocation(), rootDir),
            ProjectName: projectName,
            ServiceKey: serviceKey,
            ContainerScope: containerScope);
    }

    private static List<DiRegistration> TryExtractDiRegistrations(
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
            return [];
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
            implType = SelfRegisteredGenericMethods.Contains(methodName)
                ? serviceType
                : TryInferImplementationFromArguments(invocation, methodName, serviceType, allowTypeOf: true, semanticModel) ?? serviceType;
        }
        else
        {
            // Non-generic AddHttpClient() / AddHttpClient("name", ...) registers Singleton IHttpClientFactory
            if (methodName == "AddHttpClient")
            {
                var nonGenContaining = semanticModel.GetEnclosingSymbol(invocation.SpanStart)?.ContainingType;
                var nonGenRegisteredBy = GetCanonicalRegisteredByTypeId(nonGenContaining, projectName);
                var nonGenLoc = SymbolDependencyExtractor.ToSourceLocation(invocation.GetLocation(), rootDir);
                var nonGenScope = TryExtractContainerScope(invocation, semanticModel);
                return
                [
                    new DiRegistration(
                        ServiceTypeId: "System.Net.Http.IHttpClientFactory",
                        ImplementationTypeId: "System.Net.Http.IHttpClientFactory",
                        Lifetime: ServiceLifetimeKind.Singleton,
                        RegisteredByTypeId: nonGenRegisteredBy,
                        Location: nonGenLoc,
                        ProjectName: projectName,
                        ContainerScope: nonGenScope)
                ];
            }

            (serviceType, implType) = TryExtractNonGenericTypes(invocation, methodName, semanticModel);
        }

        if (serviceType is null || implType is null)
        {
            return [];
        }

        var containingType = semanticModel.GetEnclosingSymbol(invocation.SpanStart)?.ContainingType;
        var registeredBy = GetCanonicalRegisteredByTypeId(containingType, projectName);
        var loc = SymbolDependencyExtractor.ToSourceLocation(invocation.GetLocation(), rootDir);
        var containerScope = TryExtractContainerScope(invocation, semanticModel);

        // EF Core AddDbContext<TContext> / AddDbContextPool<TContext> register BOTH TContext (Scoped) AND DbContextOptions<TContext> (Scoped)
        if (methodName is "AddDbContext" or "AddDbContextPool")
        {
            var contextLifetime = TryExtractLifetimeFromArguments(invocation.ArgumentList.Arguments, semanticModel) ?? ServiceLifetimeKind.Scoped;
            var contextTypeId = SymbolDependencyExtractor.GetConstructedTypeId(serviceType);
            var contextImplId = SymbolDependencyExtractor.GetTypeId(serviceType);
            var optionsTypeId = $"Microsoft.EntityFrameworkCore.DbContextOptions<{contextTypeId}>";
            return
            [
                new DiRegistration(
                    ServiceTypeId: contextTypeId,
                    ImplementationTypeId: contextImplId,
                    Lifetime: contextLifetime,
                    RegisteredByTypeId: registeredBy,
                    Location: loc,
                    ProjectName: projectName,
                    ContainerScope: containerScope),
                new DiRegistration(
                    ServiceTypeId: optionsTypeId,
                    ImplementationTypeId: optionsTypeId,
                    Lifetime: contextLifetime,
                    RegisteredByTypeId: registeredBy,
                    Location: loc,
                    ProjectName: projectName,
                    ContainerScope: containerScope)
            ];
        }

        // EF Core AddDbContextFactory<TContext> registers IDbContextFactory<TContext>, DbContextOptions<TContext> (factoryLifetime, Singleton by default), AND TContext (Scoped)
        if (methodName == "AddDbContextFactory")
        {
            var factoryLifetime = TryExtractLifetimeFromArguments(invocation.ArgumentList.Arguments, semanticModel) ?? ServiceLifetimeKind.Singleton;
            var contextTypeId = SymbolDependencyExtractor.GetConstructedTypeId(serviceType);
            var contextImplId = SymbolDependencyExtractor.GetTypeId(serviceType);
            var factoryTypeId = $"Microsoft.EntityFrameworkCore.IDbContextFactory<{contextTypeId}>";
            var optionsTypeId = $"Microsoft.EntityFrameworkCore.DbContextOptions<{contextTypeId}>";
            return
            [
                new DiRegistration(
                    ServiceTypeId: factoryTypeId,
                    ImplementationTypeId: factoryTypeId,
                    Lifetime: factoryLifetime,
                    RegisteredByTypeId: registeredBy,
                    Location: loc,
                    ProjectName: projectName,
                    ContainerScope: containerScope),
                new DiRegistration(
                    ServiceTypeId: optionsTypeId,
                    ImplementationTypeId: optionsTypeId,
                    Lifetime: factoryLifetime,
                    RegisteredByTypeId: registeredBy,
                    Location: loc,
                    ProjectName: projectName,
                    ContainerScope: containerScope),
                new DiRegistration(
                    ServiceTypeId: contextTypeId,
                    ImplementationTypeId: contextImplId,
                    Lifetime: ServiceLifetimeKind.Scoped,
                    RegisteredByTypeId: registeredBy,
                    Location: loc,
                    ProjectName: projectName,
                    ContainerScope: containerScope)
            ];
        }

        var implTypeId = implType.TypeKind == Microsoft.CodeAnalysis.TypeKind.Delegate ||
                         (implType.IsGenericType && !implType.IsUnboundGenericType && !implType.Locations.Any(l => l.IsInSource))
            ? SymbolDependencyExtractor.GetConstructedTypeId(implType)
            : SymbolDependencyExtractor.GetTypeId(implType);

        var serviceTypeId = methodName == "AddHostedService"
            ? "Microsoft.Extensions.Hosting.IHostedService"
            : SymbolDependencyExtractor.GetConstructedTypeId(serviceType);

        var serviceKey = TryExtractServiceKey(invocation, methodName, typeArgs.Count > 0, semanticModel);
        var factoryDeps = ExtractFactoryLambdaDependencies(invocation, semanticModel);
        var isFactoryOrInstance = IsFactoryOrInstanceRegistration(invocation, methodName, typeArgs.Count, semanticModel);

        var results = new List<DiRegistration>
        {
            new DiRegistration(
                ServiceTypeId: serviceTypeId,
                ImplementationTypeId: implTypeId,
                Lifetime: lifetime,
                RegisteredByTypeId: registeredBy,
                Location: loc,
                ProjectName: projectName,
                ServiceKey: serviceKey,
                FactoryDependencyTypeIds: factoryDeps.Count > 0 ? factoryDeps : null,
                IsFactoryOrInstance: isFactoryOrInstance,
                ContainerScope: containerScope)
        };

        if (methodName == "AddHttpClient")
        {
            results.Add(new DiRegistration(
                ServiceTypeId: "System.Net.Http.IHttpClientFactory",
                ImplementationTypeId: "System.Net.Http.IHttpClientFactory",
                Lifetime: ServiceLifetimeKind.Singleton,
                RegisteredByTypeId: registeredBy,
                Location: loc,
                ProjectName: projectName,
                ContainerScope: containerScope));
        }

        return results;
    }

    private static bool IsFactoryOrInstanceRegistration(
        InvocationExpressionSyntax invocation,
        string methodName,
        int genericTypeArgCount,
        SemanticModel semanticModel)
    {
        if (SelfRegisteredGenericMethods.Contains(methodName) || methodName == "AddHttpClient")
        {
            return false;
        }

        var args = invocation.ArgumentList.Arguments;
        if (args.Count == 0)
        {
            return false;
        }

        // Any lambda argument (e.g. sp => new Impl(...)) is a factory registration
        if (args.Any(a => a.Expression is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax))
        {
            return true;
        }

        // Singleton instance overloads: AddSingleton(new Impl()), AddSingleton<ISvc>(new Impl()),
        // AddKeyedSingleton<ISvc>("key", new Impl()), AddSingleton(typeof(ISvc), new Impl())
        if (!methodName.Contains("Singleton", StringComparison.Ordinal) || genericTypeArgCount >= 2)
        {
            return false;
        }

        var isKeyed = methodName.Contains("Keyed", StringComparison.Ordinal);
        // Expected index of the implementation/instance argument (after optional serviceKey / typeof(ISvc))
        var startIdx = 0;
        if (genericTypeArgCount == 0 && args[0].Expression is TypeOfExpressionSyntax) startIdx++;
        if (isKeyed && startIdx < args.Count) startIdx++;

        for (var i = startIdx; i < args.Count; i++)
        {
            var expr = args[i].Expression;
            if (expr is TypeOfExpressionSyntax) continue;
            var typeInfo = semanticModel.GetTypeInfo(expr).Type;
            if (typeInfo is null) continue;
            var fullName = typeInfo.ToDisplayString();
            if (fullName == "System.Type" || fullName.EndsWith(".ServiceLifetime", StringComparison.Ordinal))
            {
                continue;
            }
            // A delegate/Func<IServiceProvider, ...> or an actual service instance expression
            return true;
        }

        return false;
    }

    private static List<string> ExtractFactoryLambdaDependencies(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel)
    {
        var deps = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var arg in invocation.ArgumentList.Arguments)
        {
            if (arg.Expression is not LambdaExpressionSyntax lambda)
            {
                continue;
            }

            var rootProviderParamSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var rootProviderParamNames = new HashSet<string>(StringComparer.Ordinal);
            switch (lambda)
            {
                case SimpleLambdaExpressionSyntax simple:
                    if (semanticModel.GetDeclaredSymbol(simple.Parameter) is { } sSym) rootProviderParamSymbols.Add(sSym);
                    rootProviderParamNames.Add(simple.Parameter.Identifier.ValueText);
                    break;
                case ParenthesizedLambdaExpressionSyntax paren when paren.ParameterList.Parameters.Count > 0:
                    var firstParam = paren.ParameterList.Parameters[0];
                    if (semanticModel.GetDeclaredSymbol(firstParam) is { } pSym) rootProviderParamSymbols.Add(pSym);
                    rootProviderParamNames.Add(firstParam.Identifier.ValueText);
                    break;
            }

            foreach (var innerInv in lambda.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = GetInvokedMethodName(innerInv);
                if (name is not ("GetService" or "GetRequiredService" or "GetServices"
                    or "GetKeyedService" or "GetRequiredKeyedService" or "GetKeyedServices"))
                {
                    continue;
                }

                // Only treat resolutions against the factory's root IServiceProvider parameter as captured;
                // resolutions via an explicit scope (`using var scope = sp.CreateScope(); scope.ServiceProvider.GetRequiredService<T>()`) are scope-safe.
                if (innerInv.Expression is MemberAccessExpressionSyntax ma)
                {
                    var recvSym = semanticModel.GetSymbolInfo(ma.Expression).Symbol;
                    var isRootProvider = recvSym is not null
                        ? rootProviderParamSymbols.Contains(recvSym)
                        : ma.Expression is IdentifierNameSyntax recvId && rootProviderParamNames.Contains(recvId.Identifier.ValueText);
                    if (!isRootProvider)
                    {
                        continue;
                    }
                }

                var isCollection = name is "GetServices" or "GetKeyedServices";
                var isKeyed = name is "GetKeyedService" or "GetRequiredKeyedService" or "GetKeyedServices";

                var symInfo = semanticModel.GetSymbolInfo(innerInv);
                var sym = symInfo.Symbol as IMethodSymbol
                    ?? symInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();

                INamedTypeSymbol? resolvedType = null;
                var typeArgs = GetGenericTypeArguments(innerInv, sym, semanticModel);
                if (typeArgs.Count >= 1)
                {
                    resolvedType = typeArgs[0];
                }
                else
                {
                    var typeofArg = innerInv.ArgumentList.Arguments
                        .Select(a => a.Expression as TypeOfExpressionSyntax)
                        .FirstOrDefault(t => t is not null);
                    if (typeofArg is not null)
                    {
                        resolvedType = semanticModel.GetTypeInfo(typeofArg.Type).Type as INamedTypeSymbol;
                    }
                }

                if (resolvedType is null)
                {
                    continue;
                }

                string? depKey = null;
                if (isKeyed)
                {
                    depKey = TryExtractServiceKey(innerInv, name, typeArgs.Count > 0, semanticModel);
                }

                var id = SymbolDependencyExtractor.GetConstructedTypeId(resolvedType);
                var core = depKey is not null
                    ? $"key:{depKey.Replace("%", "%25", StringComparison.Ordinal).Replace(";", "%3B", StringComparison.Ordinal).Replace("|", "%7C", StringComparison.Ordinal)}|{id}"
                    : id;
                var entry = isCollection ? $"many:{core}" : core;
                if (seen.Add(entry))
                {
                    deps.Add(entry);
                }
            }
        }

        return deps;
    }

    internal static string? FormatServiceKeyConstant(object? value, ITypeSymbol? exprType = null)
    {
        if (value is null)
        {
            return null;
        }

        if (exprType is INamedTypeSymbol { TypeKind: Microsoft.CodeAnalysis.TypeKind.Enum } enumType)
        {
            return $"{SymbolDependencyExtractor.GetTypeId(enumType)}:{Convert.ToString(value, CultureInfo.InvariantCulture)}";
        }

        return value is string s
            ? s
            : $"{value.GetType().Name}:{Convert.ToString(value, CultureInfo.InvariantCulture)}";
    }

    private static string? TryExtractContainerScope(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
    {
        ExpressionSyntax? receiver = invocation.Expression is MemberAccessExpressionSyntax ma ? ma.Expression : null;
        while (receiver is not null)
        {
            if (receiver is ParenthesizedExpressionSyntax paren)
            {
                receiver = paren.Expression;
                continue;
            }
            if (receiver is InvocationExpressionSyntax innerInv &&
                innerInv.Expression is MemberAccessExpressionSyntax innerMa)
            {
                receiver = innerMa.Expression;
                continue;
            }
            break;
        }

        if (receiver is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax)
        {
            var createdType = semanticModel.GetTypeInfo(receiver).Type;
            if (createdType is not null && IsServiceCollectionType(createdType))
            {
                return $"local:{invocation.SyntaxTree.FilePath}:{receiver.SpanStart}";
            }
            return null;
        }

        if (receiver is IdentifierNameSyntax ident &&
            semanticModel.GetSymbolInfo(ident).Symbol is ILocalSymbol local &&
            local.DeclaringSyntaxReferences.Length > 0 &&
            local.DeclaringSyntaxReferences[0].GetSyntax() is VariableDeclaratorSyntax decl &&
            decl.Initializer?.Value is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax)
        {
            var enclosingBlock = decl.Ancestors().FirstOrDefault(a =>
                a is BlockSyntax or BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or CompilationUnitSyntax);
            if (enclosingBlock is not null)
            {
                foreach (var ret in enclosingBlock.DescendantNodes().OfType<ReturnStatementSyntax>())
                {
                    if (ret.Expression is IdentifierNameSyntax retId &&
                        SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(retId).Symbol, local))
                    {
                        return null;
                    }
                }

                foreach (var inv in enclosingBlock.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    var name = GetInvokedMethodName(inv);
                    if (name is null) continue;
                    if (LifetimeByMethod.ContainsKey(name) || DescriptorContainerMethods.Contains(name) ||
                        name is "BuildServiceProvider" or "Clear" or "Remove" or "RemoveAll" or "Insert" or "Contains" or "IndexOf" or "CopyTo" or "GetEnumerator")
                    {
                        continue;
                    }

                    var invSym = semanticModel.GetSymbolInfo(inv).Symbol;
                    var isInSource = invSym is not null && invSym.Locations.Any(l => l.IsInSource);
                    if (!isInSource) continue;

                    foreach (var arg in inv.ArgumentList.Arguments)
                    {
                        if (arg.Expression is IdentifierNameSyntax argId &&
                            SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(argId).Symbol, local))
                        {
                            return null;
                        }
                    }

                    if (inv.Expression is MemberAccessExpressionSyntax extMa &&
                        extMa.Expression is IdentifierNameSyntax recvId &&
                        SymbolEqualityComparer.Default.Equals(semanticModel.GetSymbolInfo(recvId).Symbol, local))
                    {
                        return null;
                    }
                }
            }

            return $"local:{decl.SyntaxTree.FilePath}:{decl.SpanStart}";
        }

        return null;
    }

    private static string? TryExtractServiceKey(
        InvocationExpressionSyntax invocation,
        string methodName,
        bool isGeneric,
        SemanticModel semanticModel)
    {
        if (!methodName.Contains("Keyed", StringComparison.Ordinal))
        {
            return null;
        }

        var args = invocation.ArgumentList.Arguments;
        // Generic AddKeyed*(serviceKey, ...) has key at index 0; non-generic AddKeyed*(typeof(ISvc), serviceKey, ...) has key at index 1 (or index 0 when no typeof).
        var keyIdx = isGeneric
            ? 0
            : (args.Count > 1 && args[0].Expression is TypeOfExpressionSyntax ? 1 : 0);

        if (keyIdx >= args.Count)
        {
            return "<keyed>";
        }

        var keyExpr = args[keyIdx].Expression;
        if (IsKeyedServiceAnyKey(keyExpr, semanticModel))
        {
            return "<any-key>";
        }

        var constVal = semanticModel.GetConstantValue(keyExpr);
        if (constVal.HasValue)
        {
            var keyType = semanticModel.GetTypeInfo(keyExpr).Type;
            return FormatServiceKeyConstant(constVal.Value, keyType);
        }

        var text = keyExpr.ToString().Trim();
        return string.IsNullOrEmpty(text) ? "<keyed>" : text;
    }

    private static bool IsKeyedServiceAnyKey(ExpressionSyntax expr, SemanticModel semanticModel)
    {
        var sym = semanticModel.GetSymbolInfo(expr).Symbol;
        if (sym is { Name: "AnyKey", ContainingType.Name: "KeyedService" })
        {
            return true;
        }

        var text = expr.ToString().Trim();
        return text.Equals("KeyedService.AnyKey", StringComparison.Ordinal) ||
               text.EndsWith(".KeyedService.AnyKey", StringComparison.Ordinal);
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
        type.Name is "IServiceCollection" or "ServiceCollection" or "IdentityBuilder" ||
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
                    if (returnType is not null &&
                        (serviceType is null || methodName == "AddHostedService" || IsCompatibleImplementation(returnType, serviceType)))
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

    private static bool IsCompatibleImplementation(INamedTypeSymbol candidate, INamedTypeSymbol serviceType)
    {
        if (SymbolEqualityComparer.Default.Equals(candidate, serviceType) ||
            SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, serviceType.OriginalDefinition))
        {
            return true;
        }

        for (var b = candidate.BaseType; b is not null; b = b.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(b, serviceType) ||
                SymbolEqualityComparer.Default.Equals(b.OriginalDefinition, serviceType.OriginalDefinition))
            {
                return true;
            }
        }

        return candidate.AllInterfaces.Any(i =>
            SymbolEqualityComparer.Default.Equals(i, serviceType) ||
            SymbolEqualityComparer.Default.Equals(i.OriginalDefinition, serviceType.OriginalDefinition));
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
        if (startIdx < args.Count &&
            semanticModel.GetTypeInfo(args[startIdx].Expression).Type is INamedTypeSymbol instType &&
            instType.SpecialType == SpecialType.None)
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
