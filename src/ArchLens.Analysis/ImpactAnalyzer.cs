using ArchLens.Model;

namespace ArchLens.Analysis;

/// <summary>
/// Computes the transitive "blast radius" (dependents) of modifying a given type.
/// </summary>
public static class ImpactAnalyzer
{
    private static readonly HashSet<DependencyKind> StrictDependencyKinds =
    [
        DependencyKind.MethodCall,
        DependencyKind.FieldOrProperty,
        DependencyKind.Inherits,
        DependencyKind.Implements,
        DependencyKind.ObjectCreation
    ];

    public static ImpactAnalysisResult Analyze(
        CodeGraph graph,
        CodeNode targetNode,
        int maxDepth = 4,
        int maxItems = 50,
        bool excludeRegistrations = true,
        bool includeTests = false,
        bool strict = false)
    {
        var targetKind = targetNode.Kind;
        var diRegistrationPairs = BuildDiRegistrationPairs(graph);
        var diRegistrarTypes = graph.DiRegistrations
            .Where(r => !string.IsNullOrWhiteSpace(r.RegisteredByTypeId))
            .Select(r => r.RegisteredByTypeId!)
            .ToHashSet(StringComparer.Ordinal);

        // Group edges by (FromId, ToId) so we can classify each reverse dependency as consumer / registration / test
        var edgesByPair = new Dictionary<(string From, string To), HashSet<DependencyKind>>();
        foreach (var edge in graph.Edges)
        {
            if (edge.Kind == DependencyKind.ProjectReference || string.Equals(edge.FromId, edge.ToId, StringComparison.Ordinal))
            {
                continue;
            }

            if (graph.FindNode(edge.FromId)?.Kind != targetKind ||
                graph.FindNode(edge.ToId)?.Kind != targetKind)
            {
                continue;
            }

            var key = (edge.FromId, edge.ToId);
            if (!edgesByPair.TryGetValue(key, out var kinds))
            {
                kinds = [];
                edgesByPair[key] = kinds;
            }
            kinds.Add(edge.Kind);
        }

        var reverseAdj = new Dictionary<string, List<(string DependentId, string Category)>>(StringComparer.Ordinal);
        var excludedRegistrationIds = new HashSet<string>(StringComparer.Ordinal);
        var excludedTestIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ((fromId, toId), kinds) in edgesByPair)
        {
            if (strict && !kinds.Any(StrictDependencyKinds.Contains))
            {
                continue;
            }

            var fromNode = graph.FindNode(fromId);
            if (fromNode is null)
            {
                continue;
            }

            var isTestNode = IsTestOrSupportNode(fromNode);
            if (isTestNode && !includeTests)
            {
                if (string.Equals(toId, targetNode.Id, StringComparison.Ordinal))
                {
                    excludedTestIds.Add(fromId);
                }
                continue;
            }

            var isRegistrationEdge = targetKind == NodeKind.Type &&
                                     IsDiRegistrationEdge(fromNode, toId, kinds, diRegistrationPairs, diRegistrarTypes);
            if (isRegistrationEdge && excludeRegistrations)
            {
                if (string.Equals(toId, targetNode.Id, StringComparison.Ordinal))
                {
                    excludedRegistrationIds.Add(fromId);
                }
                continue;
            }

            var category = isTestNode ? "test" : isRegistrationEdge ? "registration" : "consumer";
            if (!reverseAdj.TryGetValue(toId, out var dependents))
            {
                dependents = [];
                reverseAdj[toId] = dependents;
            }
            dependents.Add((fromId, category));
        }

        var seedIds = new List<string> { targetNode.Id };
        if (targetKind == NodeKind.Method)
        {
            // Bridge implementing methods to matching interface/base methods so polymorphic callers are included in blast radius
            foreach (var contractMethodId in FindImplementedContractMethods(graph, targetNode))
            {
                seedIds.Add(contractMethodId);
            }
        }

