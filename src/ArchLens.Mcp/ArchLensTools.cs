using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArchLens.Analysis;
using ArchLens.Extraction.Roslyn;
using ArchLens.Model;
using ArchLens.Rules.Yaml;
using ModelContextProtocol.Server;

namespace ArchLens.Mcp;

/// <summary>
/// Coarse-grained, token-efficient MCP tools for AI coding agents analyzing C# architecture.
/// </summary>
[McpServerToolType]
public sealed class ArchLensTools(
    WorkspaceSessionManager sessions,
    ArchitectureAnalyzerService analyzer,
    IEnumerable<IRuleSetProvider> ruleProviders)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [McpServerTool(Name = "archlens_overview")]
    [Description("Load (or read cached) C# solution/project and return a compact architectural overview: projects, references, top namespaces, cycle counts, and workspace load diagnostics.")]
    public async Task<string> GetOverviewAsync(
        [Description("Path to .slnx, .sln, .csproj, or repository directory.")] string path,
        [Description("Set true to reload from disk after editing code.")] bool refresh = false,
        [Description("Include test projects in the graph (default: false).")] bool includeTests = false,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, new ExtractionOptions(IncludeTestProjects: includeTests), cancellationToken);
        var report = analyzer.GetOverview(snapshot);
        return Serialize(report);
    }

    [McpServerTool(Name = "archlens_dependency_graph")]
    [Description("Return the dependency graph and all detected cycles at project, namespace, or type scope. Use filter and maxNodes to stay within token budget.")]
    public async Task<string> GetDependencyGraphAsync(
        [Description("Path to .slnx, .sln, .csproj, or repository directory.")] string path,
        [Description("Granularity: 'project', 'namespace' (default), or 'type'.")] string scope = "namespace",
        [Description("Optional glob filter (e.g. 'MyApp.Domain.*').")] string? filter = null,
        [Description("Maximum number of nodes to return (default: 50).")] int maxNodes = 50,
        [Description("Set true to reload from disk.")] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, cancellationToken: cancellationToken);
        var nodeScope = ParseScope(scope);
        var report = analyzer.GetDependencyGraph(snapshot.Graph, nodeScope, filter, Math.Clamp(maxNodes, 1, 200));
        return Serialize(report);
    }

    [McpServerTool(Name = "archlens_hotspots")]
    [Description("Find top architectural hotspots (high complexity, excessive coupling, god classes, unstable namespaces, bloated constructors).")]
    public async Task<string> GetHotspotsAsync(
        [Description("Path to .slnx, .sln, .csproj, or repository directory.")] string path,
        [Description("Metric to rank by: 'complexity' (default), 'coupling', 'afferent', 'instability', 'distance', 'lines', or 'ctor'.")] string sortBy = "complexity",
        [Description("Granularity: 'type' (default) or 'namespace'.")] string scope = "type",
        [Description("Optional glob filter (e.g. 'MyApp.*').")] string? filter = null,
        [Description("Number of top hotspots to return (default: 15).")] int topN = 15,
        [Description("Set true to reload from disk.")] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, cancellationToken: cancellationToken);
        var nodeScope = scope.Equals("namespace", StringComparison.OrdinalIgnoreCase) ? NodeKind.Namespace : NodeKind.Type;
        var report = analyzer.GetHotspots(snapshot.Graph, sortBy, filter, nodeScope, Math.Clamp(topN, 1, 100));
        return Serialize(report);
    }

    [McpServerTool(Name = "archlens_check_rules")]
    [Description("Evaluate architecture rules from .archlens.yaml (auto-discovered in repo root or explicit path). Falls back to built-in cycle checks if no rule file exists.")]
    public async Task<string> CheckRulesAsync(
        [Description("Path to .slnx, .sln, .csproj, or repository directory.")] string path,
        [Description("Optional path to a .archlens.yaml rule file. Auto-discovered if omitted.")] string? rulesPath = null,
        [Description("Maximum violations to return (default: 50).")] int maxViolations = 50,
        [Description("Set true to reload from disk.")] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, cancellationToken: cancellationToken);
        var (rules, source) = LoadRulesOrDefaults(path, rulesPath);
        var report = analyzer.CheckRules(snapshot.Graph, rules, Math.Clamp(maxViolations, 1, 200));
        return Serialize(new { rulesSource = source, report });
    }

    [McpServerTool(Name = "archlens_inspect_symbol")]
    [Description("Inspect a single type in detail: metrics, location, outgoing dependencies, and incoming dependents.")]
    public async Task<string> InspectSymbolAsync(
        [Description("Path to .slnx, .sln, .csproj, or repository directory.")] string path,
        [Description("Full type name (e.g. 'ArchLens.Analysis.CycleDetector') or short class name.")] string symbol,
        [Description("Set true to reload from disk.")] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, cancellationToken: cancellationToken);
        var report = analyzer.InspectSymbol(snapshot.Graph, symbol);
        if (report is null)
        {
            return Serialize(new { error = $"Type '{symbol}' was not found in '{snapshot.TargetPath}'." });
        }

        return Serialize(report);
    }

    [McpServerTool(Name = "archlens_impact")]
    [Description("Analyze the transitive blast radius of modifying a type: direct and transitive dependents and affected projects.")]
    public async Task<string> AnalyzeImpactAsync(
        [Description("Path to .slnx, .sln, .csproj, or repository directory.")] string path,
        [Description("Full type name or short class name.")] string symbol,
        [Description("Maximum traversal depth (default: 4).")] int maxDepth = 4,
        [Description("Maximum impacted types to list (default: 40).")] int maxItems = 40,
        [Description("Set true to reload from disk.")] bool refresh = false,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, cancellationToken: cancellationToken);
        var result = analyzer.AnalyzeImpact(snapshot.Graph, symbol, Math.Clamp(maxDepth, 1, 10), Math.Clamp(maxItems, 1, 200));
        if (result is null)
        {
            return Serialize(new { error = $"Type '{symbol}' was not found in '{snapshot.TargetPath}'." });
        }

        return Serialize(result);
    }

    [McpServerTool(Name = "archlens_diff")]
    [Description("Reload the codebase from disk and compare architectural health (types, edges, cycles, rule violations) against the session baseline. Ideal for verifying refactoring progress.")]
    public async Task<string> DiffAgainstBaselineAsync(
        [Description("Path to .slnx, .sln, .csproj, or repository directory.")] string path,
        [Description("Optional path to .archlens.yaml rule file.")] string? rulesPath = null,
        [Description("Set true to reset the baseline to the newly loaded snapshot instead of comparing.")] bool resetBaseline = false,
        CancellationToken cancellationToken = default)
    {
        var baseline = sessions.GetBaseline(path);
        var current = await sessions.GetOrLoadAsync(path, forceRefresh: true, cancellationToken: cancellationToken);

        if (resetBaseline || baseline is null)
        {
            sessions.SetBaseline(path, current);
            return Serialize(new
            {
                status = "baseline_set",
                snapshotId = current.SnapshotId,
                capturedAt = current.CapturedAt
            });
        }

        var (rules, source) = LoadRulesOrDefaults(path, rulesPath);
        var diff = analyzer.DiffSnapshots(baseline, current, rules);
        return Serialize(new { rulesSource = source, diff });
    }

    private (IReadOnlyList<IArchitectureRule> Rules, string Source) LoadRulesOrDefaults(string targetPath, string? explicitRulesPath)
    {
        var resolvedRulesPath = !string.IsNullOrWhiteSpace(explicitRulesPath)
            ? Path.GetFullPath(explicitRulesPath)
            : YamlRuleSetProvider.DiscoverConfigFile(targetPath);

        if (resolvedRulesPath is not null)
        {
            var provider = ruleProviders.FirstOrDefault(p => p.CanLoad(resolvedRulesPath));
            if (provider is not null)
            {
                return (provider.LoadRules(resolvedRulesPath), resolvedRulesPath);
            }
        }

        IReadOnlyList<IArchitectureRule> defaults =
        [
            new NoCyclesRule("BUILTIN-001", "No project cycles", "Projects must form a DAG.", RuleSeverity.Error, NodeKind.Project),
            new NoCyclesRule("BUILTIN-002", "No namespace cycles", "Namespaces must form a DAG.", RuleSeverity.Warning, NodeKind.Namespace)
        ];

        return (defaults, "built-in defaults (no .archlens.yaml found)");
    }

    private static NodeKind ParseScope(string scope) => scope.Trim().ToLowerInvariant() switch
    {
        "project" => NodeKind.Project,
        "type" => NodeKind.Type,
        _ => NodeKind.Namespace
    };

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
