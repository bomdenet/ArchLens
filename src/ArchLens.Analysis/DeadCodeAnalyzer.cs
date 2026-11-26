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
        IReadOnlyList<DeadCodeAcknowledgement>? acknowledged = null,
        bool? treatPublicApiOverride = null)
    {
        var effectiveScope = scope == NodeKind.Method ? NodeKind.Method : NodeKind.Type;
        var (effectiveProfile, resolvedTreatPublicApi, includeDiRegistrations) =
            ResolveProfile(graph, profile, treatPublicApiAsEntryPoint);
        if (treatPublicApiOverride.HasValue)
        {
            resolvedTreatPublicApi = treatPublicApiOverride.Value;
        }

        var detectors = overrideDetectors?.ToList()
            ?? [.. EntryPointDetectors.CreateBuiltIn(resolvedTreatPublicApi, includeDiRegistrations), .. _customDetectors];

        var nodes = graph.GetNodes(effectiveScope).ToList();
        var adjacency = graph.BuildAdjacency(effectiveScope);

        HashSet<string>? reachableTypes = null;
        HashSet<string>? inSolutionContractImplMethodIds = null;
        if (effectiveScope == NodeKind.Method)
        {
            reachableTypes = ComputeReachableSet(graph, NodeKind.Type, detectors);
            inSolutionContractImplMethodIds = WireInSolutionContractToImplEdges(graph, nodes, adjacency);
        }

        var entryPoints = new HashSet<string>(StringComparer.Ordinal);
        var skippedImplicitConstructors = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (IsDetectedEntryPoint(detectors, node, graph))
            {
                entryPoints.Add(node.Id);
            }
            else if (effectiveScope == NodeKind.Method &&
                     IsReachableMethodEntryPoint(node, graph, reachableTypes!, inSolutionContractImplMethodIds!))
            {
                entryPoints.Add(node.Id);
            }
            else if (effectiveScope == NodeKind.Method &&
                     EntryPointDetectors.HasMetadataFlag(node, "isCompilerSynthesizedCtor"))
            {
                skippedImplicitConstructors.Add(node.Id);
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

        // In method scope, if a concrete class implements an interface and its implementing method is actually
        // invoked (has incoming calls from reachable code), mark the corresponding interface method declaration reachable
        // so interface declarations aren't flagged dead when callers invoke through the concrete type.
        if (effectiveScope == NodeKind.Method)
        {
            var invokedImplMethodsByType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var m in nodes)
            {
                if (m.ContainingTypeId is null || !reachable.Contains(m.Id)) continue;
                if (incomingCounts.GetValueOrDefault(m.Id, 0) <= 0) continue;
                if (!EntryPointDetectors.HasMetadataFlag(m, "implementsInterface")) continue;

                var sigSuffix = ExtractMethodSignatureSuffix(m);
                if (!invokedImplMethodsByType.TryGetValue(m.ContainingTypeId, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    invokedImplMethodsByType[m.ContainingTypeId] = set;
                }
                set.Add(sigSuffix);
            }

            if (invokedImplMethodsByType.Count > 0)
            {
                var invokedInterfaceSignatures = new HashSet<(string InterfaceId, string Sig)>();
                foreach (var edge in graph.Edges)
                {
                    if (edge.Kind != DependencyKind.Implements) continue;
                    if (invokedImplMethodsByType.TryGetValue(edge.FromId, out var sigs))
                    {
                        foreach (var sig in sigs)
                        {
                            invokedInterfaceSignatures.Add((edge.ToId, sig));
                        }
                    }
                }

                if (invokedInterfaceSignatures.Count > 0)
                {
                    foreach (var m in nodes)
                    {
                        if (m.ContainingTypeId is not null &&
                            !reachable.Contains(m.Id) &&
                            invokedInterfaceSignatures.Contains((m.ContainingTypeId, ExtractMethodSignatureSuffix(m))))
                        {
                            reachable.Add(m.Id);
                        }
                    }
                }
            }
        }

        var candidates = new List<DeadCodeCandidate>();
        var matchedAcks = new List<DeadCodeAcknowledgement>();
        foreach (var node in nodes)
        {
            if (reachable.Contains(node.Id) || skippedImplicitConstructors.Contains(node.Id))
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
        var hasFilter = !string.IsNullOrWhiteSpace(filterPattern);
        var totalEvaluated = hasFilter ? nodes.Count(n => GlobMatcher.IsMatch(n.FullName, filterPattern)) : nodes.Count;
        var entryPointCount = hasFilter ? nodes.Count(n => entryPoints.Contains(n.Id) && GlobMatcher.IsMatch(n.FullName, filterPattern)) : entryPoints.Count;

        var excludedTestsCount = graph.ExcludedTestProjectCount;
        var refsFromTestsExcluded = testsExcluded ?? (excludedTestsCount > 0 ? true : (bool?)null);
        var noteParts = new List<string>();
        if (refsFromTestsExcluded == true && (excludedTestsCount > 0 || ordered.Count > 0))
        {
            noteParts.Add(excludedTestsCount > 0
                ? $"{excludedTestsCount} test project(s) excluded; symbols referenced only by tests may appear unreferenced. Pass includeTests: true to include test references."
                : "Test projects are excluded by default (includeTests: false); symbols referenced only by tests may appear unreferenced.");
        }

        if (string.Equals(effectiveProfile, "app", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(profile, "auto", StringComparison.OrdinalIgnoreCase) &&
            totalEvaluated >= 20 &&
            ordered.Count > totalEvaluated * 0.4 &&
            graph.GetNodes(NodeKind.Project).Any(p => p.Metadata?.GetValueOrDefault("outputKind")?.Equals("DynamicallyLinkedLibrary", StringComparison.OrdinalIgnoreCase) == true))
        {
            noteParts.Add("High unreferenced ratio under auto-selected 'app' profile; if this is primarily a class-library solution, try profile='lib'.");
        }

        var note = noteParts.Count > 0 ? string.Join(" ", noteParts) : null;

        return new DeadCodeReport(
            Scope: effectiveScope,
            TotalEvaluated: totalEvaluated,
            EntryPointCount: entryPointCount,
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

    private static HashSet<string> WireInSolutionContractToImplEdges(
        CodeGraph graph,
        List<CodeNode> methodNodes,
        Dictionary<string, HashSet<string>> adjacency)
    {
        var methodIdsByOwnerAndSig = new Dictionary<(string OwnerId, string Sig), List<string>>();
        var methodIdsByOwnerAndName = new Dictionary<(string OwnerId, string Name), List<string>>();
        foreach (var m in methodNodes)
        {
            if (string.IsNullOrEmpty(m.ContainingTypeId)) continue;
            var sig = ExtractMethodSignatureSuffix(m);
            var sigKey = (m.ContainingTypeId, sig);
            if (!methodIdsByOwnerAndSig.TryGetValue(sigKey, out var sigList))
            {
                sigList = [];
                methodIdsByOwnerAndSig[sigKey] = sigList;
            }
            sigList.Add(m.Id);

            var nameKey = (m.ContainingTypeId, m.Name);
            if (!methodIdsByOwnerAndName.TryGetValue(nameKey, out var nameList))
            {
                nameList = [];
                methodIdsByOwnerAndName[nameKey] = nameList;
            }
            nameList.Add(m.Id);
        }

        // Map each type -> its direct & transitive base/interface type IDs
        var contractsByType = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var t in graph.GetNodes(NodeKind.Type))
        {
            var contracts = new HashSet<string>(StringComparer.Ordinal);
            foreach (var e in graph.GetOutgoing(t.Id))
            {
                if (e.Kind is DependencyKind.Implements or DependencyKind.Inherits)
                {
                    contracts.Add(e.ToId);
                }
            }
            foreach (var bt in EntryPointDetectors.SplitMetadataList(t, "baseTypes"))
            {
                contracts.Add(bt);
                var lt = bt.IndexOf('<');
                if (lt > 0) contracts.Add(bt[..lt]);
            }
            if (contracts.Count > 0)
            {
                contractsByType[t.Id] = [.. contracts];
            }
        }

        // Track methods whose ONLY interface/override contracts exist inside this solution graph.
        // External framework contracts (IDisposable, object.ToString, IHostedService, etc.) are NOT in the graph,
        // so methods implementing/overriding external contracts stay automatic entry points.
        var inSolutionOnlyImpls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in methodNodes)
        {
            if (string.IsNullOrEmpty(m.ContainingTypeId)) continue;
            var isImplOrOverride = EntryPointDetectors.HasMetadataFlag(m, "implementsInterface") ||
                                   EntryPointDetectors.HasMetadataFlag(m, "isOverride");
            if (!isImplOrOverride) continue;
            if (!contractsByType.TryGetValue(m.ContainingTypeId, out var contracts)) continue;

            var sig = ExtractMethodSignatureSuffix(m);
            var hasExternalContract = false;
            var matchedContractMethods = new List<string>();
            foreach (var contractTypeId in contracts)
            {
                if (graph.FindNode(contractTypeId) is null)
                {
                    if (!contractTypeId.Equals("System.Object", StringComparison.Ordinal))
                    {
                        hasExternalContract = true;
                    }
                    continue;
                }

                if (methodIdsByOwnerAndSig.TryGetValue((contractTypeId, sig), out var exactMatches))
                {
                    matchedContractMethods.AddRange(exactMatches);
                }
                else if (methodIdsByOwnerAndName.TryGetValue((contractTypeId, m.Name), out var nameMatches))
                {
                    matchedContractMethods.AddRange(nameMatches);
                }
            }

            if (matchedContractMethods.Count > 0)
            {
                foreach (var contractMethodId in matchedContractMethods)
                {
                    if (!string.Equals(contractMethodId, m.Id, StringComparison.Ordinal))
                    {
                        if (!adjacency.TryGetValue(contractMethodId, out var targets))
                        {
                            targets = new HashSet<string>(StringComparer.Ordinal);
                            adjacency[contractMethodId] = targets;
                        }
                        targets.Add(m.Id);
                    }
                }

                if (!hasExternalContract)
                {
                    inSolutionOnlyImpls.Add(m.Id);
                }
            }
        }

        return inSolutionOnlyImpls;
    }

    private static bool IsReachableMethodEntryPoint(
        CodeNode node,
        CodeGraph graph,
        HashSet<string> reachableTypes,
        HashSet<string> inSolutionContractImplMethodIds)
    {
        if (EntryPointDetectors.SplitMetadataList(node, "calledFromMemberBodyBy").Any(reachableTypes.Contains))
        {
            return true;
        }

        if (node.ContainingTypeId is null || !reachableTypes.Contains(node.ContainingTypeId))
        {
            return false;
        }

        var isContractImpl = EntryPointDetectors.HasMetadataFlag(node, "isOverride") ||
                             EntryPointDetectors.HasMetadataFlag(node, "implementsInterface");
        if ((isContractImpl && !inSolutionContractImplMethodIds.Contains(node.Id)) ||
            EntryPointDetectors.HasMetadataFlag(node, "calledFromMemberBody") ||
            EntryPointDetectors.HasMetadataFlag(node, "isXamlEventHandler") ||
            node.Metadata?.GetValueOrDefault("methodKind") == "StaticConstructor")
        {
            return true;
        }

        if (node.Metadata?.GetValueOrDefault("methodKind") == "Constructor")
        {
            // Parameterless constructors on reachable types (default/empty, serializer/EF, or private factory/union guards)
            // are not actionable dead code.
            if (node.ConstructorParameterCount == 0 ||
                EntryPointDetectors.HasMetadataFlag(node, "isEmptyConstructor") ||
                EntryPointDetectors.HasMetadataFlag(node, "isCompilerSynthesizedCtor"))
            {
                return true;
            }

            // Primary/public constructors of reachable records and pure data/DTO types are commonly invoked via
            // JSON/config/ORM deserialization rather than direct `new` calls.
            var containingType = graph.FindNode(node.ContainingTypeId);
            if (containingType is not null &&
                (containingType.TypeKind == TypeKind.Record || EntryPointDetectors.HasMetadataFlag(containingType, "isDataType")) &&
                (EntryPointDetectors.HasMetadataFlag(node, "isPrimaryConstructor") ||
                 (containingType.TypeKind != TypeKind.Record && node.Accessibility == AccessibilityKind.Public)))
            {
                return true;
            }
        }

        return false;
    }

    private static string ExtractMethodSignatureSuffix(CodeNode method)
    {
        if (method.ContainingTypeId is { Length: > 0 } owner &&
            method.FullName.StartsWith(owner + ".", StringComparison.Ordinal))
        {
            return method.FullName[(owner.Length + 1)..];
        }
        return method.Name;
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

        if (normalized != "auto")
        {
            throw new ArgumentException($"Unknown dead_code profile '{profile}'. Supported profiles: 'auto', 'app', 'lib', 'strict'.");
        }

        // "auto": treat a solution as "app" when it has a production executable project (excluding
        // test/playground/sample/benchmark harnesses that live alongside reusable class libraries).
        var projectNodes = graph.GetNodes(NodeKind.Project).ToList();
        var hasExecutableProject = projectNodes.Any(p =>
            !IsAuxiliaryExecutableProject(p) &&
            p.Metadata is not null &&
            p.Metadata.TryGetValue("outputKind", out var ok) &&
            (ok.Equals("ConsoleApplication", StringComparison.OrdinalIgnoreCase) ||
             ok.Equals("WindowsApplication", StringComparison.OrdinalIgnoreCase)));

        if (!hasExecutableProject && projectNodes.Count == 0)
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

    private static bool IsAuxiliaryExecutableProject(CodeNode projectNode)
    {
        if (EntryPointDetectors.HasMetadataFlag(projectNode, "isTestProject") ||
            EntryPointDetectors.HasMetadataFlag(projectNode, "isTestSupportProject"))
        {
            return true;
        }

        var relPath = (projectNode.Location?.FilePath ?? string.Empty).Replace('\\', '/');
        if (relPath.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith("test/", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith("samples/", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith("examples/", StringComparison.OrdinalIgnoreCase) ||
            relPath.StartsWith("benchmarks/", StringComparison.OrdinalIgnoreCase) ||
            relPath.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
            relPath.Contains("/samples/", StringComparison.OrdinalIgnoreCase) ||
            relPath.Contains("/examples/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var name = projectNode.Name;
        return name.EndsWith(".Playground", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".Demo", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".Sample", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".Samples", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".Benchmark", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".Benchmarks", StringComparison.OrdinalIgnoreCase);
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
