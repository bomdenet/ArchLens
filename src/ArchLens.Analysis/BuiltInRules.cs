using ArchLens.Model;

namespace ArchLens.Analysis;

public sealed class ForbidDependencyRule : IArchitectureRule
{
    private readonly IReadOnlyList<string> _fromPatterns;
    private readonly IReadOnlyList<string> _forbiddenTargets;
    private readonly IReadOnlyList<string>? _excludeFromPatterns;

    public ForbidDependencyRule(
        string id,
        string name,
        string description,
        RuleSeverity severity,
        string fromPattern,
        IReadOnlyList<string> forbiddenTargets,
        IReadOnlyList<string>? excludeFromPatterns = null)
        : this(id, name, description, severity, [fromPattern], forbiddenTargets, excludeFromPatterns)
    {
    }

    public ForbidDependencyRule(
        string id,
        string name,
        string description,
        RuleSeverity severity,
        IReadOnlyList<string> fromPatterns,
        IReadOnlyList<string> forbiddenTargets,
        IReadOnlyList<string>? excludeFromPatterns = null)
    {
        Id = id;
        Name = name;
        Description = description;
        Severity = severity;
        _fromPatterns = fromPatterns;
        _forbiddenTargets = forbiddenTargets;
        _excludeFromPatterns = excludeFromPatterns;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public RuleSeverity Severity { get; }

    public IEnumerable<RuleViolation> Evaluate(CodeGraph graph, IReadOnlyDictionary<string, NodeMetrics> metrics)
    {
        var seenPairs = new HashSet<(string From, string To)>();

        foreach (var edge in graph.Edges)
        {
            if (edge.Kind == DependencyKind.ProjectReference)
            {
                continue;
            }

            var from = graph.FindNode(edge.FromId);
            var to = graph.FindNode(edge.ToId);
            if (from is null || to is null || from.Kind != NodeKind.Type || to.Kind != NodeKind.Type)
            {
                continue;
            }

            if (!GlobMatcher.MatchesAny(from.FullName, _fromPatterns))
            {
                continue;
            }

            if (_excludeFromPatterns is { Count: > 0 } && GlobMatcher.MatchesAny(from.FullName, _excludeFromPatterns))
            {
                continue;
            }

            if (!GlobMatcher.MatchesAny(to.FullName, _forbiddenTargets))
            {
                continue;
            }

            if (!seenPairs.Add((from.Id, to.Id)))
            {
                continue;
            }

            yield return new RuleViolation(
                RuleId: Id,
                RuleName: Name,
                Severity: Severity,
                Message: $"Forbidden dependency: '{from.FullName}' depends on '{to.FullName}' ({edge.Kind}).",
                NodeId: from.Id,
                TargetNodeId: to.Id,
                Location: edge.Location ?? from.Location);
        }
    }
}

public sealed class AllowOnlyDependenciesRule(
    string id,
    string name,
    string description,
    RuleSeverity severity,
    string fromPattern,
    IReadOnlyList<string> allowedTargets) : IArchitectureRule
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public RuleSeverity Severity { get; } = severity;

    public IEnumerable<RuleViolation> Evaluate(CodeGraph graph, IReadOnlyDictionary<string, NodeMetrics> metrics)
    {
        var seenPairs = new HashSet<(string From, string To)>();

        foreach (var edge in graph.Edges)
        {
            if (edge.Kind == DependencyKind.ProjectReference)
            {
                continue;
            }

            var from = graph.FindNode(edge.FromId);
            var to = graph.FindNode(edge.ToId);
            if (from is null || to is null || from.Kind != NodeKind.Type || to.Kind != NodeKind.Type)
            {
                continue;
            }

            if (!GlobMatcher.IsMatch(from.FullName, fromPattern))
            {
                continue;
            }

            // Always allow self-layer dependencies matching fromPattern
            if (GlobMatcher.IsMatch(to.FullName, fromPattern) || GlobMatcher.MatchesAny(to.FullName, allowedTargets))
            {
                continue;
            }

            if (!seenPairs.Add((from.Id, to.Id)))
            {
                continue;
            }

            yield return new RuleViolation(
                RuleId: Id,
                RuleName: Name,
                Severity: Severity,
                Message: $"Disallowed dependency: '{from.FullName}' may only depend on [{string.Join(", ", allowedTargets)}], but depends on '{to.FullName}' ({edge.Kind}).",
                NodeId: from.Id,
                TargetNodeId: to.Id,
                Location: edge.Location ?? from.Location);
        }
    }
}

