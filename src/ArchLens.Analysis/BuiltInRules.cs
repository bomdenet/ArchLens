using ArchLens.Model;

namespace ArchLens.Analysis;

public sealed class ForbidDependencyRule(
    string id,
    string name,
    string description,
    RuleSeverity severity,
    string fromPattern,
    IReadOnlyList<string> forbiddenTargets,
    IReadOnlyList<string>? excludeFromPatterns = null) : IArchitectureRule
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

            if (excludeFromPatterns is { Count: > 0 } && GlobMatcher.MatchesAny(from.FullName, excludeFromPatterns))
            {
                continue;
            }

            if (!GlobMatcher.MatchesAny(to.FullName, forbiddenTargets))
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

public sealed class MaxMetricRule(
    string id,
    string name,
    string description,
    RuleSeverity severity,
    string metricName,
    double threshold,
    string? targetPattern = null) : IArchitectureRule
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public RuleSeverity Severity { get; } = severity;

    public IEnumerable<RuleViolation> Evaluate(CodeGraph graph, IReadOnlyDictionary<string, NodeMetrics> metrics)
    {
        foreach (var m in metrics.Values)
        {
            if (!GlobMatcher.IsMatch(m.Name, targetPattern))
            {
                continue;
            }

            double? value = metricName.ToLowerInvariant() switch
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
                _ => null
            };

            if (value is null || value.Value <= threshold)
            {
                continue;
            }

            yield return new RuleViolation(
                RuleId: Id,
                RuleName: Name,
                Severity: Severity,
                Message: $"Metric '{metricName}' on '{m.Name}' is {value.Value:0.##}, exceeding threshold {threshold:0.##}.",
                NodeId: m.NodeId,
                Location: m.Location);
        }
    }
}

public sealed class NamingConventionRule(
    string id,
    string name,
    string description,
    RuleSeverity severity,
    string targetPattern,
    string requiredNamePattern,
    TypeKind? typeKind = null) : IArchitectureRule
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public RuleSeverity Severity { get; } = severity;

    public IEnumerable<RuleViolation> Evaluate(CodeGraph graph, IReadOnlyDictionary<string, NodeMetrics> metrics)
    {
        foreach (var node in graph.GetNodes(NodeKind.Type))
        {
            if (typeKind.HasValue && node.TypeKind != typeKind.Value)
            {
                continue;
            }

            if (!GlobMatcher.IsMatch(node.FullName, targetPattern))
            {
                continue;
            }

            if (!GlobMatcher.IsMatch(node.Name, requiredNamePattern))
            {
                yield return new RuleViolation(
                    RuleId: Id,
                    RuleName: Name,
                    Severity: Severity,
                    Message: $"Type '{node.FullName}' violates naming pattern '{requiredNamePattern}'.",
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

    public IEnumerable<RuleViolation> Evaluate(CodeGraph graph, IReadOnlyDictionary<string, NodeMetrics> metrics)
    {
        var analyzer = new DeadCodeAnalyzer();
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
