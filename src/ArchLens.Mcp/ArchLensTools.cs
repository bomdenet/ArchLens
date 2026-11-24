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
    [Description("Load (or read cached with auto-invalidation) C# solution/project and return a compact architectural overview: projects, references, top namespaces, cycle counts, and workspace load diagnostics.")]
    public async Task<string> GetOverviewAsync(
        [Description("Path to .slnx, .sln, .csproj, or repository directory.")] string path,
        [Description("Set true to force a full reload from disk (disk changes are normally auto-detected).")] bool refresh = false,
        [Description("Include test projects in the graph (default: false).")] bool includeTests = false,
        [Description("Include method-level call graph nodes (default: false).")] bool includeMethods = false,
        [Description("Optional target framework moniker for multi-targeted projects (e.g. 'net8.0', 'net10.0').")] string? targetFramework = null,
        CancellationToken cancellationToken = default)
    {
        var opts = new ExtractionOptions(IncludeTestProjects: includeTests, IncludeMethods: includeMethods, TargetFramework: targetFramework);
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
        var report = analyzer.GetOverview(snapshot);
        return Serialize(report);
    }

    [McpServerTool(Name = "archlens_dependency_graph")]
    [Description("Return the dependency graph and all detected cycles at project, namespace, type, or method scope. Use filter and maxNodes to stay within token budget.")]
    public async Task<string> GetDependencyGraphAsync(
        [Description("Path to .slnx, .sln, .csproj, or repository directory.")] string path,
        [Description("Granularity: 'project', 'namespace' (default), 'type', or 'method'.")] string scope = "namespace",
        [Description("Optional glob filter (e.g. 'MyApp.Domain.*').")] string? filter = null,
        [Description("Maximum number of nodes to return (default: 50).")] int maxNodes = 50,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        CancellationToken cancellationToken = default)
    {
        var nodeScope = ParseScope(scope);
        var opts = new ExtractionOptions(IncludeMethods: nodeScope == NodeKind.Method, TargetFramework: targetFramework);
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
        var report = analyzer.GetDependencyGraph(snapshot.Graph, nodeScope, filter, Math.Clamp(maxNodes, 1, 200));
        return Serialize(report);
    }

    [McpServerTool(Name = "archlens_hotspots")]
    [Description("Find top architectural hotspots (cyclomatic/cognitive complexity, LCOM4 cohesion, excessive coupling, god classes, unstable namespaces, bloated constructors).")]
    public async Task<string> GetHotspotsAsync(
        [Description("Path to .slnx, .sln, .csproj, or repository directory.")] string path,
        [Description("Metric to rank by: 'complexity' (default), 'cognitive', 'lcom4', 'coupling', 'afferent', 'instability', 'distance', 'lines', or 'ctor'.")] string sortBy = "complexity",
        [Description("Granularity: 'type' (default), 'namespace', or 'method'.")] string scope = "type",
        [Description("Optional glob filter (e.g. 'MyApp.*').")] string? filter = null,
        [Description("Number of top hotspots to return (default: 15).")] int topN = 15,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        CancellationToken cancellationToken = default)
    {
        var nodeScope = scope.Trim().ToLowerInvariant() switch
        {
            "namespace" => NodeKind.Namespace,
            "method" => NodeKind.Method,
            _ => NodeKind.Type
        };
        var opts = new ExtractionOptions(IncludeMethods: nodeScope == NodeKind.Method, TargetFramework: targetFramework);
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
        var report = analyzer.GetHotspots(snapshot.Graph, sortBy, filter, nodeScope, Math.Clamp(topN, 1, 100));
        return Serialize(report);
    }

    [McpServerTool(Name = "archlens_check_rules")]
    [Description("Evaluate architecture rules from .archlens.yaml (auto-discovered in repo root or explicit path), optionally aggregating Roslyn analyzer diagnostics. Falls back to built-in cycle checks if no rule file exists.")]
    public async Task<string> CheckRulesAsync(
        [Description("Path to .slnx, .sln, .csproj, or repository directory.")] string path,
        [Description("Optional path to a .archlens.yaml rule file. Auto-discovered if omitted.")] string? rulesPath = null,
        [Description("Maximum violations to return (default: 50).")] int maxViolations = 50,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Include test projects in the graph (default: false).")] bool includeTests = false,
        [Description("Include method-level call graph nodes (auto-enabled if rules require method graph).")] bool includeMethods = false,
        [Description("Include Roslyn analyzer diagnostics (Design, Maintainability, Performance) in the report (default: false).")] bool includeAnalyzers = false,
        [Description("Optional comma-separated Roslyn analyzer categories (default: 'Design,Maintainability,Performance'; use 'all' for everything).")] string? analyzerCategories = null,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        CancellationToken cancellationToken = default)
    {
        var (rules, source) = LoadRulesOrDefaults(path, rulesPath);
        var needsMethods = includeMethods || RulesRequireMethodGraph(rules);
        var categories = ParseCategories(analyzerCategories);
        var opts = new ExtractionOptions(
            IncludeTestProjects: includeTests,
            IncludeMethods: needsMethods,
            TargetFramework: targetFramework,
            IncludeAnalyzerDiagnostics: includeAnalyzers,
            AnalyzerCategories: categories);
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
        var report = analyzer.CheckRules(snapshot.Graph, rules, Math.Clamp(maxViolations, 1, 200), includeAnalyzers);
        return Serialize(new { rulesSource = source, report });
    }

    [McpServerTool(Name = "archlens_dead_code")]
    [Description("Detect potentially dead / unreferenced types or methods using reachability analysis from built-in entry point detectors (Main, ASP.NET Controllers & Minimal APIs, MediatR/MassTransit handlers, tests, DI registrations, public library APIs).")]
    public async Task<string> AnalyzeDeadCodeAsync(
        [Description("Path to .slnx, .sln, .csproj, or repository directory.")] string path,
        [Description("Granularity: 'type' (default) or 'method'.")] string scope = "type",
        [Description("Treat public symbols in library (DLL) projects as entry points (default: true). Set false to audit unused public symbols too.")] bool treatPublicApiAsEntryPoint = true,
        [Description("Optional glob filter (e.g. 'MyApp.*').")] string? filter = null,
        [Description("Maximum candidates to return (default: 50).")] int maxItems = 50,
        [Description("Include test projects in the graph (default: false).")] bool includeTests = false,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        CancellationToken cancellationToken = default)
    {
        var nodeScope = scope.Equals("method", StringComparison.OrdinalIgnoreCase) ? NodeKind.Method : NodeKind.Type;
        var opts = new ExtractionOptions(
            IncludeTestProjects: includeTests,
            IncludeMethods: nodeScope == NodeKind.Method,
            TargetFramework: targetFramework);
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
        var report = analyzer.AnalyzeDeadCode(snapshot.Graph, nodeScope, treatPublicApiAsEntryPoint, filter, Math.Clamp(maxItems, 1, 200));
        return Serialize(report);
    }

    [McpServerTool(Name = "archlens_di_analysis")]
    [Description("Analyze IServiceCollection dependency injection registrations (AddSingleton/AddScoped/AddTransient/AddHostedService), detect captive dependencies (Scoped captured by Singleton directly or via Transient), and list single-implementation interfaces.")]
    public async Task<string> AnalyzeDiAsync(
        [Description("Path to .slnx, .sln, .csproj, or repository directory.")] string path,
        [Description("Include test projects in the graph (default: false).")] bool includeTests = false,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        CancellationToken cancellationToken = default)
    {
        var opts = new ExtractionOptions(IncludeTestProjects: includeTests, TargetFramework: targetFramework);
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
        var report = analyzer.AnalyzeDi(snapshot.Graph);
        return Serialize(report);
    }

    [McpServerTool(Name = "archlens_analyzers")]
    [Description("Run Roslyn DiagnosticAnalyzers (Microsoft.CodeAnalysis.NetAnalyzers, Roslynator, etc.) on the solution and aggregate architectural diagnostics filtered by category.")]
    public async Task<string> GetAnalyzerDiagnosticsAsync(
        [Description("Path to .slnx, .sln, .csproj, or repository directory.")] string path,
        [Description("Comma-separated categories (default: 'Design,Maintainability,Performance'; use 'all' for everything).")] string? categories = null,
        [Description("Maximum diagnostics to return (default: 60).")] int maxItems = 60,
        [Description("Include test projects in the graph (default: false).")] bool includeTests = false,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        CancellationToken cancellationToken = default)
    {
        var catList = ParseCategories(categories);
        var opts = new ExtractionOptions(
            IncludeTestProjects: includeTests,
            TargetFramework: targetFramework,
            IncludeAnalyzerDiagnostics: true,
            AnalyzerCategories: catList);
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
        var report = analyzer.GetAnalyzerDiagnostics(snapshot.Graph, catList, Math.Clamp(maxItems, 1, 200));
        return Serialize(report);
    }

    [McpServerTool(Name = "archlens_inspect_symbol")]
    [Description("Inspect a single type or method in detail: metrics (CC, cognitive complexity, LCOM4, coupling), location, outgoing dependencies, and incoming dependents.")]
    public async Task<string> InspectSymbolAsync(
        [Description("Path to .slnx, .sln, .csproj, or repository directory.")] string path,
        [Description("Full type/method name (e.g. 'ArchLens.Analysis.CycleDetector') or short class name.")] string symbol,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Include method-level call graph nodes (default: false).")] bool includeMethods = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        CancellationToken cancellationToken = default)
    {
        var opts = new ExtractionOptions(IncludeMethods: includeMethods, TargetFramework: targetFramework);
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
        var report = analyzer.InspectSymbol(snapshot.Graph, symbol);
        if (report is null)
        {
            return Serialize(new { error = $"Symbol '{symbol}' was not found in '{snapshot.TargetPath}'." });
        }

        return Serialize(report);
    }

    [McpServerTool(Name = "archlens_impact")]
    [Description("Analyze the transitive blast radius of modifying a type or method: direct and transitive dependents and affected projects.")]
    public async Task<string> AnalyzeImpactAsync(
        [Description("Path to .slnx, .sln, .csproj, or repository directory.")] string path,
        [Description("Full type/method name or short class name.")] string symbol,
        [Description("Maximum traversal depth (default: 4).")] int maxDepth = 4,
        [Description("Maximum impacted items to list (default: 40).")] int maxItems = 40,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Include method-level call graph nodes (default: false).")] bool includeMethods = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        CancellationToken cancellationToken = default)
    {
        var opts = new ExtractionOptions(IncludeMethods: includeMethods, TargetFramework: targetFramework);
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
        var result = analyzer.AnalyzeImpact(snapshot.Graph, symbol, Math.Clamp(maxDepth, 1, 10), Math.Clamp(maxItems, 1, 200));
        if (result is null)
        {
            return Serialize(new { error = $"Symbol '{symbol}' was not found in '{snapshot.TargetPath}'." });
        }

        return Serialize(result);
    }

    [McpServerTool(Name = "archlens_diff")]
    [Description("Incrementally reload changed files from disk and compare architectural health against the session baseline or a saved .archlens-baseline.json file. Can also persist a new baseline to disk.")]
    public async Task<string> DiffAgainstBaselineAsync(
        [Description("Path to .slnx, .sln, .csproj, or repository directory.")] string path,
        [Description("Optional path to .archlens.yaml rule file.")] string? rulesPath = null,
        [Description("Set true to reset the baseline to the newly loaded snapshot instead of comparing.")] bool resetBaseline = false,
        [Description("Optional path to a saved .archlens-baseline.json file (auto-discovered if no in-memory baseline exists).")] string? baselineFile = null,
        [Description("Set true to persist the current snapshot as .archlens-baseline.json on disk.")] bool saveBaseline = false,
        [Description("Include test projects in the graph (default: false).")] bool includeTests = false,
        [Description("Include method-level call graph nodes (auto-enabled if rules require method graph).")] bool includeMethods = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        CancellationToken cancellationToken = default)
    {
        var (rules, source) = LoadRulesOrDefaults(path, rulesPath);
        var needsMethods = includeMethods || RulesRequireMethodGraph(rules);
        var opts = new ExtractionOptions(IncludeTestProjects: includeTests, IncludeMethods: needsMethods, TargetFramework: targetFramework);

        // Capture any existing in-memory baseline BEFORE GetOrLoadAsync (which auto-initializes session baseline)
        var existingSessionBaseline = sessions.GetBaseline(path, opts);
        var current = await sessions.GetOrLoadAsync(path, forceRefresh: false, opts, cancellationToken);

        if (saveBaseline)
        {
            var savePath = !string.IsNullOrWhiteSpace(baselineFile)
                ? Path.GetFullPath(baselineFile)
                : Path.Combine(current.Graph.RootPath, BaselineStore.DefaultFileName);
            BaselineStore.Save(savePath, current);
            sessions.SetBaseline(path, current, opts);
            return Serialize(new
            {
                status = "baseline_saved",
                baselineFile = savePath,
                snapshotId = current.SnapshotId,
                capturedAt = current.CapturedAt
            });
        }

        if (resetBaseline)
        {
            sessions.SetBaseline(path, current, opts);
            return Serialize(new
            {
                status = "baseline_set",
                snapshotId = current.SnapshotId,
                capturedAt = current.CapturedAt
            });
        }

        ArchitectureSnapshot? baseline = null;
        string baselineSource = "session";
        if (!string.IsNullOrWhiteSpace(baselineFile))
        {
            var resolved = Path.GetFullPath(baselineFile);
            baseline = BaselineStore.Load(resolved);
            baselineSource = resolved;
        }
        else if (existingSessionBaseline is not null)
        {
            baseline = existingSessionBaseline;
        }
        else if (BaselineStore.DiscoverBaselineFile(path) is { } discovered)
        {
            baseline = BaselineStore.Load(discovered);
            baselineSource = discovered;
        }

        if (baseline is null)
        {
            sessions.SetBaseline(path, current, opts);
            return Serialize(new
            {
                status = "baseline_set",
                snapshotId = current.SnapshotId,
                capturedAt = current.CapturedAt
            });
        }

        var diff = analyzer.DiffSnapshots(baseline, current, rules);
        return Serialize(new { rulesSource = source, baselineSource, diff });
    }

    private static bool RulesRequireMethodGraph(IReadOnlyList<IArchitectureRule> rules) =>
        rules.OfType<NoCyclesRule>().Any(r => r.Scope == NodeKind.Method) ||
        rules.OfType<NoDeadCodeRule>().Any(r => r.Scope == NodeKind.Method);

    private static IReadOnlyList<string>? ParseCategories(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

    private static NodeKind ParseScope(string scope, NodeKind defaultScope = NodeKind.Namespace) => scope.Trim().ToLowerInvariant() switch
    {
        "project" => NodeKind.Project,
        "namespace" => NodeKind.Namespace,
        "type" => NodeKind.Type,
        "method" => NodeKind.Method,
        _ => defaultScope
    };

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);
}