public sealed class NoCyclesRule(
    string id,
    string name,
    string description,
    RuleSeverity severity,
    NodeKind scope,
    string? targetPattern = null,
    bool excludeMigrations = true,
    bool crossProjectOnly = false,
    bool excludeParentChild = true) : IArchitectureRule
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public RuleSeverity Severity { get; } = severity;
    public NodeKind Scope { get; } = scope;

    public IEnumerable<RuleViolation> Evaluate(CodeGraph graph, IReadOnlyDictionary<string, NodeMetrics> metrics) =>
        EvaluateWithSuppressed(graph, metrics, suppressedCounts: null);

    public IEnumerable<RuleViolation> EvaluateWithSuppressed(
        CodeGraph graph,
        IReadOnlyDictionary<string, NodeMetrics> metrics,
        Dictionary<string, int>? suppressedCounts,
        HashSet<(string Reason, string CycleKey)>? seenSuppressed = null)
    {
        Dictionary<string, HashSet<string>>? projectsByNamespace = null;
        if (crossProjectOnly && Scope == NodeKind.Namespace)
        {
            projectsByNamespace = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var t in graph.GetNodes(NodeKind.Type))
            {
                if (string.IsNullOrWhiteSpace(t.Namespace)) continue;
                if (!projectsByNamespace.TryGetValue(t.Namespace, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    projectsByNamespace[t.Namespace] = set;
                }
                set.Add(t.ProjectName);
            }
        }

        void RecordSuppressed(string reason, CycleInfo cycle)
        {
            if (suppressedCounts is null) return;
            var cycleKey = $"{cycle.Scope}:{string.Join("|", cycle.Nodes)}";
            if (seenSuppressed is not null && !seenSuppressed.Add((reason, cycleKey)))
            {
                return;
            }
            suppressedCounts[reason] = suppressedCounts.GetValueOrDefault(reason, 0) + 1;
        }

        foreach (var cycle in CycleDetector.FindCycles(graph, Scope))
        {
            if (excludeMigrations && Scope == NodeKind.Namespace &&
                CycleDetector.IsMigrationNamespaceCycle(cycle.Nodes))
            {
                RecordSuppressed("migrations", cycle);
                continue;
            }

            if (excludeParentChild && Scope == NodeKind.Namespace &&
                CycleDetector.IsParentChildNamespaceCycle(cycle.Nodes))
            {
                RecordSuppressed("parent_child", cycle);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(targetPattern) &&
                !cycle.Nodes.Any(n => GlobMatcher.IsMatch(n, targetPattern)))
            {
                RecordSuppressed("out_of_scope", cycle);
                continue;
            }

            if (projectsByNamespace is not null)
            {
                var distinctProjects = cycle.Nodes
                    .SelectMany(ns => projectsByNamespace.TryGetValue(ns, out var set) ? set : [])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                if (distinctProjects <= 1)
                {
                    RecordSuppressed("intra_project", cycle);
                    continue;
                }
            }

            var pathDisplay = cycle.CyclePath is { Count: >= 2 }
                ? string.Join(" -> ", cycle.CyclePath)
                : string.Join(" -> ", cycle.Nodes);

            var edgeSummary = cycle.SampleEdges is { Count: > 0 }
                ? " | Edges: " + string.Join("; ", cycle.SampleEdges.Take(4).Select(e =>
                    e.Location is not null
                        ? $"{e.FromType} -> {e.ToType} ({e.Location.ToShortString()})"
                        : $"{e.FromType} -> {e.ToType}"))
                : string.Empty;

            var firstLoc = cycle.SampleEdges?.FirstOrDefault(e => e.Location is not null)?.Location;
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["nodeCount"] = cycle.Nodes.Count.ToString(),
                ["internalEdges"] = cycle.InternalEdgeCount.ToString()
            };
            if (cycle.CyclePath is { Count: >= 2 })
            {
                details["cyclePath"] = pathDisplay;
            }

            yield return new RuleViolation(
                RuleId: Id,
                RuleName: Name,
                Severity: Severity,
                Message: $"{Scope} cycle detected ({cycle.Nodes.Count} nodes, {cycle.InternalEdgeCount} edges): {pathDisplay}{edgeSummary}",
                NodeId: cycle.CyclePath?.FirstOrDefault() ?? cycle.Nodes.FirstOrDefault(),
                Location: firstLoc,
                Details: details);
        }
    }
}

