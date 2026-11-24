using ArchLens.Model;

namespace ArchLens.Analysis;

/// <summary>
/// Analyzes Microsoft.Extensions.DependencyInjection registrations to detect captive dependencies
/// (direct or transitive Scoped dependencies captured by Singletons) and single-implementation interfaces.
/// </summary>
public static class DiGraphAnalyzer
{
    private static readonly HashSet<string> ScopeSafeServiceTypes = new(StringComparer.Ordinal)
    {
        "System.IServiceProvider",
        "Microsoft.Extensions.DependencyInjection.IServiceScopeFactory",
        "Microsoft.Extensions.DependencyInjection.IServiceProviderFactory"
    };

    public static DiAnalysisReport Analyze(CodeGraph graph)
    {
        var registrations = graph.DiRegistrations;
        var captiveIssues = DetectCaptiveDependencies(graph, registrations);
        var singleImpls = DetectSingleImplementationInterfaces(graph, registrations);

        return new DiAnalysisReport(
            RegistrationCount: registrations.Count,
            CaptiveDependencyCount: captiveIssues.Count,
            SingleImplementationCount: singleImpls.Count,
            Registrations: registrations,
            CaptiveDependencies: captiveIssues,
            SingleImplementationInterfaces: singleImpls);
    }

    private static List<CaptiveDependencyIssue> DetectCaptiveDependencies(
        CodeGraph graph,
        IReadOnlyList<DiRegistration> registrations)
    {
        if (registrations.Count == 0)
        {
            return [];
        }

        var regsByService = new Dictionary<string, List<DiRegistration>>(StringComparer.Ordinal);
        var openGenericRegs = new Dictionary<(string BaseName, int Arity), List<DiRegistration>>();

        foreach (var reg in registrations)
        {
            AddToBucket(regsByService, reg.ServiceTypeId, reg);
            if (TryParseOpenGenericSignature(reg.ServiceTypeId, out var openKey))
            {
                AddToBucket(openGenericRegs, openKey, reg);
            }
        }

        var issues = new List<CaptiveDependencyIssue>();
        var seenIssues = new HashSet<(string Consumer, string Dep, string Impl)>();

        foreach (var rootReg in registrations.Where(r => r.Lifetime == ServiceLifetimeKind.Singleton))
        {
            var consumerImplId = rootReg.ImplementationTypeId;
            var visitedImpls = new HashSet<string>(StringComparer.Ordinal) { consumerImplId };
            var queue = new Queue<(string CurrentImplId, string Path)>();
            queue.Enqueue((consumerImplId, $"{consumerImplId} (Singleton)"));

            while (queue.Count > 0)
            {
                var (currentImpl, currentPath) = queue.Dequeue();
                foreach (var (paramTypeId, isCollection) in GetConstructorDependencies(graph, currentImpl))
                {
                    if (ScopeSafeServiceTypes.Contains(paramTypeId))
                    {
                        continue;
                    }

                    var targetRegs = ResolveRegistrations(paramTypeId, isCollection, regsByService, openGenericRegs);
                    foreach (var targetReg in targetRegs)
                    {
                        if (targetReg.Lifetime == ServiceLifetimeKind.Scoped)
                        {
                            if (seenIssues.Add((consumerImplId, paramTypeId, targetReg.ImplementationTypeId)))
                            {
                                var consumerNode = graph.FindNode(consumerImplId);
                                var fullPath = $"{currentPath} -> {paramTypeId} => {targetReg.ImplementationTypeId} (Scoped)";
                                issues.Add(new CaptiveDependencyIssue(
                                    ConsumerTypeId: consumerImplId,
                                    ConsumerLifetime: ServiceLifetimeKind.Singleton,
                                    DependencyTypeId: paramTypeId,
                                    DependencyLifetime: ServiceLifetimeKind.Scoped,
                                    ResolvedImplementationTypeId: targetReg.ImplementationTypeId,
                                    DependencyPath: fullPath,
                                    Location: consumerNode?.Location ?? rootReg.Location));
                            }
                        }
                        else if (targetReg.Lifetime == ServiceLifetimeKind.Transient &&
                                 visitedImpls.Add(targetReg.ImplementationTypeId))
                        {
                            var nextPath = $"{currentPath} -> {paramTypeId} => {targetReg.ImplementationTypeId} (Transient)";
                            queue.Enqueue((targetReg.ImplementationTypeId, nextPath));
                        }
                    }
                }
            }
        }

        return issues
            .OrderBy(i => i.ConsumerTypeId, StringComparer.Ordinal)
            .ThenBy(i => i.DependencyTypeId, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<DiRegistration> ResolveRegistrations(
        string paramTypeId,
        bool isCollection,
        Dictionary<string, List<DiRegistration>> regsByService,
        Dictionary<(string BaseName, int Arity), List<DiRegistration>> openGenericRegs)
    {
        if (regsByService.TryGetValue(paramTypeId, out var exact))
        {
            return isCollection ? exact : [exact[^1]];
        }

        // Fall back to open generic registration (e.g. typeof(IRepository<>) for IRepository<Order>)
        if (TryParseGenericSignature(paramTypeId, out var genericKey) &&
            openGenericRegs.TryGetValue(genericKey, out var openMatches))
        {
            return isCollection ? openMatches : [openMatches[^1]];
        }

        return [];
    }

    private static IEnumerable<(string TypeId, bool IsCollection)> GetConstructorDependencies(CodeGraph graph, string typeId)
    {
        var node = graph.FindNode(typeId);
        if (node?.Metadata is not null && node.Metadata.TryGetValue("ctorParamTypes", out var raw))
        {
            // Key is present (even if empty "") -> exact Roslyn constructor metadata; never fall back to method parameters
            if (string.IsNullOrWhiteSpace(raw))
            {
                return [];
            }

            var delimiter = raw.Contains(';', StringComparison.Ordinal) || raw.Contains('<', StringComparison.Ordinal)
                ? ';'
                : ',';

            return raw.Split(delimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(entry => entry.StartsWith("many:", StringComparison.Ordinal)
                    ? (entry[5..], true)
                    : (entry, false));
        }

        // Fallback for synthetic graphs without ctorParamTypes metadata
        return graph.GetOutgoing(typeId)
            .Where(e => e.Kind == DependencyKind.Parameter)
            .Select(e => e.ToId)
            .Distinct(StringComparer.Ordinal)
            .Select(id => (id, false));
    }

    private static List<SingleImplementationInterfaceInfo> DetectSingleImplementationInterfaces(
        CodeGraph graph,
        IReadOnlyList<DiRegistration> registrations)
    {
        var interfaces = graph.GetNodes(NodeKind.Type)
            .Where(n => n.TypeKind == TypeKind.Interface)
            .ToDictionary(n => n.Id, StringComparer.Ordinal);

        if (interfaces.Count == 0)
        {
            return [];
        }

        var implsByInterface = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        void RecordImpl(string ifaceId, string concreteId)
        {
            if (!interfaces.ContainsKey(ifaceId))
            {
                return;
            }
            if (!implsByInterface.TryGetValue(ifaceId, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                implsByInterface[ifaceId] = set;
            }
            set.Add(concreteId);
        }

        // 1. Direct Implements edges
        foreach (var edge in graph.Edges)
        {
            if (edge.Kind != DependencyKind.Implements || !interfaces.ContainsKey(edge.ToId))
            {
                continue;
            }

            var fromNode = graph.FindNode(edge.FromId);
            if (fromNode is { Kind: NodeKind.Type, IsAbstract: false } && fromNode.TypeKind != TypeKind.Interface)
            {
                RecordImpl(edge.ToId, fromNode.Id);
            }
        }

        // 2. Transitive interface implementations via baseTypes metadata (e.g. Concrete : AbstractBase : IService)
        foreach (var node in graph.GetNodes(NodeKind.Type))
        {
            if (node.IsAbstract || node.TypeKind == TypeKind.Interface)
            {
                continue;
            }

            foreach (var baseTypeId in EntryPointDetectors.SplitMetadataList(node, "baseTypes"))
            {
                RecordImpl(baseTypeId, node.Id);
            }
        }

        // 3. Fallback transitive resolution via Inherits + Implements edges (for synthetic graphs without baseTypes metadata)
        var inheritsMap = graph.Edges
            .Where(e => e.Kind == DependencyKind.Inherits)
            .GroupBy(e => e.FromId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ToId).ToList(), StringComparer.Ordinal);
        var directImplementsMap = graph.Edges
            .Where(e => e.Kind == DependencyKind.Implements)
            .GroupBy(e => e.FromId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ToId).ToList(), StringComparer.Ordinal);

        foreach (var node in graph.GetNodes(NodeKind.Type))
        {
            if (node.IsAbstract || node.TypeKind == TypeKind.Interface)
            {
                continue;
            }

            var visited = new HashSet<string>(StringComparer.Ordinal) { node.Id };
            var queue = new Queue<string>();
            queue.Enqueue(node.Id);
            while (queue.Count > 0)
            {
                var curr = queue.Dequeue();
                if (directImplementsMap.TryGetValue(curr, out var ifaces))
                {
                    foreach (var ifaceId in ifaces)
                    {
                        RecordImpl(ifaceId, node.Id);
                    }
                }
                if (inheritsMap.TryGetValue(curr, out var bases))
                {
                    foreach (var b in bases)
                    {
                        if (visited.Add(b))
                        {
                            queue.Enqueue(b);
                        }
                    }
                }
            }
        }

        var diRegisteredServices = registrations
            .Select(r => r.ServiceTypeId)
            .ToHashSet(StringComparer.Ordinal);

        var results = new List<SingleImplementationInterfaceInfo>();
        foreach (var (ifaceId, implSet) in implsByInterface)
        {
            if (implSet.Count != 1)
            {
                continue;
            }

            var implId = implSet.First();
            var consumerCount = graph.GetIncoming(ifaceId)
                .Where(e => e.Kind != DependencyKind.Implements && e.Kind != DependencyKind.ProjectReference)
                .Select(e => e.FromId)
                .Where(from => !string.Equals(from, implId, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Count();

            var isRegistered = diRegisteredServices.Contains(ifaceId) ||
                (TryParseGenericSignature(ifaceId, out var ifaceGen) &&
                 diRegisteredServices.Any(s => TryParseGenericSignature(s, out var sg) && sg == ifaceGen));

            results.Add(new SingleImplementationInterfaceInfo(
                InterfaceTypeId: ifaceId,
                ImplementationTypeId: implId,
                IsDiRegistered: isRegistered,
                ConsumerCount: consumerCount,
                Location: interfaces[ifaceId].Location));
        }

        return results
            .OrderByDescending(r => r.ConsumerCount)
            .ThenBy(r => r.InterfaceTypeId, StringComparer.Ordinal)
            .ToList();
    }

    private static void AddToBucket<TKey>(Dictionary<TKey, List<DiRegistration>> map, TKey key, DiRegistration reg) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
        {
            list = [];
            map[key] = list;
        }
        list.Add(reg);
    }

    private static bool TryParseOpenGenericSignature(string typeId, out (string BaseName, int Arity) key)
    {
        if (!TryParseGenericSignature(typeId, out key))
        {
            return false;
        }

        // Open generic parameters look like simple identifiers (T, TKey, TValue) or empty (<,>) — no dots
        var lt = typeId.IndexOf('<');
        var gt = typeId.LastIndexOf('>');
        var inner = typeId[(lt + 1)..gt];
        var args = SplitTopLevelGenericArgs(inner);
        return args.All(a => string.IsNullOrEmpty(a) || !a.Contains('.', StringComparison.Ordinal));
    }

    private static bool TryParseGenericSignature(string typeId, out (string BaseName, int Arity) key)
    {
        var lt = typeId.IndexOf('<');
        var gt = typeId.LastIndexOf('>');
        if (lt > 0 && gt > lt)
        {
            var baseName = typeId[..lt];
            var inner = typeId[(lt + 1)..gt];
            var arity = SplitTopLevelGenericArgs(inner).Count;
            key = (baseName, arity);
            return true;
        }

        key = default;
        return false;
    }

    private static List<string> SplitTopLevelGenericArgs(string inner)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '<') depth++;
            else if (inner[i] == '>') depth--;
            else if (inner[i] == ',' && depth == 0)
            {
                result.Add(inner[start..i].Trim());
                start = i + 1;
            }
        }
        result.Add(inner[start..].Trim());
        return result;
    }
}