        var visited = new Dictionary<string, (int Distance, string Category)>(StringComparer.Ordinal);
        var queue = new Queue<(string NodeId, int Depth)>();
        foreach (var seedId in seedIds)
        {
            if (visited.TryAdd(seedId, (0, "target")))
            {
                queue.Enqueue((seedId, 0));
            }
        }

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (depth >= maxDepth)
            {
                continue;
            }

            if (!reverseAdj.TryGetValue(current, out var dependents))
            {
                continue;
            }

            foreach (var (dep, category) in dependents)
            {
                if (visited.TryAdd(dep, (depth + 1, category)))
                {
                    queue.Enqueue((dep, depth + 1));
                }
            }
        }

        foreach (var seedId in seedIds)
        {
            visited.Remove(seedId);
        }

        var allImpacted = visited
            .Select(kvp => (Node: graph.FindNode(kvp.Key), kvp.Value.Distance, kvp.Value.Category))
            .Where(x => x.Node is not null)
            .Select(x => new ImpactNode(x.Node!.Id, x.Node.FullName, x.Node.ProjectName, x.Distance, x.Node.Location, x.Category))
            .OrderBy(n => n.Distance)
            .ThenBy(n => n.FullName, StringComparer.Ordinal)
            .ToList();

        var directCount = allImpacted.Count(n => n.Distance == 1);
        var affectedProjects = allImpacted
            .Select(n => n.ProjectName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var notes = new List<string>();
        if (excludedRegistrationIds.Count > 0)
        {
            notes.Add($"{excludedRegistrationIds.Count} direct DI registration edge(s) excluded (pass excludeRegistrations: false to include).");
        }
        if (excludedTestIds.Count > 0)
        {
            notes.Add($"{excludedTestIds.Count} test/test-support dependent(s) excluded (pass includeTests: true to include).");
        }

        var clamped = Math.Max(1, maxItems);
        var returned = allImpacted.Take(clamped).ToList();
        return new ImpactAnalysisResult(
            TargetNodeId: targetNode.Id,
            TargetFullName: targetNode.FullName,
            DirectDependentsCount: directCount,
            TotalTransitiveDependentsCount: allImpacted.Count,
            AffectedProjects: affectedProjects,
            ImpactedNodes: returned,
            Truncated: allImpacted.Count > returned.Count,
            ReturnedCount: returned.Count,
            ExcludedRegistrationCount: excludedRegistrationIds.Count > 0 ? excludedRegistrationIds.Count : null,
            ExcludedTestCount: excludedTestIds.Count > 0 ? excludedTestIds.Count : null,
            Note: notes.Count > 0 ? string.Join(" ", notes) : null);
    }

    private static HashSet<(string RegistrarId, string TargetId)> BuildDiRegistrationPairs(CodeGraph graph)
    {
        var pairs = new HashSet<(string, string)>();
        foreach (var reg in graph.DiRegistrations)
        {
            if (string.IsNullOrWhiteSpace(reg.RegisteredByTypeId))
            {
                continue;
            }

            pairs.Add((reg.RegisteredByTypeId, StripGenericArgs(reg.ServiceTypeId)));
            pairs.Add((reg.RegisteredByTypeId, reg.ServiceTypeId));
            pairs.Add((reg.RegisteredByTypeId, StripGenericArgs(reg.ImplementationTypeId)));
            pairs.Add((reg.RegisteredByTypeId, reg.ImplementationTypeId));
        }
        return pairs;
    }

    private static bool IsDiRegistrationEdge(
        CodeNode fromNode,
        string toId,
        HashSet<DependencyKind> kinds,
        HashSet<(string RegistrarId, string TargetId)> diPairs,
        HashSet<string> diRegistrarTypes)
    {
        var hasDirectConsumerUsage =
            kinds.Contains(DependencyKind.Inherits) ||
            kinds.Contains(DependencyKind.Implements) ||
            kinds.Contains(DependencyKind.FieldOrProperty) ||
            kinds.Contains(DependencyKind.ObjectCreation) ||
            kinds.Contains(DependencyKind.MethodCall) ||
            kinds.Contains(DependencyKind.Parameter) ||
            kinds.Contains(DependencyKind.ReturnType);

        var strippedTo = StripGenericArgs(toId);
        var isKnownPair = diPairs.Contains((fromNode.Id, toId)) || diPairs.Contains((fromNode.Id, strippedTo));
        if (isKnownPair)
        {
            // If the registrar also constructs, invokes, inherits, implements, or exposes the target, treat it as a real consumer
            return !hasDirectConsumerUsage;
        }

        // Heuristic fallback for DI composition root extension classes (e.g. *ServiceCollectionExtensions)
        if (diRegistrarTypes.Contains(fromNode.Id) &&
            fromNode.IsStatic &&
            (fromNode.Name.Contains("ServiceCollection", StringComparison.OrdinalIgnoreCase) ||
             fromNode.Name.Contains("DependencyInjection", StringComparison.OrdinalIgnoreCase)) &&
            !hasDirectConsumerUsage)
        {
            return true;
        }

        return false;
    }

    private static bool IsTestOrSupportNode(CodeNode node)
    {
        if (EntryPointDetectors.HasMetadataFlag(node, "isTestProject") ||
            EntryPointDetectors.HasMetadataFlag(node, "isTestSupportProject"))
        {
            return true;
        }

        var proj = node.ProjectName;
        return proj.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
               proj.EndsWith(".Test", StringComparison.OrdinalIgnoreCase) ||
               proj.Contains(".Tests.", StringComparison.OrdinalIgnoreCase) ||
               proj.Contains(".Test.", StringComparison.OrdinalIgnoreCase) ||
               proj.EndsWith(".TestSupport", StringComparison.OrdinalIgnoreCase) ||
               proj.EndsWith(".UnitTests", StringComparison.OrdinalIgnoreCase) ||
               proj.EndsWith(".IntegrationTests", StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> FindImplementedContractMethods(CodeGraph graph, CodeNode methodNode)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(methodNode.ContainingTypeId) ||
            graph.FindNode(methodNode.ContainingTypeId) is not { } ownerType)
        {
            return result;
        }

        var contractTypeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in graph.GetOutgoing(ownerType.Id))
        {
            if (e.Kind is DependencyKind.Implements or DependencyKind.Inherits)
            {
                contractTypeIds.Add(e.ToId);
            }
        }
        foreach (var bt in EntryPointDetectors.SplitMetadataList(ownerType, "baseTypes"))
        {
            contractTypeIds.Add(StripGenericArgs(bt));
            contractTypeIds.Add(bt);
        }

        if (contractTypeIds.Count == 0)
        {
            return result;
        }

        // Extract method signature suffix after "ContainingType." (e.g. "ApplyMove(Move)")
        var prefix = ownerType.Id + ".";
        var suffix = methodNode.Id.StartsWith(prefix, StringComparison.Ordinal)
            ? methodNode.Id[prefix.Length..]
            : null;
        if (string.IsNullOrWhiteSpace(suffix))
        {
            return result;
        }

        // Handle explicit interface implementations like "IGameEngine.ApplyMove(Move)"
        var parenIdx = suffix.IndexOf('(');
        var namePart = parenIdx > 0 ? suffix[..parenIdx] : suffix;
        var lastDot = namePart.LastIndexOf('.');
        var unqualifiedSuffix = lastDot >= 0 ? suffix[(lastDot + 1)..] : suffix;

        foreach (var contractId in contractTypeIds)
        {
            var candidateId = $"{contractId}.{unqualifiedSuffix}";
            if (!string.Equals(candidateId, methodNode.Id, StringComparison.Ordinal) &&
                graph.FindNode(candidateId) is { Kind: NodeKind.Method })
            {
                result.Add(candidateId);
            }
        }

        return result;
    }

    private static string StripGenericArgs(string typeId)
    {
        var lt = typeId.IndexOf('<');
        return lt > 0 ? typeId[..lt] : typeId;
    }
}