public sealed class MaxMetricRule : IArchitectureRule
{
    private readonly string _metricName;
    private readonly double _threshold;
    private readonly IReadOnlyList<string>? _targetPatterns;
    private readonly IReadOnlyList<string>? _excludePatterns;
    private readonly NodeKind _scope;
    private readonly bool _excludeEfAndGenerated;

    public MaxMetricRule(
        string id,
        string name,
        string description,
        RuleSeverity severity,
        string metricName,
        double threshold,
        string? targetPattern = null,
        NodeKind scope = NodeKind.Type,
        bool excludeEfAndGenerated = true,
        IReadOnlyList<string>? excludePatterns = null)
        : this(id, name, description, severity, metricName, threshold,
            string.IsNullOrWhiteSpace(targetPattern) ? null : [targetPattern], scope, excludeEfAndGenerated, excludePatterns)
    {
    }

    public MaxMetricRule(
        string id,
        string name,
        string description,
        RuleSeverity severity,
        string metricName,
        double threshold,
        IReadOnlyList<string>? targetPatterns,
        NodeKind scope = NodeKind.Type,
        bool excludeEfAndGenerated = true,
        IReadOnlyList<string>? excludePatterns = null)
    {
        Id = id;
        Name = name;
        Description = description;
        Severity = severity;
        _metricName = metricName;
        _threshold = threshold;
        _targetPatterns = targetPatterns;
        _scope = scope;
        _excludeEfAndGenerated = excludeEfAndGenerated;
        _excludePatterns = excludePatterns;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public RuleSeverity Severity { get; }
    public NodeKind Scope => _scope;

    public IEnumerable<RuleViolation> Evaluate(CodeGraph graph, IReadOnlyDictionary<string, NodeMetrics> metrics)
    {
        var normalizedMetric = _metricName.ToLowerInvariant();
        var isApproximateIl = graph.LoadStatus?.Approximate == true;

        // IL backend cannot compute cognitive complexity (always 0); skip to avoid misleading passes.
        if (isApproximateIl && normalizedMetric is "cognitive_complexity" or "max_method_cognitive_complexity" or "total_cognitive_complexity")
        {
            yield break;
        }

        IEnumerable<NodeMetrics> sourceMetrics = metrics.Values;
        if (_scope == NodeKind.Method && graph.MethodsIncluded)
        {
            sourceMetrics = new MetricsEngine().CalculateMethodMetrics(graph).Values;
        }
        else if (_scope == NodeKind.Namespace)
        {
            sourceMetrics = new MetricsEngine().CalculateNamespaceMetrics(graph).Values;
        }

        var canonicalDisplayName = (_scope, normalizedMetric) switch
        {
            (NodeKind.Type, "cyclomatic_complexity") => "max_method_cyclomatic_complexity",
            (NodeKind.Type, "cognitive_complexity") => "max_method_cognitive_complexity",
            _ => _metricName
        };

        var isIlApproximateMetric = isApproximateIl &&
            normalizedMetric is "cyclomatic_complexity" or "max_method_cyclomatic_complexity" or "total_cyclomatic_complexity";

        foreach (var m in sourceMetrics)
        {
            if (_targetPatterns is { Count: > 0 } && !GlobMatcher.MatchesAny(m.Name, _targetPatterns))
            {
                continue;
            }

            if (_excludePatterns is { Count: > 0 } && GlobMatcher.MatchesAny(m.Name, _excludePatterns))
            {
                continue;
            }

            if (_excludeEfAndGenerated && ShouldExcludeEfOrGeneratedNode(graph, m, normalizedMetric))
            {
                continue;
            }

            double? value = normalizedMetric switch
            {
                "cyclomatic_complexity" or "max_method_cyclomatic_complexity" => m.MaxMethodComplexity,
                "total_cyclomatic_complexity" => m.CyclomaticComplexity,
                "cognitive_complexity" or "max_method_cognitive_complexity" => m.MaxMethodCognitiveComplexity,
                "total_cognitive_complexity" => m.CognitiveComplexity,
                "lcom4" => m.Lcom4,
                "efferent_coupling" or "ce" => m.EfferentCoupling,
                "afferent_coupling" or "ca" => m.AfferentCoupling,
                "instability" => m.Instability,
                "line_count" or "loc" => m.LineCount,
                "constructor_params" or "ctor_params" => m.ConstructorParameterCount,
                _ => m.CustomMetrics is not null && m.CustomMetrics.TryGetValue(_metricName, out var customVal)
                    ? customVal
                    : null
            };

            if (value is null || double.IsNaN(value.Value) || value.Value <= _threshold)
            {
                continue;
            }

            Dictionary<string, string>? details = null;
            if (isIlApproximateMetric)
            {
                details = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["approximate"] = "true",
                    ["backend"] = "il"
                };
            }

            yield return new RuleViolation(
                RuleId: Id,
                RuleName: Name,
                Severity: Severity,
                Message: $"Metric '{canonicalDisplayName}' on '{m.Name}' is {value.Value:0.##}, exceeding threshold {_threshold:0.##}.",
                NodeId: m.NodeId,
                Location: m.Location,
                Details: details);
        }
    }

