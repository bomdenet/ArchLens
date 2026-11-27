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
        var seenRegistrations = new HashSet<(string Service, string Impl, ServiceLifetimeKind Lifetime, string? ServiceKey, string? ContainerScope, string? BranchScope, string? FilePath, int Line, int Column)>();
        var registeredBranchesByService = new Dictionary<(string Service, string? ServiceKey, string? ContainerScope), List<string>>();
        var registeredBranchesByServiceImpl = new Dictionary<(string Service, string Impl, string? ServiceKey, string? ContainerScope), List<string>>();
        var minimalApiHandlers = new HashSet<string>(StringComparer.Ordinal);

        void RecordRegistration(DiRegistration reg, string branchScope, bool isTryAdd, bool isReplace, bool isTryAddEnumerable = false)
        {
            var effectiveBranch = branchScope.Contains('|', StringComparison.Ordinal) ? branchScope : null;
            var scopedReg = effectiveBranch is not null ? reg with { BranchScope = effectiveBranch } : reg;
            var svcKey = (scopedReg.ServiceTypeId, scopedReg.ServiceKey, scopedReg.ContainerScope);
            var svcImplKey = (scopedReg.ServiceTypeId, scopedReg.ImplementationTypeId, scopedReg.ServiceKey, scopedReg.ContainerScope);
            if (isTryAdd && registeredBranchesByService.TryGetValue(svcKey, out var priorScopes) &&
                priorScopes.Any(prior => IsSameOrEnclosingBranch(prior, branchScope)))
            {
                return;
            }

            if (isTryAddEnumerable && registeredBranchesByServiceImpl.TryGetValue(svcImplKey, out var priorImplScopes) &&
                priorImplScopes.Any(prior => IsSameOrEnclosingBranch(prior, branchScope)))
            {
                return;
            }

            if (isReplace)
            {
                var replaceIdx = registrations.FindIndex(r =>
                    string.Equals(r.ServiceTypeId, scopedReg.ServiceTypeId, StringComparison.Ordinal) &&
                    string.Equals(r.ServiceKey, scopedReg.ServiceKey, StringComparison.Ordinal) &&
                    string.Equals(r.ContainerScope, scopedReg.ContainerScope, StringComparison.Ordinal) &&
                    (effectiveBranch is null || string.Equals(r.BranchScope, effectiveBranch, StringComparison.Ordinal)));
                if (replaceIdx >= 0)
                {
                    registrations.RemoveAt(replaceIdx);
                }
            }

            var locKey = (scopedReg.ServiceTypeId, scopedReg.ImplementationTypeId, scopedReg.Lifetime, scopedReg.ServiceKey, scopedReg.ContainerScope, scopedReg.BranchScope, scopedReg.Location?.FilePath, scopedReg.Location?.Line ?? 0, scopedReg.Location?.Column ?? 0);
            if (seenRegistrations.Add(locKey) || isReplace)
            {
                registrations.Add(scopedReg);
                if (!registeredBranchesByService.TryGetValue(svcKey, out var scopes))
                {
                    scopes = [];
                    registeredBranchesByService[svcKey] = scopes;
                }
                scopes.Add(branchScope);

                if (!registeredBranchesByServiceImpl.TryGetValue(svcImplKey, out var implScopes))
                {
                    implScopes = [];
                    registeredBranchesByServiceImpl[svcImplKey] = implScopes;
                }
                implScopes.Add(branchScope);
            }
        }

        void RemoveMatchingRegistrations(string? removeServiceTypeId, string? removeContainerScope, string branchScope)
        {
            var effectiveBranch = branchScope.Contains('|', StringComparison.Ordinal) ? branchScope : null;
            registrations.RemoveAll(r =>
                (removeServiceTypeId is null || string.Equals(r.ServiceTypeId, removeServiceTypeId, StringComparison.Ordinal)) &&
                string.Equals(r.ContainerScope, removeContainerScope, StringComparison.Ordinal) &&
                (effectiveBranch is null || string.Equals(r.BranchScope, effectiveBranch, StringComparison.Ordinal)));

            if (effectiveBranch is null)
            {
                foreach (var key in registeredBranchesByService.Keys
                    .Where(k => (removeServiceTypeId is null || string.Equals(k.Service, removeServiceTypeId, StringComparison.Ordinal)) &&
                                string.Equals(k.ContainerScope, removeContainerScope, StringComparison.Ordinal))
                    .ToList())
                {
                    registeredBranchesByService.Remove(key);
                }
                foreach (var key in registeredBranchesByServiceImpl.Keys
                    .Where(k => (removeServiceTypeId is null || string.Equals(k.Service, removeServiceTypeId, StringComparison.Ordinal)) &&
                                string.Equals(k.ContainerScope, removeContainerScope, StringComparison.Ordinal))
                    .ToList())
                {
                    registeredBranchesByServiceImpl.Remove(key);
                }
            }
        }

        // Group DI registrations and in-source IServiceCollection helper invocations by enclosing method/top-level unit
        // so interprocedural call order (e.g. Program.cs calling AddScoped then services.AddInfra()) matches runtime order.
        var ownerOrder = new List<object>();
        var ownerSteps = new Dictionary<object, List<DiOwnerStep>>(DiOwnerComparer.Instance);
        var calledHelpers = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

        List<DiOwnerStep> GetOrCreateSteps(object owner)
        {
            if (!ownerSteps.TryGetValue(owner, out var list))
            {
                list = [];
                ownerSteps[owner] = list;
                ownerOrder.Add(owner);
            }
            return list;
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

                if (IsInUnreachableConstantBranch(invocation, semanticModel))
                {
                    continue;
                }

                if (LifetimeByMethod.TryGetValue(methodName, out var lifetime))
                {
                    var extractedRegs = TryExtractDiRegistrations(invocation, methodName, lifetime, projectName, semanticModel, getSemanticModel, rootDir);
                    if (extractedRegs.Count == 0)
                    {
                        continue;
                    }

                    var isTryAdd = methodName.StartsWith("TryAdd", StringComparison.Ordinal);
                    var branchScope = GetBranchScopeKey(tree.FilePath, invocation);
                    var owner = GetEnclosingDiOwner(invocation, tree, semanticModel);
                    GetOrCreateSteps(owner).Add(new DiOwnerStep(extractedRegs, branchScope, isTryAdd, IsReplace: false, HelperTarget: null));
                }
                else if (DescriptorContainerMethods.Contains(methodName))
                {
                    var descriptorReg = TryExtractServiceDescriptorCall(invocation, methodName, projectName, semanticModel, getSemanticModel, rootDir);
                    if (descriptorReg is not null)
                    {
                        var isTryAdd = methodName.Equals("TryAdd", StringComparison.Ordinal);
                        var isReplace = methodName.Equals("Replace", StringComparison.Ordinal);
                        var isTryAddEnum = methodName.Equals("TryAddEnumerable", StringComparison.Ordinal);
                        var branchScope = GetBranchScopeKey(tree.FilePath, invocation);
                        var owner = GetEnclosingDiOwner(invocation, tree, semanticModel);
                        GetOrCreateSteps(owner).Add(new DiOwnerStep([descriptorReg], branchScope, isTryAdd, isReplace, HelperTarget: null, IsTryAddEnumerable: isTryAddEnum));
                    }
                }
                else if (methodName is "RemoveAll" or "Clear")
                {
                    var removeInfo = TryExtractRemoveCall(invocation, methodName, semanticModel);
                    if (removeInfo.HasValue)
                    {
                        var branchScope = GetBranchScopeKey(tree.FilePath, invocation);
                        var owner = GetEnclosingDiOwner(invocation, tree, semanticModel);
                        GetOrCreateSteps(owner).Add(new DiOwnerStep(
                            null, branchScope, false, false, HelperTarget: null,
                            IsRemove: true,
                            RemoveServiceTypeId: removeInfo.Value.ServiceTypeId,
                            RemoveContainerScope: removeInfo.Value.ContainerScope));
                    }
                }
                else if (MinimalApiMethods.Contains(methodName))
                {
                    CollectMinimalApiHandlers(invocation, semanticModel, minimalApiHandlers);
                }
                else
                {
                    var helperTarget = TryResolveInSourceServiceCollectionHelper(invocation, semanticModel);
                    if (helperTarget is not null)
                    {
                        var owner = GetEnclosingDiOwner(invocation, tree, semanticModel);
                        if (!Equals(owner, helperTarget))
                        {
                            GetOrCreateSteps(owner).Add(new DiOwnerStep(null, string.Empty, false, false, helperTarget));
                            calledHelpers.Add(helperTarget);
                        }
                    }
                }
            }
        }

        var visitedOwners = new HashSet<object>(DiOwnerComparer.Instance);
        var visitingOwners = new HashSet<object>(DiOwnerComparer.Instance);

        void EmitOwner(object owner)
        {
            if (!visitedOwners.Add(owner) || !visitingOwners.Add(owner))
            {
                return;
            }
            if (ownerSteps.TryGetValue(owner, out var steps))
            {
                foreach (var step in steps)
                {
                    if (step.HelperTarget is not null)
                    {
                        if (ownerSteps.ContainsKey(step.HelperTarget))
                        {
                            EmitOwner(step.HelperTarget);
                        }
                    }
                    else if (step.IsRemove)
                    {
                        RemoveMatchingRegistrations(step.RemoveServiceTypeId, step.RemoveContainerScope, step.BranchScope);
                    }
                    else if (step.Registrations is not null)
                    {
                        foreach (var reg in step.Registrations)
                        {
                            RecordRegistration(reg, step.BranchScope, step.IsTryAdd, step.IsReplace, step.IsTryAddEnumerable);
                        }
                    }
                }
            }
            visitingOwners.Remove(owner);
        }

        // First emit root entry points/callers (inlining called DI helpers at their exact call sites),
        // then emit any uncalled helpers (e.g. library Add* extension methods).
        foreach (var owner in ownerOrder)
        {
            if (owner is ISymbol sym && calledHelpers.Contains(sym))
            {
                continue;
            }
            EmitOwner(owner);
        }

        foreach (var owner in ownerOrder)
        {
            if (!visitedOwners.Contains(owner))
            {
                EmitOwner(owner);
            }
        }

        return (registrations, minimalApiHandlers);
    }

    private sealed class DiOwnerComparer : IEqualityComparer<object>
    {
        public static readonly DiOwnerComparer Instance = new();
        public new bool Equals(object? x, object? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is ISymbol sx && y is ISymbol sy) return SymbolEqualityComparer.Default.Equals(sx, sy);
            return object.Equals(x, y);
        }
        public int GetHashCode(object obj) =>
            obj is ISymbol s ? SymbolEqualityComparer.Default.GetHashCode(s) : obj.GetHashCode();
    }

    private sealed record DiOwnerStep(
        List<DiRegistration>? Registrations,
        string BranchScope,
        bool IsTryAdd,
        bool IsReplace,
        IMethodSymbol? HelperTarget,
        bool IsTryAddEnumerable = false,
        bool IsRemove = false,
        string? RemoveServiceTypeId = null,
        string? RemoveContainerScope = null);

    private static (string? ServiceTypeId, string? ContainerScope)? TryExtractRemoveCall(
        InvocationExpressionSyntax invocation,
        string methodName,
        SemanticModel semanticModel)
    {
        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
        var methodSymbol = symbolInfo.Symbol as IMethodSymbol
            ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        if (methodSymbol is not null && !IsServiceCollectionCall(methodSymbol, invocation, semanticModel))
        {
            return null;
        }
        if (methodSymbol is null && invocation.Expression is MemberAccessExpressionSyntax ma)
        {
            var recvType = semanticModel.GetTypeInfo(ma.Expression).Type;
            if (recvType is null || !IsServiceCollectionType(recvType))
            {
                return null;
            }
        }

        var containerScope = TryExtractContainerScope(invocation, semanticModel);
        if (methodName == "Clear")
        {
            return (null, containerScope);
        }

        INamedTypeSymbol? targetService = null;
        var typeArgs = GetGenericTypeArguments(invocation, methodSymbol, semanticModel);
        if (typeArgs.Count >= 1)
        {
            targetService = typeArgs[0];
        }
        else if (invocation.ArgumentList.Arguments.Count >= 1 &&
                 invocation.ArgumentList.Arguments[0].Expression is TypeOfExpressionSyntax typeofExpr)
        {
            targetService = semanticModel.GetTypeInfo(typeofExpr.Type).Type as INamedTypeSymbol;
        }

        if (targetService is null)
        {
            return null;
        }

        return (SymbolDependencyExtractor.GetConstructedTypeId(targetService), containerScope);
    }

    private static object GetEnclosingDiOwner(InvocationExpressionSyntax invocation, SyntaxTree tree, SemanticModel semanticModel)
    {
        for (SyntaxNode? cur = invocation.Parent; cur is not null; cur = cur.Parent)
        {
            if (cur is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax)
            {
                if (semanticModel.GetDeclaredSymbol(cur) is IMethodSymbol mSym)
                {
                    return mSym.OriginalDefinition;
                }
                break;
            }
        }
        return tree;
    }

    private static IMethodSymbol? TryResolveInSourceServiceCollectionHelper(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel)
    {
        var symInfo = semanticModel.GetSymbolInfo(invocation);
        var sym = symInfo.Symbol as IMethodSymbol
            ?? symInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
        if (sym is null)
        {
            return null;
        }

        var target = (sym.ReducedFrom ?? sym).OriginalDefinition;
        if (target.DeclaringSyntaxReferences.Length == 0)
        {
            return null;
        }

        if (target.Parameters.Any(p => IsServiceCollectionType(p.Type)))
        {
            return target;
        }

        return null;
    }

    private static bool IsInUnreachableConstantBranch(SyntaxNode node, SemanticModel semanticModel)
    {
        for (SyntaxNode? cur = node.Parent; cur is not null; cur = cur.Parent)
        {
            switch (cur)
            {
                case ElseClauseSyntax elseClause when elseClause.Parent is IfStatementSyntax parentIf:
                {
                    var c = semanticModel.GetConstantValue(parentIf.Condition);
                    if (c.HasValue && c.Value is bool b && b)
                    {
                        return true;
                    }
                    break;
                }
                case IfStatementSyntax ifStmt when node.SpanStart >= ifStmt.Statement.SpanStart && node.SpanStart <= ifStmt.Statement.Span.End:
                {
                    var c = semanticModel.GetConstantValue(ifStmt.Condition);
                    if (c.HasValue && c.Value is bool b && !b)
                    {
                        return true;
                    }
                    break;
                }
                case ConditionalExpressionSyntax cond:
                {
                    var c = semanticModel.GetConstantValue(cond.Condition);
                    if (c.HasValue && c.Value is bool b)
                    {
                        var inTrue = node.SpanStart >= cond.WhenTrue.SpanStart && node.SpanStart <= cond.WhenTrue.Span.End;
                        if ((inTrue && !b) || (!inTrue && b))
                        {
                            return true;
                        }
                    }
                    break;
                }
            }
        }
        return false;
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
        Func<SyntaxTree, SemanticModel> getSemanticModel,
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
                var reg = TryExtractFromServiceDescriptorFactory(factoryInv, invocation, projectName, semanticModel, getSemanticModel, rootDir);
                if (reg is not null)
                {
                    return reg;
                }
            }
            else if (arg.Expression is ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax)
            {
                var reg = TryExtractFromServiceDescriptorCtor(arg.Expression, invocation, projectName, semanticModel, getSemanticModel, rootDir);
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
        Func<SyntaxTree, SemanticModel> getSemanticModel,
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
        var factoryDeps = ExtractFactoryLambdaDependencies(factoryInv, semanticModel, getSemanticModel);
        var isFactoryOrInstance = IsFactoryOrInstanceRegistration(factoryInv, factoryName, typeArgs.Count, semanticModel) ||
                                  HasFactoryOrInstanceDescriptorArgument(factoryInv.ArgumentList.Arguments, factoryName, typeArgs.Count, semanticModel);
        var reg = BuildRegistration(outerInvocation, serviceType, implType, lifetime, serviceKey, projectName, semanticModel, rootDir);
        return reg with
        {
            FactoryDependencyTypeIds = factoryDeps.Count > 0 ? factoryDeps : null,
            IsFactoryOrInstance = isFactoryOrInstance
        };
    }

    private static bool HasFactoryOrInstanceDescriptorArgument(
        SeparatedSyntaxList<ArgumentSyntax> args,
        string factoryName,
        int genericTypeArgCount,
        SemanticModel semanticModel)
    {
        if (args.Any(a => a.Expression is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax))
        {
            return true;
        }
        if (args.Any(a => a.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax &&
                          semanticModel.GetSymbolInfo(a.Expression).Symbol is IMethodSymbol))
        {
            return true;
        }

        var isKeyed = factoryName.Contains("Keyed", StringComparison.Ordinal);
        var startIdx = 0;
        if (genericTypeArgCount == 0 && args.Count > 0 && args[0].Expression is TypeOfExpressionSyntax) startIdx++;
        if (isKeyed && startIdx < args.Count) startIdx++;

        for (var i = startIdx; i < args.Count; i++)
        {
            var expr = args[i].Expression;
            if (expr is TypeOfExpressionSyntax) continue;
            var exprType = semanticModel.GetTypeInfo(expr).Type;
            if (exprType is null) continue;
            if (exprType.TypeKind == Microsoft.CodeAnalysis.TypeKind.Delegate)
            {
                return true;
            }
            var fullName = exprType.ToDisplayString();
            if (fullName == "System.Type" || fullName.EndsWith(".ServiceLifetime", StringComparison.Ordinal))
            {
                continue;
            }
            if (factoryName.Contains("Singleton", StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    private static DiRegistration? TryExtractFromServiceDescriptorCtor(
        ExpressionSyntax creationExpr,
        InvocationExpressionSyntax outerInvocation,
        string projectName,
        SemanticModel semanticModel,
        Func<SyntaxTree, SemanticModel> getSemanticModel,
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
        var explicitLifetime = TryExtractLifetimeFromArguments(argList.Arguments, semanticModel);
        var isInstanceCtor = typeofArgs.Count == 1 && !explicitLifetime.HasValue && argList.Arguments.Count is 2 or 3;
        var factoryDeps = ExtractFactoryLambdaDependenciesFromArgs(argList.Arguments, semanticModel, getSemanticModel);
        var isFactoryCtor = argList.Arguments.Any(a =>
            a.Expression is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax ||
            (a.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax &&
             semanticModel.GetSymbolInfo(a.Expression).Symbol is IMethodSymbol) ||
            semanticModel.GetTypeInfo(a.Expression).Type?.TypeKind == Microsoft.CodeAnalysis.TypeKind.Delegate);

        INamedTypeSymbol? implType;
        if (typeofArgs.Count >= 2)
        {
            implType = typeofArgs[1];
        }
        else
        {
            // Inspect ServiceDescriptor's own arguments (not outerInvocation, whose arg is ServiceDescriptor itself)
            var isKeyedCtor = argList.Arguments.Count >= 4 || (argList.Arguments.Count == 3 && !explicitLifetime.HasValue);
            var implArgIdx = isKeyedCtor ? 2 : 1;
            INamedTypeSymbol? argImpl = null;
            if (implArgIdx < argList.Arguments.Count)
            {
                var implExpr = argList.Arguments[implArgIdx].Expression;
                argImpl = implExpr switch
                {
                    LambdaExpressionSyntax lam => TryGetLambdaReturnedType(lam, semanticModel),
                    IdentifierNameSyntax or MemberAccessExpressionSyntax when semanticModel.GetSymbolInfo(implExpr).Symbol is IMethodSymbol mgSym =>
                        mgSym.ReturnType as INamedTypeSymbol,
                    _ => semanticModel.GetTypeInfo(implExpr).Type as INamedTypeSymbol
                };
            }
            implType = argImpl is not null && argImpl.SpecialType == SpecialType.None && argImpl.Name != "ServiceDescriptor" && argImpl.TypeKind != Microsoft.CodeAnalysis.TypeKind.Delegate
                ? argImpl
                : serviceType;
        }

        // Two-arg `new ServiceDescriptor(typeof(T), instance)` and three-arg keyed `new ServiceDescriptor(typeof(T), key, instance)`
        // always register a Singleton instance descriptor.
        var lifetime = explicitLifetime ?? (isInstanceCtor ? ServiceLifetimeKind.Singleton : ServiceLifetimeKind.Transient);

        // Keyed ServiceDescriptor ctors have signature (Type serviceType, object? serviceKey, Type/Func/object impl[, ServiceLifetime])
        string? serviceKey = null;
        if ((argList.Arguments.Count >= 4 || (argList.Arguments.Count == 3 && !explicitLifetime.HasValue)) &&
            argList.Arguments[0].Expression is TypeOfExpressionSyntax)
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

        var reg = BuildRegistration(outerInvocation, serviceType, implType, lifetime, serviceKey, projectName, semanticModel, rootDir);
        return (isInstanceCtor || isFactoryCtor)
            ? reg with
            {
                IsFactoryOrInstance = true,
                FactoryDependencyTypeIds = factoryDeps.Count > 0 ? factoryDeps : null
            }
            : reg;
    }

    private static ServiceLifetimeKind? TryExtractLifetimeFromArguments(
        SeparatedSyntaxList<ArgumentSyntax> args,
        SemanticModel semanticModel)
    {
        foreach (var arg in args)
        {
            var exprType = semanticModel.GetTypeInfo(arg.Expression).Type;
            if (exprType?.Name == "ServiceLifetime")
            {
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

            // Syntactic fallback only when expression explicitly references ServiceLifetime.<Member> (never bare integer keys!)
            var text = arg.Expression.ToString().Trim();
            if (text.EndsWith("ServiceLifetime.Singleton", StringComparison.Ordinal))
                return ServiceLifetimeKind.Singleton;
            if (text.EndsWith("ServiceLifetime.Scoped", StringComparison.Ordinal))
                return ServiceLifetimeKind.Scoped;
            if (text.EndsWith("ServiceLifetime.Transient", StringComparison.Ordinal))
                return ServiceLifetimeKind.Transient;
        }
        return null;
    }

    private static string? GetCanonicalRegisteredByTypeId(
        InvocationExpressionSyntax invocation,
        SemanticModel semanticModel,
        string projectName)
    {
        var enclosing = semanticModel.GetEnclosingSymbol(invocation.SpanStart);
        var containingType = enclosing as INamedTypeSymbol ?? enclosing?.ContainingType;
        if (containingType is null)
        {
            if (invocation.Ancestors().OfType<GlobalStatementSyntax>().Any() && !string.IsNullOrWhiteSpace(projectName))
            {
                return $"{projectName}.Program";
            }
            return null;
        }

        // Synthesized top-level Program.cs class has canonical graph node ID "{projectName}.Program".
        if (containingType.ContainingNamespace is { IsGlobalNamespace: true } &&
            (containingType.Name is "Program" or "<Program>$") &&
            (containingType.IsImplicitlyDeclared ||
             containingType.GetMembers("<Main>$").Length > 0 ||
             containingType.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is CompilationUnitSyntax)) &&
            !string.IsNullOrWhiteSpace(projectName))
        {
            return $"{projectName}.Program";
        }

        if (containingType.Name.StartsWith("<", StringComparison.Ordinal))
        {
            return null;
        }

        // Match exact canonical graph node ID produced by RoslynCodeGraphExtractor (no synthetic projectName prefix for regular global-namespace types).
        return SymbolDependencyExtractor.GetTypeId(containingType);
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
        var registeredBy = GetCanonicalRegisteredByTypeId(invocation, semanticModel, projectName);

        var implTypeId = implType.TypeKind == Microsoft.CodeAnalysis.TypeKind.Delegate ||
                         (implType.IsGenericType && !implType.IsUnboundGenericType && !implType.Locations.Any(l => l.IsInSource))
            ? SymbolDependencyExtractor.GetConstructedTypeId(implType)
            : SymbolDependencyExtractor.GetTypeId(implType);

        var serviceTypeId = methodName switch
        {
            "AddHostedService" => "Microsoft.Extensions.Hosting.IHostedService",
            "AddExceptionHandler" => "Microsoft.AspNetCore.Diagnostics.IExceptionHandler",
            _ => SymbolDependencyExtractor.GetConstructedTypeId(serviceType)
        };
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
        Func<SyntaxTree, SemanticModel> getSemanticModel,
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
                var nonGenRegisteredBy = GetCanonicalRegisteredByTypeId(invocation, semanticModel, projectName);
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

        var registeredBy = GetCanonicalRegisteredByTypeId(invocation, semanticModel, projectName);
        var loc = SymbolDependencyExtractor.ToSourceLocation(invocation.GetLocation(), rootDir);
        var containerScope = TryExtractContainerScope(invocation, semanticModel);

        // EF Core AddDbContext<TContext>(optionsAction, contextLifetime, optionsLifetime) registers TContext with contextLifetime
        // and DbContextOptions<TContext> with optionsLifetime; AddDbContextPool<TContext> always registers pooled DbContextOptions<TContext> as Singleton.
        if (methodName is "AddDbContext" or "AddDbContextPool")
        {
            var (contextLifetime, optionsLifetime) = methodName == "AddDbContextPool"
                ? (ServiceLifetimeKind.Scoped, ServiceLifetimeKind.Singleton)
                : TryExtractDbContextLifetimes(invocation.ArgumentList.Arguments, semanticModel);
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
                    Lifetime: optionsLifetime,
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

        var serviceTypeId = methodName switch
        {
            "AddHostedService" => "Microsoft.Extensions.Hosting.IHostedService",
            "AddExceptionHandler" => "Microsoft.AspNetCore.Diagnostics.IExceptionHandler",
            _ => SymbolDependencyExtractor.GetConstructedTypeId(serviceType)
        };

        var serviceKey = TryExtractServiceKey(invocation, methodName, typeArgs.Count > 0, semanticModel);
        var factoryDeps = ExtractFactoryLambdaDependencies(invocation, semanticModel, getSemanticModel);
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

    private static (ServiceLifetimeKind ContextLifetime, ServiceLifetimeKind OptionsLifetime) TryExtractDbContextLifetimes(
        SeparatedSyntaxList<ArgumentSyntax> args,
        SemanticModel semanticModel)
    {
        ServiceLifetimeKind? contextLifetime = null;
        ServiceLifetimeKind? optionsLifetime = null;
        var positionalLifetimes = new List<ServiceLifetimeKind>();

        foreach (var arg in args)
        {
            var lt = TryExtractSingleLifetime(arg.Expression, semanticModel);
            if (!lt.HasValue) continue;

            var argName = arg.NameColon?.Name.Identifier.ValueText;
            if (string.Equals(argName, "contextLifetime", StringComparison.Ordinal))
            {
                contextLifetime = lt.Value;
            }
            else if (string.Equals(argName, "optionsLifetime", StringComparison.Ordinal))
            {
                optionsLifetime = lt.Value;
            }
            else
            {
                positionalLifetimes.Add(lt.Value);
            }
        }

        if (!contextLifetime.HasValue && positionalLifetimes.Count >= 1)
        {
            contextLifetime = positionalLifetimes[0];
        }
        if (!optionsLifetime.HasValue && positionalLifetimes.Count >= 2)
        {
            optionsLifetime = positionalLifetimes[1];
        }

        var effContext = contextLifetime ?? ServiceLifetimeKind.Scoped;
        var effOptions = optionsLifetime ?? effContext;
        // In EF Core, when contextLifetime is Singleton, AddDbContext promotes optionsLifetime to Singleton
        // so the singleton DbContext never captures a shorter-lived DbContextOptions<T>.
        if (effContext == ServiceLifetimeKind.Singleton)
        {
            effOptions = ServiceLifetimeKind.Singleton;
        }
        return (effContext, effOptions);
    }

    private static ServiceLifetimeKind? TryExtractSingleLifetime(ExpressionSyntax expr, SemanticModel semanticModel)
    {
        var text = expr.ToString();
        if (text.EndsWith(".Singleton", StringComparison.Ordinal) || text.Equals("Singleton", StringComparison.Ordinal))
            return ServiceLifetimeKind.Singleton;
        if (text.EndsWith(".Scoped", StringComparison.Ordinal) || text.Equals("Scoped", StringComparison.Ordinal))
            return ServiceLifetimeKind.Scoped;
        if (text.EndsWith(".Transient", StringComparison.Ordinal) || text.Equals("Transient", StringComparison.Ordinal))
            return ServiceLifetimeKind.Transient;

        var exprType = semanticModel.GetTypeInfo(expr).Type;
        if (exprType?.Name == "ServiceLifetime")
        {
            var c = semanticModel.GetConstantValue(expr);
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

        // Any lambda argument (e.g. sp => new Impl(...)) or method-group factory delegate is a factory registration
        if (args.Any(a => a.Expression is LambdaExpressionSyntax or AnonymousMethodExpressionSyntax))
        {
            return true;
        }
        if (args.Any(a => a.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax &&
                          semanticModel.GetSymbolInfo(a.Expression).Symbol is IMethodSymbol))
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
        SemanticModel semanticModel,
        Func<SyntaxTree, SemanticModel> getSemanticModel) =>
        ExtractFactoryLambdaDependenciesFromArgs(invocation.ArgumentList.Arguments, semanticModel, getSemanticModel);

    private static List<string> ExtractFactoryLambdaDependenciesFromArgs(
        SeparatedSyntaxList<ArgumentSyntax> arguments,
        SemanticModel semanticModel,
        Func<SyntaxTree, SemanticModel> getSemanticModel)
    {
        var deps = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var visitedMethods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);

        foreach (var arg in arguments)
        {
            if (arg.Expression is LambdaExpressionSyntax lambda)
            {
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

                ScanFactoryBody(lambda.Body, rootProviderParamSymbols, rootProviderParamNames, semanticModel, getSemanticModel, deps, seen, visitedMethods, depth: 0);
            }
            else if (arg.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax)
            {
                var symInfo = semanticModel.GetSymbolInfo(arg.Expression);
                var methodSym = symInfo.Symbol as IMethodSymbol
                    ?? symInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                if (methodSym is not null && methodSym.Parameters.Length > 0 && IsServiceProviderType(methodSym.Parameters[0].Type))
                {
                    ScanFactoryHelperMethod(methodSym, providerParamIndex: 0, getSemanticModel, deps, seen, visitedMethods, depth: 0);
                }
            }
        }

        return deps;
    }

    private static void ScanFactoryHelperMethod(
        IMethodSymbol helperMethod,
        int providerParamIndex,
        Func<SyntaxTree, SemanticModel> getSemanticModel,
        List<string> deps,
        HashSet<string> seen,
        HashSet<IMethodSymbol> visitedMethods,
        int depth)
    {
        if (depth > 3) return;
        var target = (helperMethod.ReducedFrom ?? helperMethod).OriginalDefinition;
        if (providerParamIndex < 0 || providerParamIndex >= target.Parameters.Length) return;
        if (target.DeclaringSyntaxReferences.Length == 0 || !visitedMethods.Add(target)) return;

        try
        {
            foreach (var syntaxRef in target.DeclaringSyntaxReferences)
            {
                var syntax = syntaxRef.GetSyntax();
                SyntaxNode? body = syntax switch
                {
                    MethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
                    LocalFunctionStatementSyntax lf => (SyntaxNode?)lf.Body ?? lf.ExpressionBody,
                    _ => null
                };
                if (body is null) continue;

                var model = getSemanticModel(syntaxRef.SyntaxTree);
                var paramSym = target.Parameters[providerParamIndex];
                var providerSymbols = new HashSet<ISymbol>(SymbolEqualityComparer.Default) { paramSym };
                var providerNames = new HashSet<string>(StringComparer.Ordinal) { paramSym.Name };
                ScanFactoryBody(body, providerSymbols, providerNames, model, getSemanticModel, deps, seen, visitedMethods, depth + 1);
            }
        }
        finally
        {
            visitedMethods.Remove(target);
        }
    }

    private static void ScanFactoryBody(
        SyntaxNode body,
        HashSet<ISymbol> rootProviderParamSymbols,
        HashSet<string> rootProviderParamNames,
        SemanticModel semanticModel,
        Func<SyntaxTree, SemanticModel> getSemanticModel,
        List<string> deps,
        HashSet<string> seen,
        HashSet<IMethodSymbol> visitedMethods,
        int depth)
    {
        bool IsRootProviderExpr(ExpressionSyntax expr)
        {
            while (expr is ParenthesizedExpressionSyntax paren) expr = paren.Expression;
            if (expr is CastExpressionSyntax cast) expr = cast.Expression;
            var recvSym = semanticModel.GetSymbolInfo(expr).Symbol;
            if (recvSym is not null)
            {
                return rootProviderParamSymbols.Contains(recvSym);
            }
            return expr is IdentifierNameSyntax recvId && rootProviderParamNames.Contains(recvId.Identifier.ValueText);
        }

        // Propagate local provider aliases (e.g. `var p = sp; p.GetRequiredService<IScoped>()`)
        var aliasAdded = true;
        while (aliasAdded)
        {
            aliasAdded = false;
            foreach (var decl in body.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                if (decl.Initializer?.Value is null) continue;
                if (!IsRootProviderExpr(decl.Initializer.Value)) continue;
                var localSym = semanticModel.GetDeclaredSymbol(decl);
                if (localSym is not null && rootProviderParamSymbols.Add(localSym))
                {
                    aliasAdded = true;
                }
                if (rootProviderParamNames.Add(decl.Identifier.ValueText))
                {
                    aliasAdded = true;
                }
            }
            foreach (var assign in body.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (assign.Left is not IdentifierNameSyntax leftId || !IsRootProviderExpr(assign.Right)) continue;
                var leftSym = semanticModel.GetSymbolInfo(leftId).Symbol;
                if (leftSym is not null && rootProviderParamSymbols.Add(leftSym))
                {
                    aliasAdded = true;
                }
                if (rootProviderParamNames.Add(leftId.Identifier.ValueText))
                {
                    aliasAdded = true;
                }
            }
        }

        foreach (var innerInv in body.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>())
        {
            var name = GetInvokedMethodName(innerInv);
            if (name is null) continue;

            if (name is "GetService" or "GetRequiredService" or "GetServices"
                or "GetKeyedService" or "GetRequiredKeyedService" or "GetKeyedServices")
            {
                // Only treat resolutions against the factory's root IServiceProvider (or its local aliases) as captured;
                // resolutions via an explicit scope (`using var scope = sp.CreateScope(); scope.ServiceProvider.GetRequiredService<T>()`) are scope-safe.
                if (innerInv.Expression is MemberAccessExpressionSyntax ma && !IsRootProviderExpr(ma.Expression))
                {
                    continue;
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
                continue;
            }

            // ActivatorUtilities.CreateInstance<T>(sp, ...) / CreateInstance(sp, typeof(T), ...) resolves T's constructor via root sp
            if (name == "CreateInstance")
            {
                var symInfo = semanticModel.GetSymbolInfo(innerInv);
                var sym = symInfo.Symbol as IMethodSymbol
                    ?? symInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                var receiverText = innerInv.Expression is MemberAccessExpressionSyntax ma ? ma.Expression.ToString() : string.Empty;
                var isActivator = sym?.ContainingType?.Name == "ActivatorUtilities" ||
                                  receiverText.EndsWith("ActivatorUtilities", StringComparison.Ordinal);
                if (isActivator && innerInv.ArgumentList.Arguments.Any(a => IsRootProviderExpr(a.Expression)))
                {
                    INamedTypeSymbol? createdType = null;
                    var typeArgs = GetGenericTypeArguments(innerInv, sym, semanticModel);
                    if (typeArgs.Count >= 1)
                    {
                        createdType = typeArgs[0];
                    }
                    else
                    {
                        var typeofArg = innerInv.ArgumentList.Arguments
                            .Select(a => a.Expression as TypeOfExpressionSyntax)
                            .FirstOrDefault(t => t is not null);
                        if (typeofArg is not null)
                        {
                            createdType = semanticModel.GetTypeInfo(typeofArg.Type).Type as INamedTypeSymbol;
                        }
                    }

                    if (createdType is not null)
                    {
                        var entry = $"activate:{SymbolDependencyExtractor.GetTypeId(createdType)}";
                        if (seen.Add(entry))
                        {
                            deps.Add(entry);
                        }
                    }
                }
                continue;
            }

            // Follow same-compilation helper method calls that receive the root IServiceProvider (e.g. sp => CreateViaHelper(sp))
            if (depth < 3 && name is not ("CreateScope" or "CreateAsyncScope"))
            {
                var receiverIsRoot = innerInv.Expression is MemberAccessExpressionSyntax extMa && IsRootProviderExpr(extMa.Expression);
                var matchingArgIdx = -1;
                for (var a = 0; a < innerInv.ArgumentList.Arguments.Count; a++)
                {
                    if (IsRootProviderExpr(innerInv.ArgumentList.Arguments[a].Expression))
                    {
                        matchingArgIdx = a;
                        break;
                    }
                }

                if (receiverIsRoot || matchingArgIdx >= 0)
                {
                    var symInfo = semanticModel.GetSymbolInfo(innerInv);
                    var sym = symInfo.Symbol as IMethodSymbol
                        ?? symInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                    if (sym is not null)
                    {
                        var targetParamIdx = sym.ReducedFrom is not null
                            ? (receiverIsRoot ? 0 : matchingArgIdx + 1)
                            : matchingArgIdx;
                        if (targetParamIdx >= 0)
                        {
                            ScanFactoryHelperMethod(sym, targetParamIdx, getSemanticModel, deps, seen, visitedMethods, depth);
                        }
                    }
                }
            }
        }
    }

    private static bool IsServiceProviderType(ITypeSymbol type) =>
        type.Name is "IServiceProvider" or "IKeyedServiceProvider" or "ServiceProvider" ||
        type.AllInterfaces.Any(i => i.Name == "IServiceProvider");

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
                    if (semanticModel.GetSymbolInfo(expr).Symbol is IMethodSymbol mgMethod)
                    {
                        var concreteFromBody = TryGetMethodReturnedConcreteType(mgMethod, semanticModel.Compilation);
                        if (concreteFromBody is not null &&
                            (serviceType is null || methodName == "AddHostedService" || IsCompatibleImplementation(concreteFromBody, serviceType)))
                        {
                            return concreteFromBody;
                        }
                        if (mgMethod.ReturnType is INamedTypeSymbol mgRetType &&
                            (serviceType is null || methodName == "AddHostedService" || IsCompatibleImplementation(mgRetType, serviceType)))
                        {
                            return mgRetType;
                        }
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

    private static INamedTypeSymbol? TryGetMethodReturnedConcreteType(IMethodSymbol method, Compilation compilation)
    {
        var target = (method.ReducedFrom ?? method).OriginalDefinition;
        foreach (var syntaxRef in target.DeclaringSyntaxReferences)
        {
            if (!compilation.ContainsSyntaxTree(syntaxRef.SyntaxTree)) continue;
            var model = compilation.GetSemanticModel(syntaxRef.SyntaxTree);
            var syntax = syntaxRef.GetSyntax();
            SyntaxNode? body = syntax switch
            {
                MethodDeclarationSyntax m => (SyntaxNode?)m.ExpressionBody?.Expression ?? m.Body,
                LocalFunctionStatementSyntax lf => (SyntaxNode?)lf.ExpressionBody?.Expression ?? lf.Body,
                _ => null
            };
            if (body is null) continue;

            if (body is ExpressionSyntax exprBody)
            {
                var t = model.GetTypeInfo(exprBody).Type as INamedTypeSymbol;
                if (t is not null && !t.IsAbstract && t.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class)
                {
                    return t;
                }
            }
            else
            {
                foreach (var ret in body.DescendantNodes().OfType<ReturnStatementSyntax>())
                {
                    if (ret.Expression is null) continue;
                    var t = model.GetTypeInfo(ret.Expression).Type as INamedTypeSymbol;
                    if (t is not null && !t.IsAbstract && t.TypeKind == Microsoft.CodeAnalysis.TypeKind.Class)
                    {
                        return t;
                    }
                }
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
