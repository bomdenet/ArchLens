namespace ArchLens.Model;

public sealed record ExtractionOptions(
    bool IncludeTestProjects = false,
    bool IncludeGeneratedCode = false);

/// <summary>
/// Extension point for extracting a canonical CodeGraph from a target (.slnx, .sln, .csproj, or compiled assemblies).
/// </summary>
public interface ICodeGraphExtractor
{
    string Name { get; }
    bool CanHandle(string targetPath);
    Task<ArchitectureSnapshot> ExtractAsync(
        string targetPath,
        ExtractionOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Extension point for architecture rules (declarative YAML rules or custom C# plugin rules).
/// </summary>
public interface IArchitectureRule
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    RuleSeverity Severity { get; }

    IEnumerable<RuleViolation> Evaluate(
        CodeGraph graph,
        IReadOnlyDictionary<string, NodeMetrics> metrics);
}

/// <summary>
/// Extension point for loading rule sets from config files or external providers.
/// </summary>
public interface IRuleSetProvider
{
    bool CanLoad(string path);
    IReadOnlyList<IArchitectureRule> LoadRules(string path);
}

/// <summary>
/// Extension point for custom numeric metrics attached to nodes.
/// </summary>
public interface ICustomMetricCalculator
{
    string MetricName { get; }
    string Description { get; }
    IReadOnlyDictionary<string, double> Calculate(CodeGraph graph);
}