    internal static bool ShouldExcludeEfOrGeneratedNode(CodeGraph graph, NodeMetrics m, string normalizedMetric)
    {
        var node = graph.FindNode(m.NodeId);
        var filePath = m.Location?.FilePath ?? node?.Location?.FilePath ?? string.Empty;
        var normPath = filePath.Replace('\\', '/');

        if ((node is not null && EntryPointDetectors.HasMetadataFlag(node, "isMigration")) ||
            normPath.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase) ||
            normPath.StartsWith("Migrations/", StringComparison.OrdinalIgnoreCase) ||
            normPath.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) ||
            m.Name.Contains(".Migrations.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if ((node is not null && EntryPointDetectors.HasMetadataFlag(node, "isJsonSerializerContext")) ||
            (node is not null && EntryPointDetectors.HasAnyBaseTypeContaining(node, ["JsonSerializerContext"])))
        {
            return true;
        }

        if (normalizedMetric == "lcom4" &&
            ((node is not null && EntryPointDetectors.HasMetadataFlag(node, "isDbContext")) ||
             (node is not null && EntryPointDetectors.HasAnyBaseTypeContaining(node, ["Microsoft.EntityFrameworkCore.DbContext", "IdentityDbContext"])) ||
             m.Name.EndsWith("DbContext", StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }
}

public sealed class GodClassRule(
    string id,
    string name,
    string description,
    RuleSeverity severity,
    int maxLines = 500,
    int maxComplexity = 50,
    int maxCoupling = 20,
    int minConditions = 3,
    IReadOnlyList<string>? targetPatterns = null,
    IReadOnlyList<string>? excludePatterns = null,
    bool excludeEfAndGenerated = true) : IArchitectureRule
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public RuleSeverity Severity { get; } = severity;

    public IEnumerable<RuleViolation> Evaluate(CodeGraph graph, IReadOnlyDictionary<string, NodeMetrics> metrics)
    {
        var required = Math.Clamp(minConditions, 1, 3);
        foreach (var m in metrics.Values)
        {
            if (m.Kind != NodeKind.Type) continue;
            if (targetPatterns is { Count: > 0 } && !GlobMatcher.MatchesAny(m.Name, targetPatterns)) continue;
            if (excludePatterns is { Count: > 0 } && GlobMatcher.MatchesAny(m.Name, excludePatterns)) continue;
            if (excludeEfAndGenerated && MaxMetricRule.ShouldExcludeEfOrGeneratedNode(graph, m, "god_class")) continue;

            var hitLines = m.LineCount > maxLines;
            var hitCc = m.CyclomaticComplexity > maxComplexity;
            var hitCe = m.EfferentCoupling > maxCoupling;
            var count = (hitLines ? 1 : 0) + (hitCc ? 1 : 0) + (hitCe ? 1 : 0);

            if (count < required) continue;

            var locPart = hitLines ? $"LOC={m.LineCount} (>{maxLines})" : $"LOC={m.LineCount}";
            var ccPart = hitCc ? $"total CC={m.CyclomaticComplexity} (>{maxComplexity})" : $"total CC={m.CyclomaticComplexity}";
            var cePart = hitCe ? $"Ce={m.EfferentCoupling} (>{maxCoupling})" : $"Ce={m.EfferentCoupling}";

            yield return new RuleViolation(
                RuleId: Id,
                RuleName: Name,
                Severity: Severity,
                Message: $"God class candidate '{m.Name}': {locPart}, {ccPart}, {cePart}.",
                NodeId: m.NodeId,
                Location: m.Location);
        }
    }
}

public sealed class NamingConventionRule : IArchitectureRule
{
    private readonly IReadOnlyList<string> _targetPatterns;
    private readonly string _requiredNamePattern;
    private readonly TypeKind? _typeKind;

