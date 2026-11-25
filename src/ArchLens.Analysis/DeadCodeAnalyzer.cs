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
        IEnumerable<IEntryPointDetector>? overrideDetectors = null,
        bool? testsExcluded = null,
        string? profile = null,
        IReadOnlyList<DeadCodeAcknowledgement>? acknowledged = null)
    {
        var effectiveScope = scope == NodeKind.Method ? NodeKind.Method : NodeKind.Type;
        var (effectiveProfile, resolvedTreatPublicApi, includeDiRegistrations) =
            ResolveProfile(graph, profile, treatPublicApiAsEntryPoint);

        var detectors = overrideDetectors?.ToList()
            ?? [.. EntryPointDetectors.CreateBuiltIn(resolvedTreatPublicApi, includeDiRegistrations), .. _customDetectors];

        var nodes = graph.GetNodes(effectiveScope).ToList();
        var adjacency = graph.BuildAdjacency(effectiveScope);

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
                entryPoints.Add(node.Id);
            }
        }

        var reachable = RunReachabilityBfs(entryPoints, adjacency);

        var incomingCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, targets) in adjacency)
        {
            foreach (var target in targets)
            {
                incomingCounts[target] = incomingCounts.GetValueOrDefault(target, 0) + 1;
            }
        }

        var candidates = new List<DeadCodeCandidate>();
        var matchedAcks = new List<DeadCodeAcknowledgement>();
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

            var ack = FindMatchingAcknowledgement(node, acknowledged);
            if (ack is not null)
            {
                matchedAcks.Add(new DeadCodeAcknowledgement(node.FullName, ack.Reason));
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
        var excludedTestsCount = graph.ExcludedTestProjectCount;
        var refsFromTestsExcluded = testsExcluded ?? (excludedTestsCount > 0 ? true : (bool?)null);
        string? note = null;
        if (refsFromTestsExcluded == true && (excludedTestsCount > 0 || ordered.Count > 0))
        {
            note = excludedTestsCount > 0
                ? $"{excludedTestsCount} test project(s) excluded; symbols referenced only by tests may appear unreferenced. Pass includeTests: true to include test references."
                : "Test projects are excluded by default (includeTests: false); symbols referenced only by tests may appear unreferenced.";
        }

        return new DeadCodeReport(
            Scope: effectiveScope,
            TotalEvaluated: nodes.Count,
            EntryPointCount: entryPoints.Count,
            UnreferencedCount: ordered.Count,
            Truncated: ordered.Count > clampedMax,
            ActiveDetectors: detectors.Select(d => d.Name).Distinct(StringComparer.Ordinal).ToList(),
            Candidates: ordered.Take(clampedMax).ToList(),
            ReferencesFromTestsExcluded: refsFromTestsExcluded,
            ExcludedTestProjectCount: excludedTestsCount > 0 ? excludedTestsCount : null,
            Note: note,
            Profile: effectiveProfile,
            AcknowledgedCount: matchedAcks.Count > 0 ? matchedAcks.Count : null,
            Acknowledged: matchedAcks.Count > 0 ? matchedAcks.OrderBy(a => a.Symbol, StringComparer.Ordinal).ToList() : null);
    }

    private static DeadCodeAcknowledgement? FindMatchingAcknowledgement(
        CodeNode node,
        IReadOnlyList<DeadCodeAcknowledgement>? acknowledged)
    {
        if (acknowledged is null || acknowledged.Count == 0)
        {
            return null;
        }

        foreach (var ack in acknowledged)
        {
            if (string.IsNullOrWhiteSpace(ack.Symbol)) continue;
            if (string.Equals(node.Id, ack.Symbol, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(node.FullName, ack.Symbol, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(node.Name, ack.Symbol, StringComparison.OrdinalIgnoreCase) ||
                GlobMatcher.IsMatch(node.FullName, ack.Symbol))
            {
                return ack;
            }
        }

        return null;
    }

    private static (string EffectiveProfile, bool TreatPublicApiAsEntryPoint, bool IncludeDiRegistrations) ResolveProfile(
        CodeGraph graph,
        string? profile,
        bool treatPublicApiFallback)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return (treatPublicApiFallback ? "lib" : "app", treatPublicApiFallback, true);
        }

        var normalized = profile.Trim().ToLowerInvariant();
        if (normalized == "app")
        {
            return ("app", false, true);
        }

        if (normalized == "lib" || normalized == "library")
        {
            return ("lib", true, true);
        }

        if (normalized == "strict")
        {
            return ("strict", false, false);
        }

        // "auto": if the solution has an executable entry project (ConsoleApplication / WindowsApplication)
        // alongside library layers (or any executable project), treat it as an application ("app") so internal
        // public layer types are audited rather than blindly exempted as public NuGet library APIs.
        var hasExecutableProject = graph.GetNodes(NodeKind.Project).Any(p =>
            p.Metadata is not null &&
            p.Metadata.TryGetValue("outputKind", out var ok) &&
            (ok.Equals("ConsoleApplication", StringComparison.OrdinalIgnoreCase) ||
             ok.Equals("WindowsApplication", StringComparison.OrdinalIgnoreCase)));

        if (!hasExecutableProject)
        {
            hasExecutableProject = graph.GetNodes(NodeKind.Type).Any(t =>
                EntryPointDetectors.HasMetadataFlag(t, "isEntryPointMain") ||
                (t.Metadata is not null &&
                 t.Metadata.TryGetValue("outputKind", out var ok) &&
                 (ok.Equals("ConsoleApplication", StringComparison.OrdinalIgnoreCase) ||
                  ok.Equals("WindowsApplication", StringComparison.OrdinalIgnoreCase))));
        }

        return hasExecutableProject
            ? ("app", false, true)
            : ("lib", true, true);
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
