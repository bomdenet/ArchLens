using ArchLens.Model;

namespace ArchLens.Analysis;

/// <summary>
/// High-level agent-friendly architecture query facade.
/// Transport-agnostic: consumed identically by MCP tools, CLI commands, and future UI/CI adapters.
/// </summary>
public sealed class ArchitectureAnalyzerService
{
    private readonly MetricsEngine _metricsEngine;
    private readonly IReadOnlyList<IEntryPointDetector> _customEntryPointDetectors;
    private readonly DeadCodeAnalyzer _deadCodeAnalyzer;

    public ArchitectureAnalyzerService(
        MetricsEngine? metricsEngine = null,
        IEnumerable<IEntryPointDetector>? customEntryPointDetectors = null)
    {
        _metricsEngine = metricsEngine ?? new MetricsEngine();
        _customEntryPointDetectors = customEntryPointDetectors?.ToList() ?? [];
        _deadCodeAnalyzer = new DeadCodeAnalyzer(_customEntryPointDetectors);
    }

    public SolutionOverviewReport GetOverview(ArchitectureSnapshot snapshot, int topNamespacesLimit = 25)
    {
        var graph = snapshot.Graph;
        var types = graph.GetNodes(NodeKind.Type).ToList();
        var methods = graph.GetNodes(NodeKind.Method).ToList();
        var namespaces = graph.GetNodes(NodeKind.Namespace).ToList();
        var projectCycles = CycleDetector.FindCycles(graph, NodeKind.Project);
        var namespaceCycles = CycleDetector.FindCycles(graph, NodeKind.Namespace);
        var namespaceAdjacency = namespaceCycles.Count > 0 ? graph.BuildAdjacency(NodeKind.Namespace) : null;
        var suppressedNamespaceCycles = new Dictionary<string, int>(StringComparer.Ordinal);
        var actionableNamespaceCycleCount = 0;
        foreach (var cycle in namespaceCycles)
        {
            var reason = CycleDetector.GetDefaultNamespaceSuppressionReason(cycle, namespaceAdjacency);
            if (reason is not null)
            {
                suppressedNamespaceCycles[reason] = suppressedNamespaceCycles.GetValueOrDefault(reason, 0) + 1;
            }
            else
            {
                actionableNamespaceCycleCount++;
            }
        }

        var allNamespaces = types
            .Where(t => !string.IsNullOrWhiteSpace(t.Namespace))
            .GroupBy(t => t.Namespace!, StringComparer.Ordinal)
            .Select(g => new NamespaceSummary(g.Key, g.Count(), g.Sum(t => t.LineCount)))
            .OrderByDescending(n => n.TypeCount)
            .ToList();

        var clampedNsLimit = Math.Max(1, topNamespacesLimit);
        var topNamespaces = allNamespaces.Take(clampedNsLimit).ToList();
        var topNamespacesTruncated = allNamespaces.Count > topNamespaces.Count;

        return new SolutionOverviewReport(
            SnapshotId: snapshot.SnapshotId,
            TargetPath: snapshot.TargetPath,
            CapturedAt: snapshot.CapturedAt,
            ProjectCount: graph.Projects.Count,
            NamespaceCount: namespaces.Count,
            TypeCount: types.Count,
            DependencyEdgeCount: graph.Edges.Count,
            ProjectCycleCount: projectCycles.Count,
            NamespaceCycleCount: actionableNamespaceCycleCount,
            Projects: graph.Projects,
            TopNamespaces: topNamespaces,
            Diagnostics: graph.Diagnostics,
            MethodCount: graph.MethodsIncluded ? methods.Count : null,
            MethodsIncluded: graph.MethodsIncluded,
            TopNamespacesTruncated: topNamespacesTruncated,
            LoadStatus: graph.LoadStatus,
            NamespaceCycleCountRaw: suppressedNamespaceCycles.Count > 0 ? namespaceCycles.Count : null,
            SuppressedNamespaceCycles: suppressedNamespaceCycles.Count > 0 ? suppressedNamespaceCycles : null);
    }

    public DependencyGraphReport GetDependencyGraph(
        CodeGraph graph,
        NodeKind scope = NodeKind.Namespace,
        string? filterPattern = null,
        int maxNodes = 60,
        int? maxEdges = null,
        bool includeCycles = true,
        int maxCycles = 20,
        bool includeSuppressedCycles = false)
    {
        var adj = graph.BuildAdjacency(scope);
        var rawCycles = CycleDetector.FindCycles(graph, scope);

        var filteredNodes = adj.Keys
            .Where(k => GlobMatcher.IsMatch(k, filterPattern))
            .OrderByDescending(k => adj[k].Count)
            .ThenBy(k => k, StringComparer.Ordinal)
            .ToList();
        var filteredSet = filteredNodes.ToHashSet(StringComparer.Ordinal);

        var scopedCycles = string.IsNullOrWhiteSpace(filterPattern)
            ? rawCycles
            : rawCycles.Where(c => c.Nodes.Any(filteredSet.Contains)).ToList();

        Dictionary<string, int>? suppressedCycles = null;
        IReadOnlyList<CycleInfo> allCycles;
        if (!includeSuppressedCycles && scope == NodeKind.Namespace)
        {
            var actionable = new List<CycleInfo>(scopedCycles.Count);
            var suppressed = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var cycle in scopedCycles)
            {
                var reason = CycleDetector.GetDefaultNamespaceSuppressionReason(cycle, adj);
                if (reason is not null)
                {
                    suppressed[reason] = suppressed.GetValueOrDefault(reason, 0) + 1;
                }
                else
                {
                    actionable.Add(cycle);
                }
            }
            allCycles = actionable;
            if (suppressed.Count > 0) suppressedCycles = suppressed;
        }
        else
        {
            allCycles = scopedCycles;
        }

        var clampedNodes = Math.Max(1, maxNodes);
        var nodesTruncated = filteredNodes.Count > clampedNodes;
        var selectedSet = filteredNodes.Take(clampedNodes).ToHashSet(StringComparer.Ordinal);

        var totalEdgesInScope = 0;
        foreach (var from in filteredSet)
        {
            foreach (var to in adj[from])
            {
                if (filteredSet.Contains(to))
                {
                    totalEdgesInScope++;
                }
            }
        }

        var allSelectedEdges = new List<DependencyGraphEdgeDto>();
        foreach (var from in selectedSet.OrderBy(x => x, StringComparer.Ordinal))
        {
            foreach (var to in adj[from].OrderBy(x => x, StringComparer.Ordinal))
            {
                if (selectedSet.Contains(to))
                {
                    allSelectedEdges.Add(new DependencyGraphEdgeDto(from, to));
                }
            }
        }

