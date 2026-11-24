using ArchLens.Model;

namespace ArchLens.Analysis;

/// <summary>
/// Detects dead code and unreferenced symbols using reachability BFS starting from detected entry points.
/// Finds both zero-incoming-reference symbols and unreachable islands (dead cycles).
/// </summary>
public sealed class DeadCodeAnalyzer
{
    private readonly IReadOnlyList<IEntryPointDetector> _customDetectors;

    public DeadCodeAnalyzer(IEnumerable<IEntryPointDetector>? customDetectors = null)
    {
        _customDetectors = customDetectors?.ToList() ?? [];
    }

    public DeadCodeReport Analyze(
        CodeGraph graph,
        NodeKind scope = NodeKind.Type,
        bool treatPublicApiAsEntryPoint = true,
        string? filterPattern = null,
        int maxItems = 100,
        IEnumerable<IEntryPointDetector>? overrideDetectors = null)
    {
        var effectiveScope = scope == NodeKind.Method ? NodeKind.Method : NodeKind.Type;
        var detectors = overrideDetectors?.ToList()
            ?? [.. EntryPointDetectors.CreateBuiltIn(treatPublicApiAsEntryPoint), .. _customDetectors];

        var nodes = graph.GetNodes(effectiveScope).ToList();
        var adjacency = graph.BuildAdjacency(effectiveScope);

        // Compute reachable types first when analyzing methods so overrides/interface impls on live types are preserved
        HashSet<string>? reachableTypes = null;
        if (effectiveScope == NodeKind.Method)
        {
            reachableTypes = ComputeReachableSet(graph, NodeKind.Type, detectors);
        }

        var entryPoints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (IsDetectedEntryPoint(detectors, node, graph))
            {
                entryPoints.Add(node.Id);
            }
            else if (effectiveScope == NodeKind.Method &&
                     node.ContainingTypeId is not null &&
                     reachableTypes!.Contains(node.ContainingTypeId) &&
                     (EntryPointDetectors.HasMetadataFlag(node, "isOverride") ||
                      EntryPointDetectors.HasMetadataFlag(node, "implementsInterface") ||
                      EntryPointDetectors.HasMetadataFlag(node, "calledFromMemberBody") ||
                      node.Metadata?.GetValueOrDefault("methodKind") == "StaticConstructor"))
            {
                // Polymorphic overrides, interface implementations, property/initializer helpers, and static ctors on reachable types are live
                entryPoints.Add(node.Id);
            }
        }

        var reachable = RunReachabilityBfs(entryPoints, adjacency);

        // Compute incoming counts for clear reason reporting (0 refs vs unreachable island)
        var incomingCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, targets) in adjacency)
        {
            foreach (var target in targets)
            {
                incomingCounts[target] = incomingCounts.GetValueOrDefault(target, 0) + 1;
            }
        }

        var candidates = new List<DeadCodeCandidate>();
        foreach (var node in nodes)
        {
            if (reachable.Contains(node.Id))
            {
                continue;
            }

            if (!GlobMatcher.IsMatch(node.FullName, filterPattern))
            {
                continue;
            }

            var incoming = incomingCounts.GetValueOrDefault(node.Id, 0);
            var reason = incoming == 0
                ? "Unreferenced (0 incoming references and not a detected entry point)."
                : $"Unreachable island (referenced only by {incoming} other unreachable symbol(s)).";

            candidates.Add(new DeadCodeCandidate(
                NodeId: node.Id,
                FullName: node.FullName,
                Kind: node.Kind,
                ProjectName: node.ProjectName,
                Accessibility: node.Accessibility,
                Reason: reason,
                Location: node.Location));
        }

        var ordered = candidates
            .OrderBy(c => c.ProjectName, StringComparer.Ordinal)
            .ThenBy(c => c.FullName, StringComparer.Ordinal)
            .ToList();

        var clampedMax = Math.Max(1, maxItems);
        return new DeadCodeReport(
            Scope: effectiveScope,
            TotalEvaluated: nodes.Count,
            EntryPointCount: entryPoints.Count,
            UnreferencedCount: ordered.Count,
            Truncated: ordered.Count > clampedMax,
            ActiveDetectors: detectors.Select(d => d.Name).Distinct(StringComparer.Ordinal).ToList(),
            Candidates: ordered.Take(clampedMax).ToList());
    }

    private static HashSet<string> ComputeReachableSet(
        CodeGraph graph,
        NodeKind scope,
        IReadOnlyList<IEntryPointDetector> detectors)
    {
        var nodes = graph.GetNodes(scope).ToList();
        var adjacency = graph.BuildAdjacency(scope);
        var seeds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var node in nodes)
        {
            if (IsDetectedEntryPoint(detectors, node, graph))
            {
                seeds.Add(node.Id);
            }
        }

        return RunReachabilityBfs(seeds, adjacency);
    }

    private static bool IsDetectedEntryPoint(
        IReadOnlyList<IEntryPointDetector> detectors,
        CodeNode node,
        CodeGraph graph)
    {
        foreach (var detector in detectors)
        {
            try
            {
                if (detector.IsEntryPoint(node, graph))
                {
                    return true;
                }
            }
            catch
            {
                // Isolate buggy plugin entry-point detectors so built-in analysis continues.
            }
        }

        return false;
    }

    private static HashSet<string> RunReachabilityBfs(
        HashSet<string> seeds,
        Dictionary<string, HashSet<string>> adjacency)
    {
        var visited = new HashSet<string>(seeds, StringComparer.Ordinal);
        var queue = new Queue<string>(seeds);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!adjacency.TryGetValue(current, out var neighbors))
            {
                continue;
            }

            foreach (var next in neighbors)
            {
                if (visited.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        return visited;
    }
}