    public NamingConventionRule(
        string id,
        string name,
        string description,
        RuleSeverity severity,
        string targetPattern,
        string requiredNamePattern,
        TypeKind? typeKind = null)
        : this(id, name, description, severity, [targetPattern], requiredNamePattern, typeKind)
    {
    }

    public NamingConventionRule(
        string id,
        string name,
        string description,
        RuleSeverity severity,
        IReadOnlyList<string> targetPatterns,
        string requiredNamePattern,
        TypeKind? typeKind = null)
    {
        Id = id;
        Name = name;
        Description = description;
        Severity = severity;
        _targetPatterns = targetPatterns;
        _requiredNamePattern = requiredNamePattern;
        _typeKind = typeKind;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public RuleSeverity Severity { get; }

    public IEnumerable<RuleViolation> Evaluate(CodeGraph graph, IReadOnlyDictionary<string, NodeMetrics> metrics)
    {
        foreach (var node in graph.GetNodes(NodeKind.Type))
        {
            if (_typeKind.HasValue && node.TypeKind != _typeKind.Value)
            {
                continue;
            }

            if (_targetPatterns.Count > 0 && !GlobMatcher.MatchesAny(node.FullName, _targetPatterns))
            {
                continue;
            }

            if (!GlobMatcher.IsMatch(node.Name, _requiredNamePattern))
            {
                yield return new RuleViolation(
                    RuleId: Id,
                    RuleName: Name,
                    Severity: Severity,
                    Message: $"Type '{node.FullName}' violates naming pattern '{_requiredNamePattern}'.",
                    NodeId: node.Id,
                    Location: node.Location);
            }
        }
    }
}

public sealed class NoCaptiveDependenciesRule(
    string id,
    string name,
    string description,
    RuleSeverity severity) : IArchitectureRule
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public RuleSeverity Severity { get; } = severity;

    public IEnumerable<RuleViolation> Evaluate(CodeGraph graph, IReadOnlyDictionary<string, NodeMetrics> metrics)
    {
        var report = DiGraphAnalyzer.Analyze(graph);
        foreach (var issue in report.CaptiveDependencies)
        {
            yield return new RuleViolation(
                RuleId: Id,
                RuleName: Name,
                Severity: Severity,
                Message: $"Captive dependency: {issue.DependencyPath}",
                NodeId: issue.ConsumerTypeId,
                TargetNodeId: issue.ResolvedImplementationTypeId,
                Location: issue.Location);
        }
    }
}

public sealed class NoDeadCodeRule(
    string id,
    string name,
    string description,
    RuleSeverity severity,
    NodeKind scope = NodeKind.Type,
    bool treatPublicApiAsEntryPoint = true,
    string? targetPattern = null,
    string? profile = null,
    IReadOnlyList<DeadCodeAcknowledgement>? acknowledged = null) : IArchitectureRule
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public RuleSeverity Severity { get; } = severity;
    public NodeKind Scope { get; } = scope;

    public IEnumerable<RuleViolation> Evaluate(CodeGraph graph, IReadOnlyDictionary<string, NodeMetrics> metrics) =>
        Evaluate(graph, metrics, extraDetectors: null);

    public IEnumerable<RuleViolation> Evaluate(
        CodeGraph graph,
        IReadOnlyDictionary<string, NodeMetrics> metrics,
        IEnumerable<IEntryPointDetector>? extraDetectors)
    {
        var analyzer = new DeadCodeAnalyzer(extraDetectors);
        var report = analyzer.Analyze(graph, Scope, treatPublicApiAsEntryPoint, targetPattern, int.MaxValue, profile: profile, acknowledged: acknowledged);
        foreach (var candidate in report.Candidates)
        {
            yield return new RuleViolation(
                RuleId: Id,
                RuleName: Name,
                Severity: Severity,
                Message: $"Potentially dead {candidate.Kind.ToString().ToLowerInvariant()} '{candidate.FullName}': {candidate.Reason}",
                NodeId: candidate.NodeId,
                Location: candidate.Location);
        }
    }
}