        var edgesTruncated = (maxEdges.HasValue && allSelectedEdges.Count > Math.Max(0, maxEdges.Value)) ||
                             allSelectedEdges.Count < totalEdgesInScope;
        var returnedEdges = maxEdges.HasValue && allSelectedEdges.Count > Math.Max(0, maxEdges.Value)
            ? allSelectedEdges.Take(Math.Max(0, maxEdges!.Value)).ToList()
            : allSelectedEdges;

        var returnedCycles = includeCycles
            ? allCycles.Take(Math.Max(0, maxCycles)).ToList()
            : [];
        var cyclesTruncated = includeCycles && allCycles.Count > returnedCycles.Count;
        var isFiltered = !string.IsNullOrWhiteSpace(filterPattern);
        var hasCrossBoundaryCycle = isFiltered && returnedCycles.Any(c => c.Nodes.Any(n => !selectedSet.Contains(n)));
        var graphNote = hasCrossBoundaryCycle
            ? "Cycles include all cycles touching filtered nodes (some cycle members lie outside the filtered node list)."
            : null;

        return new DependencyGraphReport(
            Scope: scope,
            TotalNodesInScope: filteredNodes.Count,
            ReturnedNodeCount: selectedSet.Count,
            Truncated: nodesTruncated || edgesTruncated || cyclesTruncated,
            Cycles: returnedCycles,
            Nodes: selectedSet.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            Edges: returnedEdges,
            TotalEdgeCount: totalEdgesInScope,
            ReturnedEdgeCount: returnedEdges.Count,
            EdgesTruncated: edgesTruncated,
            TotalCycleCount: allCycles.Count,
            SuppressedCycles: suppressedCycles,
            TotalNodesBeforeFilter: isFiltered && filteredNodes.Count != adj.Count ? adj.Count : null,
            Note: graphNote);
    }

    public HotspotsReport GetHotspots(
        CodeGraph graph,
        string sortBy = "complexity",
        string? filterPattern = null,
        NodeKind scope = NodeKind.Type,
        int topN = 15,
        IEnumerable<ICustomMetricCalculator>? extraMetricCalculators = null)
    {
        var key = (sortBy ?? string.Empty).Trim().ToLowerInvariant();
        if (key == "distance" && scope != NodeKind.Namespace)
        {
            throw new ArgumentException("Metric 'distance' (Distance from Main Sequence) is only defined at scope='namespace'. Pass scope: 'namespace'.");
        }

        if (scope is NodeKind.Method or NodeKind.Namespace &&
            key is "lcom4" or "cohesion" or "ctor" or "constructor_params" or "god_class" or "godclass")
        {
            var scopeLabel = scope == NodeKind.Method ? "method" : "namespace";
            throw new ArgumentException($"Metric '{sortBy}' is only defined at type scope (not scope='{scopeLabel}'). Pass scope: 'type'.");
        }

        if (graph.LoadStatus?.Approximate == true && key is "cognitive" or "cognitive_complexity" or "total_cognitive")
        {
            throw new ArgumentException("Cognitive complexity is unavailable with backend='il'. Pass backend: 'roslyn'.");
        }

        var metricsMap = scope switch
        {
            NodeKind.Namespace => _metricsEngine.CalculateNamespaceMetrics(graph, extraMetricCalculators),
            NodeKind.Method => _metricsEngine.CalculateMethodMetrics(graph, extraMetricCalculators),
            _ => _metricsEngine.CalculateTypeMetrics(graph, extraMetricCalculators)
        };

        var filtered = metricsMap.Values
            .Where(m => GlobMatcher.IsMatch(m.Name, filterPattern))
            .ToList();

        // Align default unfiltered lcom4 / ctor hotspot rankings with the rule engine's noise exclusions
        // (DbContext, XAML, Razor, *Options, migrations for lcom4; records/DTOs for ctor).
        string? hotspotsNote = null;
        if (scope == NodeKind.Type && string.IsNullOrWhiteSpace(filterPattern) &&
            key is "lcom4" or "cohesion" or "ctor" or "constructor_params")
        {
            var ruleMetricName = key is "lcom4" or "cohesion" ? "lcom4" : "constructor_params";
            var beforeCount = filtered.Count;
            filtered = filtered
                .Where(m => !MaxMetricRule.ShouldExcludeEfOrGeneratedNode(graph, m, ruleMetricName))
                .ToList();
            var excludedCount = beforeCount - filtered.Count;
            if (excludedCount > 0)
            {
                hotspotsNote = ruleMetricName == "constructor_params"
                    ? $"{excludedCount} record/data type(s) excluded by default; pass a filter (e.g. '*') to include all types."
                    : $"{excludedCount} EF/generated/UI/options type(s) excluded by default; pass a filter (e.g. '*') to include all types.";
            }
        }

        var isBuiltIn = key is
            "god_class" or "godclass" or
            "cognitive" or "cognitive_complexity" or "total_cognitive" or
            "lcom4" or "cohesion" or
            "coupling" or "ce" or "efferent" or
            "afferent" or "ca" or
            "instability" or
            "distance" or
            "lines" or "loc" or "size" or
            "ctor" or "constructor_params" or
            "complexity" or "cyclomatic" or "cyclomatic_complexity" or "total_complexity" or "total_cyclomatic";

        if (!isBuiltIn && !metricsMap.Values.Any(m => HasValidCustomMetric(m, sortBy!)))
        {
            var customNames = metricsMap.Values
                .Where(m => m.CustomMetrics is { Count: > 0 })
                .SelectMany(m => m.CustomMetrics!.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var customHint = customNames.Count > 0 ? $" Custom metrics: {string.Join(", ", customNames)}." : string.Empty;
            throw new ArgumentException(
                $"Unknown hotspots sortBy '{sortBy}'. Supported values: complexity, total_complexity, god_class, cognitive, total_cognitive, lcom4, coupling, afferent, instability, distance (namespace scope), lines, ctor.{customHint}");
        }

        IOrderedEnumerable<NodeMetrics> ordered = key switch
        {
            "god_class" or "godclass" => filtered
                .OrderByDescending(m => ComputeGodClassScore(m))
                .ThenByDescending(m => m.LineCount)
                .ThenByDescending(m => m.CyclomaticComplexity),
            "cognitive" or "cognitive_complexity" => filtered.OrderByDescending(m => m.MaxMethodCognitiveComplexity).ThenByDescending(m => m.CognitiveComplexity),
            "total_cognitive" => filtered.OrderByDescending(m => m.CognitiveComplexity).ThenByDescending(m => m.MaxMethodCognitiveComplexity),
            "lcom4" or "cohesion" => filtered.OrderByDescending(m => m.Lcom4).ThenByDescending(m => m.EfferentCoupling),
            "coupling" or "ce" or "efferent" => filtered.OrderByDescending(m => m.EfferentCoupling).ThenByDescending(m => m.AfferentCoupling),
            "afferent" or "ca" => filtered.OrderByDescending(m => m.AfferentCoupling).ThenByDescending(m => m.EfferentCoupling),
            "instability" => filtered.OrderByDescending(m => m.Instability).ThenByDescending(m => m.EfferentCoupling),
            "distance" => filtered.OrderByDescending(m => m.DistanceFromMainSequence ?? 0).ThenByDescending(m => m.EfferentCoupling),
            "lines" or "loc" or "size" => filtered.OrderByDescending(m => m.LineCount),
            "ctor" or "constructor_params" => filtered.OrderByDescending(m => m.ConstructorParameterCount).ThenByDescending(m => m.EfferentCoupling),
            "total_complexity" or "total_cyclomatic" => filtered.OrderByDescending(m => m.CyclomaticComplexity).ThenByDescending(m => m.MaxMethodComplexity),
            "complexity" or "cyclomatic" or "cyclomatic_complexity" => filtered.OrderByDescending(m => m.MaxMethodComplexity).ThenByDescending(m => m.CyclomaticComplexity),
            _ => filtered
                .OrderByDescending(m => HasValidCustomMetric(m, sortBy!))
                .ThenByDescending(m => GetValidCustomMetric(m, sortBy!))
        };

        var items = ordered.Take(Math.Max(1, topN)).ToList();
        return new HotspotsReport(scope, sortBy!, metricsMap.Count, items, hotspotsNote);
    }

    public static double ComputeGodClassScore(NodeMetrics m) =>
        Math.Round((m.LineCount / 500.0) * (m.CyclomaticComplexity / 50.0) * (m.EfferentCoupling / 20.0), 3);

    private static bool HasValidCustomMetric(NodeMetrics m, string metricName) =>
        m.CustomMetrics is not null &&
        m.CustomMetrics.TryGetValue(metricName, out var v) &&
        !double.IsNaN(v);

    private static double GetValidCustomMetric(NodeMetrics m, string metricName) =>
        m.CustomMetrics is not null &&
        m.CustomMetrics.TryGetValue(metricName, out var v) &&
        !double.IsNaN(v)
            ? v
            : double.NegativeInfinity;

    public RuleCheckReport CheckRules(
        CodeGraph graph,
        IEnumerable<IArchitectureRule> rules,
        int maxViolations = 100,
        bool includeAnalyzerDiagnostics = false,
        IEnumerable<ICustomMetricCalculator>? extraMetricCalculators = null,
        IEnumerable<IEntryPointDetector>? extraEntryPointDetectors = null,
        bool excludeDegradedProjects = true,
        string? rootNamespace = null)
    {
        var ruleList = rules.ToList();
        var typeMetrics = _metricsEngine.CalculateTypeMetrics(graph, extraMetricCalculators);
        var mergedDetectors = MergeDetectors(extraEntryPointDetectors);
        var allViolations = new List<RuleViolation>();
        var suppressedCycles = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenSuppressed = new HashSet<(string Reason, string CycleKey)>();
        var acknowledgedDeadCode = new List<DeadCodeAcknowledgement>();

        foreach (var rule in ruleList)
        {
            try
            {
                var violations = rule switch
                {
                    NoDeadCodeRule deadCodeRule => deadCodeRule.Evaluate(graph, typeMetrics, mergedDetectors, acknowledgedDeadCode),
                    NoCyclesRule noCyclesRule => noCyclesRule.EvaluateWithSuppressed(graph, typeMetrics, suppressedCycles, seenSuppressed),
                    _ => rule.Evaluate(graph, typeMetrics)
                };
                allViolations.AddRange(violations);
            }
            catch (Exception ex)
            {
                allViolations.Add(new RuleViolation(
                    RuleId: rule.Id,
                    RuleName: rule.Name,
                    Severity: RuleSeverity.Warning,
                    Message: $"Rule '{rule.Id}' failed during evaluation: {ex.Message}"));
            }
        }

        if (includeAnalyzerDiagnostics && graph.AnalyzerDiagnostics.Count > 0)
        {
            foreach (var diag in graph.AnalyzerDiagnostics)
            {
                allViolations.Add(new RuleViolation(
                    RuleId: diag.Id,
                    RuleName: string.IsNullOrWhiteSpace(diag.Title) ? diag.Id : diag.Title,
                    Severity: diag.Severity,
                    Message: $"[{diag.Category}] {diag.Message}",
                    NodeId: diag.ProjectName,
                    Location: diag.Location));
            }
        }

        string? configNote = ValidateRootNamespace(graph, rootNamespace);
        var vacuousConfiguration = configNote is not null;
        if (vacuousConfiguration)
        {
            allViolations.Add(new RuleViolation(
                RuleId: "ARCHLENS-CONFIG-001",
                RuleName: "Vacuous rootNamespace configuration",
                Severity: RuleSeverity.Error,
                Message: configNote!));
        }

        IReadOnlyList<string>? skippedDegradedProjects = null;
        if (excludeDegradedProjects &&
            graph.LoadStatus is { Degraded: true, DegradedProjects: { Count: > 0 } degradedList })
        {
            var degradedSet = degradedList.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var filteredViolations = new List<RuleViolation>(allViolations.Count);
            var skippedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var v in allViolations)
            {
                var projNames = ResolveViolationProjects(graph, v);
                if (projNames.Count > 0 && projNames.All(degradedSet.Contains))
                {
                    foreach (var p in projNames) skippedSet.Add(p);
                    continue;
                }
                filteredViolations.Add(v);
            }

            allViolations = filteredViolations;
            if (skippedSet.Count > 0)
            {
                skippedDegradedProjects = skippedSet.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        // Advisory when every configured dependency-selector rule matched 0 symbols in a non-empty graph
        if (configNote is null && graph.GetNodes(NodeKind.Type).Any())
        {
            var selectorRules = ruleList
                .Where(r => r is ForbidDependencyRule or AllowOnlyDependenciesRule)
                .ToList();
            if (selectorRules.Count > 0 && selectorRules.All(r => r switch
                {
                    ForbidDependencyRule f => f.CountMatchedSourceSymbols(graph) == 0,
                    AllowOnlyDependenciesRule a => a.CountMatchedSourceSymbols(graph) == 0,
                    _ => false
                }))
            {
                var ruleIds = string.Join(", ", selectorRules.Select(r => r.Id));
                configNote = $"Advisory: all dependency selector rule(s) ({ruleIds}) matched 0 symbols in the graph; verify namespace/project globs match this codebase.";
            }
        }

        var configurationError = allViolations.Any(v => v.Details is not null && v.Details.TryGetValue("configError", out var ce) && ce == "true");

        var ordered = allViolations
            .OrderByDescending(v => v.Severity)
            .ThenBy(v => v.RuleId, StringComparer.Ordinal)
            .ThenBy(v => v.NodeId, StringComparer.Ordinal)
            .ToList();

        var violationCountByRule = ordered
            .GroupBy(v => v.RuleId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var evaluatedRules = ruleList
            .Select(r => new EvaluatedRuleSummary(r.Id, r.Name, r.Severity, violationCountByRule.GetValueOrDefault(r.Id, 0)))
            .ToList();
        if (violationCountByRule.TryGetValue("ARCHLENS-CONFIG-001", out var configCount) && configCount > 0)
        {
            evaluatedRules.Insert(0, new EvaluatedRuleSummary(
                "ARCHLENS-CONFIG-001", "Vacuous rootNamespace configuration", RuleSeverity.Error, configCount));
        }

        var distinctAcknowledged = acknowledgedDeadCode.Count > 0
            ? acknowledgedDeadCode
                .GroupBy(a => a.Symbol, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .OrderBy(a => a.Symbol, StringComparer.Ordinal)
                .ToList()
            : null;

        return new RuleCheckReport(
            RulesEvaluated: ruleList.Count,
            TotalViolations: ordered.Count,
            ErrorCount: ordered.Count(v => v.Severity == RuleSeverity.Error),
            WarningCount: ordered.Count(v => v.Severity == RuleSeverity.Warning),
            InfoCount: ordered.Count(v => v.Severity == RuleSeverity.Info),
            Truncated: ordered.Count > maxViolations,
            Violations: ordered.Take(Math.Max(1, maxViolations)).ToList(),
            SkippedDegradedProjects: skippedDegradedProjects,
            SuppressedCycles: suppressedCycles.Count > 0 ? suppressedCycles : null,
            EvaluatedRules: evaluatedRules,
            Note: configNote,
            VacuousConfiguration: vacuousConfiguration,
            AcknowledgedDeadCode: distinctAcknowledged,
            ConfigurationError: configurationError);
    }

    public static string? ValidateRootNamespace(CodeGraph graph, string? rootNamespace)
    {
        if (string.IsNullOrWhiteSpace(rootNamespace))
        {
            return null;
        }

        var types = graph.GetNodes(NodeKind.Type).ToList();
        var trimmedRoot = rootNamespace.Trim().TrimEnd('.', '*');
        if (string.IsNullOrWhiteSpace(trimmedRoot))
        {
            return null;
        }

        if (types.Count == 0)
        {
            return $"rootNamespace '{rootNamespace}' matched 0 types (graph contains 0 types); namespace-scoped rules evaluated vacuously. Verify the target workspace contains analyzable projects or pass includeTests: true.";
        }

        var matched = types.Any(t =>
            !string.IsNullOrWhiteSpace(t.Namespace) &&
            (t.Namespace.Equals(trimmedRoot, StringComparison.OrdinalIgnoreCase) ||
             t.Namespace.StartsWith(trimmedRoot + ".", StringComparison.OrdinalIgnoreCase)));
        if (matched)
        {
            return null;
        }

        var detectedPrefixes = types
            .Select(t => t.Namespace)
            .Where(ns => !string.IsNullOrWhiteSpace(ns) && ns != "(global)")
            .Select(ns =>
            {
                var dot = ns!.IndexOf('.');
                return dot > 0 ? ns[..dot] : ns;
            })
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(g => g.Key)
            .ToList();

        var prefixHint = detectedPrefixes.Count > 0
            ? $" Detected top-level namespace prefixes: {string.Join(", ", detectedPrefixes)}."
            : string.Empty;

        return $"rootNamespace '{rootNamespace}' matched 0 types (out of {types.Count} in the graph); namespace-scoped preset rules evaluated vacuously.{prefixHint} Verify rootNamespace or omit it.";
    }

    private static List<string> ResolveViolationProjects(CodeGraph graph, RuleViolation v)
    {
        if (v.Details is not null && v.Details.TryGetValue("projects", out var rawProjects) && !string.IsNullOrWhiteSpace(rawProjects))
        {
            return rawProjects.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        if (string.IsNullOrWhiteSpace(v.NodeId))
        {
            return [];
        }

        if (graph.FindNode(v.NodeId) is { ProjectName.Length: > 0 } node)
        {
            return [node.ProjectName];
        }

        if (graph.FindNode($"ns:{v.NodeId}") is { ProjectName.Length: > 0 } nsNode)
        {
            var nsProjects = graph.GetNodes(NodeKind.Type)
                .Where(t => string.Equals(t.Namespace, v.NodeId, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(t.ProjectName))
                .Select(t => t.ProjectName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return nsProjects.Count > 0 ? nsProjects : [nsNode.ProjectName];
        }

        if (graph.Projects.Any(p => p.Name.Equals(v.NodeId, StringComparison.OrdinalIgnoreCase)))
        {
            return [v.NodeId];
        }

        return [];
    }

    public DeadCodeReport AnalyzeDeadCode(
        CodeGraph graph,
        NodeKind scope = NodeKind.Type,
        bool treatPublicApiAsEntryPoint = true,
        string? filterPattern = null,
        int maxItems = 100,
        IEnumerable<IEntryPointDetector>? extraDetectors = null,
        bool? testsExcluded = null,
        string? profile = null,
        IReadOnlyList<DeadCodeAcknowledgement>? acknowledged = null,
        bool? treatPublicApiOverride = null,
        IReadOnlyList<string>? excludePatterns = null)
    {
        if (extraDetectors is null)
        {
            return _deadCodeAnalyzer.Analyze(graph, scope, treatPublicApiAsEntryPoint, filterPattern, maxItems, testsExcluded: testsExcluded, profile: profile, acknowledged: acknowledged, treatPublicApiOverride: treatPublicApiOverride, excludePatterns: excludePatterns);
        }

        var combined = new DeadCodeAnalyzer(MergeDetectors(extraDetectors));
        return combined.Analyze(graph, scope, treatPublicApiAsEntryPoint, filterPattern, maxItems, testsExcluded: testsExcluded, profile: profile, acknowledged: acknowledged, treatPublicApiOverride: treatPublicApiOverride, excludePatterns: excludePatterns);
    }

    private IReadOnlyList<IEntryPointDetector>? MergeDetectors(IEnumerable<IEntryPointDetector>? extraDetectors)
    {
        var extraList = extraDetectors?.ToList();
        if ((extraList is null || extraList.Count == 0) && _customEntryPointDetectors.Count == 0)
        {
            return null;
        }

        if (extraList is null || extraList.Count == 0)
        {
            return _customEntryPointDetectors;
        }

        if (_customEntryPointDetectors.Count == 0)
        {
            return extraList;
        }

        return [.. _customEntryPointDetectors, .. extraList];
    }

    public DiAnalysisReport AnalyzeDi(CodeGraph graph) =>
        DiGraphAnalyzer.Analyze(graph);

    public AnalyzerDiagnosticsReport GetAnalyzerDiagnostics(
        CodeGraph graph,
        IReadOnlyList<string>? categories = null,
        int maxItems = 100,
        RuleSeverity? minSeverity = null,
        string? filterPattern = null,
        bool excludeDegradedProjects = true,
        bool elevated = false)
    {
        var filter = categories is { Count: > 0 } &&
                     !categories.Any(c => c.Equals("all", StringComparison.OrdinalIgnoreCase) || c == "*")
            ? categories.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        if (filter is not null &&
            graph.AnalyzersLoadedCount is > 0 &&
            graph.AvailableAnalyzerCategories is { Count: > 0 } availableCategories)
        {
            var availableSet = availableCategories.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknown = categories!
                .Where(c => !string.IsNullOrWhiteSpace(c) && !availableSet.Contains(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (unknown.Count > 0)
            {
                var label = unknown.Count == 1 ? "category" : "categories";
                throw new ArgumentException(
                    $"Unknown analyzer {label}: {string.Join(", ", unknown.Select(c => $"'{c}'"))}. " +
                    $"Available categories from loaded analyzers: {string.Join(", ", availableCategories)} (or 'all').");
            }
        }

        var degradedSet = excludeDegradedProjects &&
                          graph.LoadStatus is { Degraded: true, DegradedProjects: { Count: > 0 } dl }
            ? dl.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        Dictionary<string, List<(string FullName, string? Namespace, int StartLine, int EndLine)>>? typeSpansByFile = null;
        if (!string.IsNullOrWhiteSpace(filterPattern))
        {
            typeSpansByFile = new Dictionary<string, List<(string, string?, int, int)>>(StringComparer.OrdinalIgnoreCase);
            void AddTypeSpan(string filePath, string fullName, string? ns, int startLine, int endLine)
            {
                if (string.IsNullOrWhiteSpace(filePath)) return;
                if (!typeSpansByFile.TryGetValue(filePath, out var list))
                {
                    list = [];
                    typeSpansByFile[filePath] = list;
                }
                list.Add((fullName, ns, startLine, endLine));
            }

            foreach (var t in graph.GetNodes(NodeKind.Type))
            {
                if (t.Metadata is not null &&
                    t.Metadata.TryGetValue("declarationSpans", out var rawSpans) &&
                    !string.IsNullOrWhiteSpace(rawSpans))
                {
                    var parsedAny = false;
                    foreach (var entry in rawSpans.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = entry.Split('|');
                        if (parts.Length == 3 &&
                            int.TryParse(parts[1], out var sLine) &&
                            int.TryParse(parts[2], out var eLine))
                        {
                            AddTypeSpan(parts[0], t.FullName, t.Namespace, sLine, eLine);
                            parsedAny = true;
                        }
                    }
                    if (parsedAny) continue;
                }

                var fp = t.Location?.FilePath;
                if (string.IsNullOrWhiteSpace(fp)) continue;
                var startLine = t.Location!.Line;
                var endLine = t.LineCount > 0 ? startLine + t.LineCount - 1 : int.MaxValue;
                AddTypeSpan(fp, t.FullName, t.Namespace, startLine, endLine);
            }
        }

        bool MatchesTypeOrNamespaceInFile(AnalyzerDiagnostic d)
        {
            if (typeSpansByFile is null || d.Location?.FilePath is not { } sfp ||
                !typeSpansByFile.TryGetValue(sfp, out var spans))
            {
                return false;
            }

            var diagLine = d.Location.Line;
            if (diagLine > 0)
            {
                var containing = spans.Where(s => diagLine >= s.StartLine && diagLine <= s.EndLine).ToList();
                if (containing.Count > 0)
                {
                    return containing.Any(s =>
                        GlobMatcher.IsMatch(s.FullName, filterPattern) ||
                        (!string.IsNullOrWhiteSpace(s.Namespace) && GlobMatcher.IsMatch(s.Namespace, filterPattern)));
                }
            }

            // Fallback when diagnostic line is outside declared type spans (e.g. file-level or namespace-level diagnostic)
            return spans.Any(s =>
                (!string.IsNullOrWhiteSpace(s.Namespace) && GlobMatcher.IsMatch(s.Namespace, filterPattern)) ||
                (diagLine <= 0 && GlobMatcher.IsMatch(s.FullName, filterPattern)));
        }

        var filterHasPathSep = !string.IsNullOrWhiteSpace(filterPattern) &&
            (filterPattern.Contains('/') || filterPattern.Contains('\\'));
        string? normalizedRelFilterPath = null;
        if (!string.IsNullOrWhiteSpace(filterPattern) &&
            !filterPattern.Contains('*') && !filterPattern.Contains('?') &&
            Path.IsPathRooted(filterPattern) &&
            !string.IsNullOrWhiteSpace(graph.RootPath))
        {
            try
            {
                var rel = Path.GetRelativePath(graph.RootPath, Path.GetFullPath(filterPattern)).Replace('\\', '/');
                if (!rel.StartsWith("..", StringComparison.Ordinal))
                {
                    normalizedRelFilterPath = rel;
                }
            }
            catch
            {
                // Ignore invalid path syntax
            }
        }

        var inScopeBeforeFilter = graph.AnalyzerDiagnostics
            .Where(d => filter is null || filter.Contains(d.Category))
            .Where(d => degradedSet is null || d.ProjectName is null || !degradedSet.Contains(d.ProjectName))
            .ToList();

        var matchingScope = inScopeBeforeFilter
            .Where(d => string.IsNullOrWhiteSpace(filterPattern) ||
                        GlobMatcher.IsMatch(d.Id, filterPattern) ||
                        (d.Location?.FilePath is { } fp &&
                         (GlobMatcher.IsMatch(fp, filterPattern) ||
                          (normalizedRelFilterPath is not null && fp.Equals(normalizedRelFilterPath, StringComparison.OrdinalIgnoreCase)) ||
                          (!filterHasPathSep && GlobMatcher.IsMatch(Path.GetFileName(fp), filterPattern)))) ||
                        (d.ProjectName is { } pn && GlobMatcher.IsMatch(pn, filterPattern)) ||
                        MatchesTypeOrNamespaceInFile(d))
            .ToList();

        var filtered = matchingScope
            .Where(d => !minSeverity.HasValue || d.Severity >= minSeverity.Value)
            .OrderByDescending(d => d.Severity)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ThenBy(d => d.Location?.FilePath, StringComparer.Ordinal)
            .ToList();

        var hiddenBySeverity = matchingScope.Count - filtered.Count;
        var analyzersLoaded = graph.AnalyzersLoadedCount;

        string? note = null;
        if (graph.Projects.Count == 0 && (graph.LoadStatus is { ProjectsExpected: 0 } || graph.ExcludedTestProjectCount > 0))
        {
            note = graph.ExcludedTestProjectCount > 0
                ? $"0 non-test projects were loaded ({graph.ExcludedTestProjectCount} test project(s) excluded). Pass includeTests: true (--include-tests) to analyze test projects."
                : "0 projects were loaded in this workspace (nothing to analyze).";
        }
        else if (analyzersLoaded == 0)
        {
            note = "No Roslyn analyzer references were found in project metadata. Enable <AnalysisLevel>latest-recommended</AnalysisLevel> or reference Microsoft.CodeAnalysis.NetAnalyzers / Roslynator in the project to get diagnostics.";
        }
        else if (!string.IsNullOrWhiteSpace(filterPattern) && matchingScope.Count == 0 && inScopeBeforeFilter.Count > 0)
        {
            note = $"0 diagnostics matched filter '{filterPattern}' (out of {inScopeBeforeFilter.Count} in scope; filter matches rule ID, project name, file path, or owning type/namespace).";
        }
        else if (hiddenBySeverity > 0 && minSeverity.HasValue)
        {
            note = elevated
                ? $"{hiddenBySeverity} lower-severity diagnostic(s) hidden by minSeverity={minSeverity.Value.ToString().ToLowerInvariant()}. Lower minSeverity to 'info' to include them."
                : $"{hiddenBySeverity} lower-severity diagnostic(s) hidden by minSeverity={minSeverity.Value.ToString().ToLowerInvariant()}. Lower minSeverity to 'info' to include them, or enable elevateToWarning to also evaluate Info/suppressed rules.";
        }
        else if (filtered.Count == 0 && analyzersLoaded is > 0)
        {
            note = elevated
                ? "0 diagnostics matched the requested categories/severity (with elevateToWarning already enabled)."
                : "0 enabled diagnostics matched the requested categories/severity. Some loaded analyzer rules are Info/Hidden or suppressed by default; enable elevateToWarning to evaluate them.";
        }

        var clamped = Math.Max(1, maxItems);
        var distinctCats = filtered.Select(d => d.Category).Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctCats.Count == 0 && analyzersLoaded is > 0)
        {
            if (matchingScope.Count > 0)
            {
                distinctCats = matchingScope.Select(d => d.Category).Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else if (filter is not null)
            {
                distinctCats = filter.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            }
            else if (graph.AvailableAnalyzerCategories is { Count: > 0 } avail)
            {
                distinctCats = avail.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        return new AnalyzerDiagnosticsReport(
            TotalDiagnostics: filtered.Count,
            ErrorCount: filtered.Count(d => d.Severity == RuleSeverity.Error),
            WarningCount: filtered.Count(d => d.Severity == RuleSeverity.Warning),
            InfoCount: filtered.Count(d => d.Severity == RuleSeverity.Info),
            Truncated: filtered.Count > clamped,
            Categories: distinctCats,
            Diagnostics: filtered.Take(clamped).ToList(),
            HiddenBySeverityCount: hiddenBySeverity > 0 ? hiddenBySeverity : null,
            AnalyzersLoaded: analyzersLoaded,
            Note: note);
    }

    public SymbolInspectionReport? InspectSymbol(
        CodeGraph graph,
        string symbolQuery,
        IEnumerable<ICustomMetricCalculator>? extraMetricCalculators = null)
    {
        var target = ResolveNode(graph, symbolQuery);
        if (target is null)
        {
            return null;
        }

        var metrics = target.Kind == NodeKind.Method
            ? _metricsEngine.CalculateMethodMetrics(graph, extraMetricCalculators).GetValueOrDefault(target.Id)
            : _metricsEngine.CalculateTypeMetrics(graph, extraMetricCalculators).GetValueOrDefault(target.Id);

        var outgoing = graph.GetOutgoing(target.Id)
            .Where(e => e.Kind != DependencyKind.ProjectReference && graph.FindNode(e.ToId)?.Kind == target.Kind)
            .GroupBy(e => e.ToId, StringComparer.Ordinal)
            .Select(g => new SymbolDependencyDto(
                g.Key,
                g.Select(e => e.Kind.ToString()).Distinct().OrderBy(x => x).ToList(),
                g.Sum(e => e.Weight),
                g.FirstOrDefault(e => e.Location is not null)?.Location))
            .OrderBy(d => d.TargetId, StringComparer.Ordinal)
            .ToList();

        var incoming = graph.GetIncoming(target.Id)
            .Where(e => e.Kind != DependencyKind.ProjectReference && graph.FindNode(e.FromId)?.Kind == target.Kind)
            .GroupBy(e => e.FromId, StringComparer.Ordinal)
            .Select(g => new SymbolDependencyDto(
                g.Key,
                g.Select(e => e.Kind.ToString()).Distinct().OrderBy(x => x).ToList(),
                g.Sum(e => e.Weight),
                g.FirstOrDefault(e => e.Location is not null)?.Location))
            .OrderBy(d => d.TargetId, StringComparer.Ordinal)
            .ToList();

        return new SymbolInspectionReport(target, metrics, outgoing, incoming);
    }

    public ImpactAnalysisResult? AnalyzeImpact(
        CodeGraph graph,
        string symbolQuery,
        int maxDepth = 4,
        int maxItems = 50,
        bool excludeRegistrations = true,
        bool includeTests = false,
        bool strict = false)
    {
        var target = ResolveNode(graph, symbolQuery);
        return target is null
            ? null
            : ImpactAnalyzer.Analyze(graph, target, maxDepth, maxItems, excludeRegistrations, includeTests, strict);
    }

    public SnapshotDiffResult DiffSnapshots(
        ArchitectureSnapshot baseline,
        ArchitectureSnapshot current,
        IEnumerable<IArchitectureRule>? rules = null,
        IEnumerable<ICustomMetricCalculator>? extraMetricCalculators = null,
        IEnumerable<IEntryPointDetector>? extraEntryPointDetectors = null,
        bool excludeDegradedProjects = true,
        string? rootNamespace = null)
    {
        var baseTypes = baseline.Graph.GetNodes(NodeKind.Type).Count();
        var currTypes = current.Graph.GetNodes(NodeKind.Type).Count();

        var baseCycles = FilterDefaultNamespaceCycles(baseline.Graph, CycleDetector.FindCycles(baseline.Graph, NodeKind.Namespace));
        var currCycles = FilterDefaultNamespaceCycles(current.Graph, CycleDetector.FindCycles(current.Graph, NodeKind.Namespace));

        static string CycleKey(CycleInfo c) => $"{c.Scope}:{string.Join("|", c.Nodes)}";
        var baseCycleKeys = baseCycles.Select(CycleKey).ToHashSet(StringComparer.Ordinal);
        var currCycleKeys = currCycles.Select(CycleKey).ToHashSet(StringComparer.Ordinal);

        var addedCycles = currCycles.Where(c => !baseCycleKeys.Contains(CycleKey(c))).ToList();
        var resolvedCycles = baseCycles.Where(c => !currCycleKeys.Contains(CycleKey(c))).ToList();

        var ruleList = rules?.ToList() ?? [];
        var extraCalcs = extraMetricCalculators?.ToList();
        var extraDetectors = extraEntryPointDetectors?.ToList();
        var baseReport = ruleList.Count > 0
            ? CheckRules(baseline.Graph, ruleList, int.MaxValue, includeAnalyzerDiagnostics: false, extraMetricCalculators: extraCalcs, extraEntryPointDetectors: extraDetectors, excludeDegradedProjects: excludeDegradedProjects, rootNamespace: rootNamespace)
            : null;
        var currReport = ruleList.Count > 0
            ? CheckRules(current.Graph, ruleList, int.MaxValue, includeAnalyzerDiagnostics: false, extraMetricCalculators: extraCalcs, extraEntryPointDetectors: extraDetectors, excludeDegradedProjects: excludeDegradedProjects, rootNamespace: rootNamespace)
            : null;
        var baseViolations = baseReport?.Violations ?? [];
        var currViolations = currReport?.Violations ?? [];

        static string ViolationKey(RuleViolation v)
        {
            if (v.Details is not null && v.Details.TryGetValue("cycleKey", out var cycleKey) && !string.IsNullOrEmpty(cycleKey))
            {
                return $"{v.RuleId}|{cycleKey}";
            }

            if (!string.IsNullOrEmpty(v.NodeId) && !string.IsNullOrEmpty(v.TargetNodeId))
            {
                return $"{v.RuleId}|{v.NodeId}|{v.TargetNodeId}";
            }

            if (!string.IsNullOrEmpty(v.NodeId) && v.Message.StartsWith("Metric '", StringComparison.Ordinal))
            {
                return $"{v.RuleId}|{v.NodeId}";
            }

            return $"{v.RuleId}|{v.NodeId}|{v.TargetNodeId}|{v.Message}";
        }
        var baseVKeys = baseViolations.Select(ViolationKey).ToHashSet(StringComparer.Ordinal);
        var currVKeys = currViolations.Select(ViolationKey).ToHashSet(StringComparer.Ordinal);

        var newViolations = currViolations.Where(v => !baseVKeys.Contains(ViolationKey(v))).ToList();
        var resolvedViolations = baseViolations.Where(v => !currVKeys.Contains(ViolationKey(v))).ToList();

        return new SnapshotDiffResult(
            BaselineSnapshotId: baseline.SnapshotId,
            CurrentSnapshotId: current.SnapshotId,
            TypeCountDelta: currTypes - baseTypes,
            EdgeCountDelta: current.Graph.Edges.Count - baseline.Graph.Edges.Count,
            CycleCountDelta: currCycles.Count - baseCycles.Count,
            ViolationCountDelta: currViolations.Count - baseViolations.Count,
            AddedCycles: addedCycles,
            ResolvedCycles: resolvedCycles,
            NewViolations: newViolations,
            ResolvedViolations: resolvedViolations,
            VacuousConfiguration: currReport?.VacuousConfiguration ?? false,
            Note: currReport?.Note,
            BaselineViolationCount: baseViolations.Count,
            CurrentViolationCount: currViolations.Count,
            ConfigurationError: currReport?.ConfigurationError ?? false);
    }

    private static List<CycleInfo> FilterDefaultNamespaceCycles(CodeGraph graph, IReadOnlyList<CycleInfo> cycles)
    {
        if (cycles.Count == 0) return [];
        var nsAdj = graph.BuildAdjacency(NodeKind.Namespace);
        return cycles
            .Where(c => !CycleDetector.IsMigrationNamespaceCycle(c.Nodes, nsAdj) &&
                        !CycleDetector.IsParentChildNamespaceCycle(c.Nodes, nsAdj))
            .ToList();
    }

    public static (CodeNode? Match, IReadOnlyList<CodeNode> AmbiguousCandidates) ResolveNodeWithAmbiguity(CodeGraph graph, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return (null, []);
        }

        var (typeMatch, ambiguousTypes) = ResolveTypeNodeWithAmbiguity(graph, query);
        if (typeMatch is not null || ambiguousTypes.Count > 1)
        {
            return (typeMatch, ambiguousTypes);
        }

        var exact = graph.FindNode(query);
        if (exact is { Kind: NodeKind.Method })
        {
            return (exact, []);
        }

        var methods = graph.GetNodes(NodeKind.Method).ToList();
        var byFull = methods.Where(m => m.FullName.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byFull.Count == 1) return (byFull[0], []);
        if (byFull.Count > 1) return (null, byFull.OrderBy(m => m.FullName, StringComparer.Ordinal).ToList());

        var byName = methods.Where(m => m.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) return (byName[0], []);
        if (byName.Count > 1) return (null, byName.OrderBy(m => m.FullName, StringComparer.Ordinal).ToList());

        var bySuffix = methods.Where(m => m.FullName.EndsWith("." + query, StringComparison.OrdinalIgnoreCase) ||
                                          m.FullName.Contains("." + query + "(", StringComparison.OrdinalIgnoreCase)).ToList();
        if (bySuffix.Count == 1) return (bySuffix[0], []);
        if (bySuffix.Count > 1) return (null, bySuffix.OrderBy(m => m.FullName, StringComparer.Ordinal).ToList());

        var byContains = methods.Where(m => m.FullName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byContains.Count == 1) return (byContains[0], []);
        if (byContains.Count > 1) return (null, byContains.OrderBy(m => m.FullName, StringComparer.Ordinal).ToList());

        return (null, []);
    }

    public static (CodeNode? Match, IReadOnlyList<CodeNode> AmbiguousCandidates) ResolveTypeNodeWithAmbiguity(CodeGraph graph, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return (null, []);
        }

        var exact = graph.FindNode(query);
        if (exact is { Kind: NodeKind.Type })
        {
            return (exact, []);
        }

        var types = graph.GetNodes(NodeKind.Type).ToList();
        var byFull = types.Where(t => t.FullName.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byFull.Count == 1) return (byFull[0], []);
        if (byFull.Count > 1) return (null, byFull.OrderBy(t => t.FullName, StringComparer.Ordinal).ToList());

        var byShort = types.Where(t => t.Name.Equals(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byShort.Count == 1) return (byShort[0], []);
        if (byShort.Count > 1) return (null, byShort.OrderBy(t => t.FullName, StringComparer.Ordinal).ToList());

        var bySuffix = types.Where(t => t.FullName.EndsWith("." + query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (bySuffix.Count == 1) return (bySuffix[0], []);
        if (bySuffix.Count > 1) return (null, bySuffix.OrderBy(t => t.FullName, StringComparer.Ordinal).ToList());

        return (null, []);
    }

    public static CodeNode? ResolveNode(CodeGraph graph, string query)
    {
        var typeMatch = ResolveTypeNode(graph, query);
        if (typeMatch is not null)
        {
            return typeMatch;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var exact = graph.FindNode(query);
        if (exact is { Kind: NodeKind.Method })
        {
            return exact;
        }

        var methods = graph.GetNodes(NodeKind.Method).ToList();
        return methods.FirstOrDefault(m => m.FullName.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? methods.FirstOrDefault(m => m.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
            ?? methods.FirstOrDefault(m => m.FullName.EndsWith("." + query, StringComparison.OrdinalIgnoreCase) ||
                                           m.FullName.Contains("." + query + "(", StringComparison.OrdinalIgnoreCase))
            ?? methods.FirstOrDefault(m => m.FullName.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    public static CodeNode? ResolveTypeNode(CodeGraph graph, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        // 1. Exact ID match
        var exact = graph.FindNode(query);
        if (exact is { Kind: NodeKind.Type })
        {
            return exact;
        }

        var types = graph.GetNodes(NodeKind.Type).ToList();

        // 2. Case-insensitive full name match
        var byFullName = types.FirstOrDefault(t => t.FullName.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (byFullName is not null)
        {
            return byFullName;
        }

        // 3. Exact short class name match
        var byShortName = types.FirstOrDefault(t => t.Name.Equals(query, StringComparison.OrdinalIgnoreCase));
        if (byShortName is not null)
        {
            return byShortName;
        }

        // 4. Suffix match (e.g. "Analysis.CycleDetector")
        return types.FirstOrDefault(t =>
            t.FullName.EndsWith("." + query, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record NamespaceSummary(string Namespace, int TypeCount, int TotalLines);

public sealed record SolutionOverviewReport(
    string SnapshotId,
    string TargetPath,
    DateTimeOffset CapturedAt,
    int ProjectCount,
    int NamespaceCount,
    int TypeCount,
    int DependencyEdgeCount,
    int ProjectCycleCount,
    int NamespaceCycleCount,
    IReadOnlyList<ProjectInfo> Projects,
    IReadOnlyList<NamespaceSummary> TopNamespaces,
    IReadOnlyList<ExtractionDiagnostic> Diagnostics,
    int? MethodCount = null,
    bool MethodsIncluded = false,
    bool TopNamespacesTruncated = false,
    GraphLoadStatus? LoadStatus = null,
    int? NamespaceCycleCountRaw = null,
    IReadOnlyDictionary<string, int>? SuppressedNamespaceCycles = null);

public sealed record DependencyGraphEdgeDto(string From, string To);

public sealed record DependencyGraphReport(
    NodeKind Scope,
    int TotalNodesInScope,
    int ReturnedNodeCount,
    bool Truncated,
    IReadOnlyList<CycleInfo> Cycles,
    IReadOnlyList<string> Nodes,
    IReadOnlyList<DependencyGraphEdgeDto> Edges,
    int? TotalEdgeCount = null,
    int? ReturnedEdgeCount = null,
    bool? EdgesTruncated = null,
    int? TotalCycleCount = null,
    IReadOnlyDictionary<string, int>? SuppressedCycles = null,
    int? TotalNodesBeforeFilter = null,
    string? Note = null);

public sealed record HotspotsReport(
    NodeKind Scope,
    string SortBy,
    int TotalEvaluated,
    IReadOnlyList<NodeMetrics> Items,
    string? Note = null);

public sealed record EvaluatedRuleSummary(
    string Id,
    string Name,
    RuleSeverity Severity,
    int ViolationCount);

public sealed record RuleCheckReport(
    int RulesEvaluated,
    int TotalViolations,
    int ErrorCount,
    int WarningCount,
    int InfoCount,
    bool Truncated,
    IReadOnlyList<RuleViolation> Violations,
    IReadOnlyList<string>? SkippedDegradedProjects = null,
    IReadOnlyDictionary<string, int>? SuppressedCycles = null,
    IReadOnlyList<EvaluatedRuleSummary>? EvaluatedRules = null,
    string? Note = null,
    bool VacuousConfiguration = false,
    IReadOnlyList<DeadCodeAcknowledgement>? AcknowledgedDeadCode = null,
    bool ConfigurationError = false);

public sealed record SymbolDependencyDto(
    string TargetId,
    IReadOnlyList<string> Kinds,
    int Weight,
    SourceLocation? Location);

public sealed record SymbolInspectionReport(
    CodeNode Node,
    NodeMetrics? Metrics,
    IReadOnlyList<SymbolDependencyDto> OutgoingDependencies,
    IReadOnlyList<SymbolDependencyDto> IncomingDependents);
