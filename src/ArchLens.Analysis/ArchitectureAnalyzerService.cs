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
            NamespaceCycleCount: namespaceCycles.Count,
            Projects: graph.Projects,
            TopNamespaces: topNamespaces,
            Diagnostics: graph.Diagnostics,
            MethodCount: graph.MethodsIncluded ? methods.Count : null,
            MethodsIncluded: graph.MethodsIncluded,
            TopNamespacesTruncated: topNamespacesTruncated,
            LoadStatus: graph.LoadStatus);
    }

    public DependencyGraphReport GetDependencyGraph(
        CodeGraph graph,
        NodeKind scope = NodeKind.Namespace,
        string? filterPattern = null,
        int maxNodes = 60,
        int? maxEdges = null,
        bool includeCycles = true,
        int maxCycles = 20)
    {
        var adj = graph.BuildAdjacency(scope);
        var allCycles = CycleDetector.FindCycles(graph, scope);

        var filteredNodes = adj.Keys
            .Where(k => GlobMatcher.IsMatch(k, filterPattern))
            .OrderByDescending(k => adj[k].Count)
            .ThenBy(k => k, StringComparer.Ordinal)
            .ToList();

        var clampedNodes = Math.Max(1, maxNodes);
        var nodesTruncated = filteredNodes.Count > clampedNodes;
        var selectedSet = filteredNodes.Take(clampedNodes).ToHashSet(StringComparer.Ordinal);

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

        var edgesTruncated = maxEdges.HasValue && allSelectedEdges.Count > Math.Max(0, maxEdges.Value);
        var returnedEdges = edgesTruncated
            ? allSelectedEdges.Take(Math.Max(0, maxEdges!.Value)).ToList()
            : allSelectedEdges;

        var returnedCycles = includeCycles
            ? allCycles.Take(Math.Max(0, maxCycles)).ToList()
            : [];

        return new DependencyGraphReport(
            Scope: scope,
            TotalNodesInScope: adj.Count,
            ReturnedNodeCount: selectedSet.Count,
            Truncated: nodesTruncated || edgesTruncated,
            Cycles: returnedCycles,
            Nodes: selectedSet.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            Edges: returnedEdges,
            TotalEdgeCount: allSelectedEdges.Count,
            ReturnedEdgeCount: returnedEdges.Count,
            EdgesTruncated: edgesTruncated,
            TotalCycleCount: allCycles.Count);
    }

    public HotspotsReport GetHotspots(
        CodeGraph graph,
        string sortBy = "complexity",
        string? filterPattern = null,
        NodeKind scope = NodeKind.Type,
        int topN = 15,
        IEnumerable<ICustomMetricCalculator>? extraMetricCalculators = null)
    {
        var metricsMap = scope switch
        {
            NodeKind.Namespace => _metricsEngine.CalculateNamespaceMetrics(graph, extraMetricCalculators),
            NodeKind.Method => _metricsEngine.CalculateMethodMetrics(graph, extraMetricCalculators),
            _ => _metricsEngine.CalculateTypeMetrics(graph, extraMetricCalculators)
        };

        var filtered = metricsMap.Values
            .Where(m => GlobMatcher.IsMatch(m.Name, filterPattern))
            .ToList();

        var key = sortBy.ToLowerInvariant();
        IOrderedEnumerable<NodeMetrics> ordered = key switch
        {
            "cognitive" or "cognitive_complexity" => filtered.OrderByDescending(m => m.MaxMethodCognitiveComplexity).ThenByDescending(m => m.CognitiveComplexity),
            "lcom4" or "cohesion" => filtered.OrderByDescending(m => m.Lcom4).ThenByDescending(m => m.EfferentCoupling),
            "coupling" or "ce" or "efferent" => filtered.OrderByDescending(m => m.EfferentCoupling).ThenByDescending(m => m.AfferentCoupling),
            "afferent" or "ca" => filtered.OrderByDescending(m => m.AfferentCoupling).ThenByDescending(m => m.EfferentCoupling),
            "instability" => filtered.OrderByDescending(m => m.Instability).ThenByDescending(m => m.EfferentCoupling),
            "distance" => filtered.OrderByDescending(m => m.DistanceFromMainSequence ?? 0).ThenByDescending(m => m.EfferentCoupling),
            "lines" or "loc" or "size" => filtered.OrderByDescending(m => m.LineCount),
            "ctor" or "constructor_params" => filtered.OrderByDescending(m => m.ConstructorParameterCount).ThenByDescending(m => m.EfferentCoupling),
            "complexity" or "cyclomatic" or "cyclomatic_complexity" => filtered.OrderByDescending(m => m.MaxMethodComplexity).ThenByDescending(m => m.CyclomaticComplexity),
            _ => filtered.Any(m => HasValidCustomMetric(m, sortBy))
                ? filtered
                    .OrderByDescending(m => HasValidCustomMetric(m, sortBy))
                    .ThenByDescending(m => GetValidCustomMetric(m, sortBy))
                : filtered.OrderByDescending(m => m.MaxMethodComplexity).ThenByDescending(m => m.CyclomaticComplexity)
        };

        var items = ordered.Take(Math.Max(1, topN)).ToList();
        return new HotspotsReport(scope, sortBy, metricsMap.Count, items);
    }

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
        IEnumerable<IEntryPointDetector>? extraEntryPointDetectors = null)
    {
        var ruleList = rules.ToList();
        var typeMetrics = _metricsEngine.CalculateTypeMetrics(graph, extraMetricCalculators);
        var mergedDetectors = MergeDetectors(extraEntryPointDetectors);
        var allViolations = new List<RuleViolation>();

        foreach (var rule in ruleList)
        {
            try
            {
                var violations = rule is NoDeadCodeRule deadCodeRule
                    ? deadCodeRule.Evaluate(graph, typeMetrics, mergedDetectors)
                    : rule.Evaluate(graph, typeMetrics);
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

        var ordered = allViolations
            .OrderByDescending(v => v.Severity)
            .ThenBy(v => v.RuleId, StringComparer.Ordinal)
            .ThenBy(v => v.NodeId, StringComparer.Ordinal)
            .ToList();

        return new RuleCheckReport(
            RulesEvaluated: ruleList.Count,
            TotalViolations: ordered.Count,
            ErrorCount: ordered.Count(v => v.Severity == RuleSeverity.Error),
            WarningCount: ordered.Count(v => v.Severity == RuleSeverity.Warning),
            InfoCount: ordered.Count(v => v.Severity == RuleSeverity.Info),
            Truncated: ordered.Count > maxViolations,
            Violations: ordered.Take(Math.Max(1, maxViolations)).ToList());
    }

    public DeadCodeReport AnalyzeDeadCode(
        CodeGraph graph,
        NodeKind scope = NodeKind.Type,
        bool treatPublicApiAsEntryPoint = true,
        string? filterPattern = null,
        int maxItems = 100,
        IEnumerable<IEntryPointDetector>? extraDetectors = null,
        bool? testsExcluded = null)
    {
        if (extraDetectors is null)
        {
            return _deadCodeAnalyzer.Analyze(graph, scope, treatPublicApiAsEntryPoint, filterPattern, maxItems, testsExcluded: testsExcluded);
        }

        var combined = new DeadCodeAnalyzer(MergeDetectors(extraDetectors));
        return combined.Analyze(graph, scope, treatPublicApiAsEntryPoint, filterPattern, maxItems, testsExcluded: testsExcluded);
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
        int maxItems = 100)
    {
        var filter = categories is { Count: > 0 } &&
                     !categories.Any(c => c.Equals("all", StringComparison.OrdinalIgnoreCase) || c == "*")
            ? categories.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        var filtered = graph.AnalyzerDiagnostics
            .Where(d => filter is null || filter.Contains(d.Category))
            .OrderByDescending(d => d.Severity)
            .ThenBy(d => d.Id, StringComparer.Ordinal)
            .ThenBy(d => d.Location?.FilePath, StringComparer.Ordinal)
            .ToList();

        var clamped = Math.Max(1, maxItems);
        var distinctCats = filtered.Select(d => d.Category).Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AnalyzerDiagnosticsReport(
            TotalDiagnostics: filtered.Count,
            ErrorCount: filtered.Count(d => d.Severity == RuleSeverity.Error),
            WarningCount: filtered.Count(d => d.Severity == RuleSeverity.Warning),
            InfoCount: filtered.Count(d => d.Severity == RuleSeverity.Info),
            Truncated: filtered.Count > clamped,
            Categories: distinctCats,
            Diagnostics: filtered.Take(clamped).ToList());
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

    public ImpactAnalysisResult? AnalyzeImpact(CodeGraph graph, string symbolQuery, int maxDepth = 4, int maxItems = 50)
    {
        var target = ResolveNode(graph, symbolQuery);
        return target is null ? null : ImpactAnalyzer.Analyze(graph, target, maxDepth, maxItems);
    }

    public SnapshotDiffResult DiffSnapshots(
        ArchitectureSnapshot baseline,
        ArchitectureSnapshot current,
        IEnumerable<IArchitectureRule>? rules = null,
        IEnumerable<ICustomMetricCalculator>? extraMetricCalculators = null,
        IEnumerable<IEntryPointDetector>? extraEntryPointDetectors = null)
    {
        var baseTypes = baseline.Graph.GetNodes(NodeKind.Type).Count();
        var currTypes = current.Graph.GetNodes(NodeKind.Type).Count();

        var baseCycles = CycleDetector.FindCycles(baseline.Graph, NodeKind.Namespace);
        var currCycles = CycleDetector.FindCycles(current.Graph, NodeKind.Namespace);

        static string CycleKey(CycleInfo c) => $"{c.Scope}:{string.Join("|", c.Nodes)}";
        var baseCycleKeys = baseCycles.Select(CycleKey).ToHashSet(StringComparer.Ordinal);
        var currCycleKeys = currCycles.Select(CycleKey).ToHashSet(StringComparer.Ordinal);

        var addedCycles = currCycles.Where(c => !baseCycleKeys.Contains(CycleKey(c))).ToList();
        var resolvedCycles = baseCycles.Where(c => !currCycleKeys.Contains(CycleKey(c))).ToList();

        var ruleList = rules?.ToList() ?? [];
        var extraCalcs = extraMetricCalculators?.ToList();
        var extraDetectors = extraEntryPointDetectors?.ToList();
        var baseViolations = ruleList.Count > 0 ? CheckRules(baseline.Graph, ruleList, int.MaxValue, includeAnalyzerDiagnostics: false, extraMetricCalculators: extraCalcs, extraEntryPointDetectors: extraDetectors).Violations : [];
        var currViolations = ruleList.Count > 0 ? CheckRules(current.Graph, ruleList, int.MaxValue, includeAnalyzerDiagnostics: false, extraMetricCalculators: extraCalcs, extraEntryPointDetectors: extraDetectors).Violations : [];

        static string ViolationKey(RuleViolation v)
        {
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
            ResolvedViolations: resolvedViolations);
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
    GraphLoadStatus? LoadStatus = null);

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
    int? TotalCycleCount = null);

public sealed record HotspotsReport(
    NodeKind Scope,
    string SortBy,
    int TotalEvaluated,
    IReadOnlyList<NodeMetrics> Items);

public sealed record RuleCheckReport(
    int RulesEvaluated,
    int TotalViolations,
    int ErrorCount,
    int WarningCount,
    int InfoCount,
    bool Truncated,
    IReadOnlyList<RuleViolation> Violations);

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
