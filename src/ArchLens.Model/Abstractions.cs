namespace ArchLens.Model;

public sealed record ExtractionOptions(
    bool IncludeTestProjects = false,
    bool IncludeGeneratedCode = false,
    bool IncludeMethods = false,
    string? TargetFramework = null,
    bool IncludeAnalyzerDiagnostics = false,
    IReadOnlyList<string>? AnalyzerCategories = null);

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
/// Optional extension for extractors that can selectively re-extract only changed projects/files
/// while preserving unchanged portions of a previous snapshot.
/// </summary>
public interface IIncrementalCodeGraphExtractor : ICodeGraphExtractor
{
    Task<ArchitectureSnapshot> ExtractIncrementalAsync(
        string targetPath,
        ArchitectureSnapshot previousSnapshot,
        IReadOnlySet<string> changedFilePaths,
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

/// <summary>
/// Extension point for identifying entry points (e.g., Main, ASP.NET endpoints, MediatR/MassTransit handlers,
/// test methods, DI registrations, or public library APIs) used by dead-code reachability analysis.
/// </summary>
public interface IEntryPointDetector
{
    string Name { get; }
    bool IsEntryPoint(CodeNode node, CodeGraph graph);
}
