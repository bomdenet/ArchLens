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
    NodeKind scope) : IArchitectureRule
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public RuleSeverity Severity { get; } = severity;
    public NodeKind Scope { get; } = scope;

    public IEnumerable<RuleViolation> Evaluate(CodeGraph graph, IReadOnlyDictionary<string, NodeMetrics> metrics)
    {
        foreach (var cycle in CycleDetector.FindCycles(graph, Scope))
        {
            yield return new RuleViolation(
                RuleId: Id,
                RuleName: Name,
                Severity: Severity,
                Message: $"{Scope} cycle detected ({cycle.Nodes.Count} nodes, {cycle.InternalEdgeCount} edges): {string.Join(" -> ", cycle.Nodes)}",
                NodeId: cycle.Nodes.FirstOrDefault());
        }
    }
}

public sealed class MaxMetricRule : IArchitectureRule
{
    private readonly string _metricName;
    private readonly double _threshold;
    private readonly IReadOnlyList<string>? _targetPatterns;
    private readonly NodeKind _scope;

    public MaxMetricRule(
        string id,
        string name,
        string description,
        RuleSeverity severity,
        string metricName,
        double threshold,
        string? targetPattern = null,
        NodeKind scope = NodeKind.Type)
        : this(id, name, description, severity, metricName, threshold,
            string.IsNullOrWhiteSpace(targetPattern) ? null : [targetPattern], scope)
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
        NodeKind scope = NodeKind.Type)
    {
        Id = id;
        Name = name;
        Description = description;
        Severity = severity;
        _metricName = metricName;
        _threshold = threshold;
        _targetPatterns = targetPatterns;
        _scope = scope;
    }

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public RuleSeverity Severity { get; }
    public NodeKind Scope => _scope;

    public IEnumerable<RuleViolation> Evaluate(CodeGraph graph, IReadOnlyDictionary<string, NodeMetrics> metrics)
    {
        IEnumerable<NodeMetrics> sourceMetrics = metrics.Values;
        if (_scope == NodeKind.Method && graph.MethodsIncluded)
        {
            sourceMetrics = new MetricsEngine().CalculateMethodMetrics(graph).Values;
        }
        else if (_scope == NodeKind.Namespace)
        {
            sourceMetrics = new MetricsEngine().CalculateNamespaceMetrics(graph).Values;
        }

        var normalizedMetric = _metricName.ToLowerInvariant();
        var isMaxMethodOnType = _scope == NodeKind.Type &&
                                normalizedMetric is "cyclomatic_complexity" or "cognitive_complexity";

        foreach (var m in sourceMetrics)
        {
            if (_targetPatterns is { Count: > 0 } && !GlobMatcher.MatchesAny(m.Name, _targetPatterns))
            {
                continue;
            }

            double? value = normalizedMetric switch
            {
                "cyclomatic_complexity" => m.MaxMethodComplexity,
                "total_cyclomatic_complexity" => m.CyclomaticComplexity,
                "cognitive_complexity" => m.MaxMethodCognitiveComplexity,
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

            var qualifier = isMaxMethodOnType ? " (max method)" : string.Empty;
            yield return new RuleViolation(
                RuleId: Id,
                RuleName: Name,
                Severity: Severity,
                Message: $"Metric '{_metricName}'{qualifier} on '{m.Name}' is {value.Value:0.##}, exceeding threshold {_threshold:0.##}.",
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
    string? targetPattern = null) : IArchitectureRule
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
        var report = analyzer.Analyze(graph, Scope, treatPublicApiAsEntryPoint, targetPattern, int.MaxValue);
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
