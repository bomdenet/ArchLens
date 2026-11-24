using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArchLens.Analysis;
using ArchLens.Extraction.Roslyn;
using ArchLens.Model;
using ArchLens.Query;
using ArchLens.Rules.Yaml;
using ModelContextProtocol.Server;

namespace ArchLens.Mcp;

public sealed record PluginHostOptions(
    bool AllowRepoPlugins = false,
    string? ExplicitPluginsDirectory = null);

/// <summary>
/// Coarse-grained, token-efficient MCP tools for AI coding agents analyzing C# architecture.
/// Tool names omit the redundant "archlens_" prefix since MCP clients/harnesses namespace tools by server name.
/// Repository-local plugins (.archlens/plugins/*.dll) are loaded only when explicitly opted in at host startup
/// (--allow-plugins or ARCHLENS_ALLOW_PLUGINS=1) to prevent untrusted code execution.
/// </summary>
[McpServerToolType]
public sealed class ArchLensTools(
    WorkspaceSessionManager sessions,
    ArchitectureAnalyzerService analyzer,
    GraphSqlQueryEngine sqlEngine,
    IEnumerable<IRuleSetProvider> ruleProviders,
    PluginHostOptions? pluginHostOptions = null)
{
    private readonly PluginHostOptions _pluginOptions = pluginHostOptions ?? new PluginHostOptions();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [McpServerTool(Name = "overview")]
    [Description("Load (or read cached with auto-invalidation and persistent SQLite cache) C# solution/project/assemblies and return a compact architectural overview.")]
    public async Task<string> GetOverviewAsync(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Set true to force a full reload from disk (disk changes are normally auto-detected).")] bool refresh = false,
        [Description("Include test projects in the graph (default: false).")] bool includeTests = false,
        [Description("Include method-level call graph nodes (default: false).")] bool includeMethods = false,
        [Description("Optional target framework moniker for multi-targeted projects (e.g. 'net8.0', 'net10.0').")] string? targetFramework = null,
        [Description("Optional extraction backend: 'auto' (default), 'roslyn' (full source semantics), or 'il' (fast compiled .dll+.pdb reader).")] string? backend = null,
        CancellationToken cancellationToken = default)
    {
        var opts = new ExtractionOptions(IncludeTestProjects: includeTests, IncludeMethods: includeMethods, TargetFramework: targetFramework, Backend: backend);
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
        var report = analyzer.GetOverview(snapshot);
        return Serialize(report);
    }

    [McpServerTool(Name = "dependency_graph")]
    [Description("Return the dependency graph and all detected cycles at project, namespace, type, or method scope. Use filter and maxNodes to stay within token budget.")]
    public async Task<string> GetDependencyGraphAsync(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Granularity: 'project', 'namespace' (default), 'type', or 'method'.")] string scope = "namespace",
        [Description("Optional glob filter (e.g. 'MyApp.Domain.*').")] string? filter = null,
        [Description("Maximum number of nodes to return (default: 50).")] int maxNodes = 50,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        [Description("Optional extraction backend: 'auto' (default), 'roslyn', or 'il'.")] string? backend = null,
        CancellationToken cancellationToken = default)
    {
        var nodeScope = ParseScope(scope);
        var opts = new ExtractionOptions(IncludeMethods: nodeScope == NodeKind.Method, TargetFramework: targetFramework, Backend: backend);
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
        var report = analyzer.GetDependencyGraph(snapshot.Graph, nodeScope, filter, Math.Clamp(maxNodes, 1, 200));
        return Serialize(report);
    }

    [McpServerTool(Name = "hotspots")]
    [Description("Find top architectural hotspots (cyclomatic/cognitive complexity, LCOM4 cohesion, excessive coupling, god classes, unstable namespaces, bloated constructors, or custom plugin metrics).")]
    public async Task<string> GetHotspotsAsync(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Metric to rank by: 'complexity' (default), 'cognitive', 'lcom4', 'coupling', 'afferent', 'instability', 'distance', 'lines', 'ctor', or any custom plugin metric name.")] string sortBy = "complexity",
        [Description("Granularity: 'type' (default), 'namespace', or 'method'.")] string scope = "type",
        [Description("Optional glob filter (e.g. 'MyApp.*').")] string? filter = null,
        [Description("Number of top hotspots to return (default: 15).")] int topN = 15,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        [Description("Optional extraction backend: 'auto' (default), 'roslyn', or 'il'.")] string? backend = null,
        CancellationToken cancellationToken = default)
    {
        var nodeScope = scope.Trim().ToLowerInvariant() switch
        {
            "namespace" => NodeKind.Namespace,
            "method" => NodeKind.Method,
            _ => NodeKind.Type
        };
        var opts = new ExtractionOptions(IncludeMethods: nodeScope == NodeKind.Method, TargetFramework: targetFramework, Backend: backend);
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
        using var plugins = LoadRepoPlugins(snapshot.Graph.RootPath, out _);
        var report = analyzer.GetHotspots(snapshot.Graph, sortBy, filter, nodeScope, Math.Clamp(topN, 1, 100), plugins.MetricCalculators);
        return Serialize(report);
    }

    [McpServerTool(Name = "check_rules")]
    [Description("Evaluate architecture rules from .archlens.yaml, built-in architectural presets, and enabled plugins, optionally aggregating Roslyn analyzer diagnostics.")]
    public async Task<string> CheckRulesAsync(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Optional path to a .archlens.yaml rule file. Auto-discovered if omitted.")] string? rulesPath = null,
        [Description("Optional built-in architectural preset: 'clean-architecture', 'vertical-slice', 'modular-monolith', or 'ddd'.")] string? preset = null,
        [Description("Optional root namespace prefix for presets (e.g. 'MyApp').")] string? rootNamespace = null,
        [Description("Maximum violations to return (default: 50).")] int maxViolations = 50,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Include test projects in the graph (default: false).")] bool includeTests = false,
        [Description("Include method-level call graph nodes (auto-enabled if rules require method graph).")] bool includeMethods = false,
        [Description("Include Roslyn analyzer diagnostics (Design, Maintainability, Performance) in the report (default: false).")] bool includeAnalyzers = false,
        [Description("Optional comma-separated Roslyn analyzer categories (default: 'Design,Maintainability,Performance'; use 'all' for everything).")] string? analyzerCategories = null,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        [Description("Optional extraction backend: 'auto' (default), 'roslyn', or 'il'.")] string? backend = null,
        CancellationToken cancellationToken = default)
    {
        var (baseRules, source) = LoadRulesOrDefaults(path, rulesPath, preset, rootNamespace);
        var needsMethods = includeMethods || RulesRequireMethodGraph(baseRules);
        var categories = ParseCategories(analyzerCategories);
        var opts = new ExtractionOptions(
            IncludeTestProjects: includeTests,
            IncludeMethods: needsMethods,
            TargetFramework: targetFramework,
            IncludeAnalyzerDiagnostics: includeAnalyzers,
            AnalyzerCategories: categories,
            Backend: backend);
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
        using var plugins = LoadRepoPlugins(snapshot.Graph.RootPath, out var pluginNote);
        var allRules = plugins.Rules.Count > 0 ? [.. baseRules, .. plugins.Rules] : baseRules;
        var allDiags = MergePluginDiagnostics(plugins.Diagnostics, pluginNote);
        var report = analyzer.CheckRules(
            snapshot.Graph,
            allRules,
            Math.Clamp(maxViolations, 1, 200),
            includeAnalyzers,
            plugins.MetricCalculators,
            plugins.EntryPointDetectors.Count > 0 ? plugins.EntryPointDetectors : null);
        return Serialize(new
        {
            rulesSource = source,
            loadedPlugins = plugins.LoadedPluginFiles.Count > 0 ? plugins.LoadedPluginFiles : null,
            pluginDiagnostics = allDiags.Count > 0 ? allDiags : null,
            report
        });
    }

    [McpServerTool(Name = "dead_code")]
    [Description("Detect potentially dead / unreferenced types or methods using reachability analysis from built-in and enabled plugin IEntryPointDetector implementations.")]
    public async Task<string> AnalyzeDeadCodeAsync(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Granularity: 'type' (default) or 'method'.")] string scope = "type",
        [Description("Treat public symbols in library (DLL) projects as entry points (default: true). Set false to audit unused public symbols too.")] bool treatPublicApiAsEntryPoint = true,
        [Description("Optional glob filter (e.g. 'MyApp.*').")] string? filter = null,
        [Description("Maximum candidates to return (default: 50).")] int maxItems = 50,
        [Description("Include test projects in the graph (default: false).")] bool includeTests = false,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        [Description("Optional extraction backend: 'auto' (default), 'roslyn', or 'il'.")] string? backend = null,
        CancellationToken cancellationToken = default)
    {
        var nodeScope = scope.Equals("method", StringComparison.OrdinalIgnoreCase) ? NodeKind.Method : NodeKind.Type;
        var opts = new ExtractionOptions(
            IncludeTestProjects: includeTests,
            IncludeMethods: nodeScope == NodeKind.Method,
            TargetFramework: targetFramework,
            Backend: backend);
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
        using var plugins = LoadRepoPlugins(snapshot.Graph.RootPath, out _);
        var report = analyzer.AnalyzeDeadCode(snapshot.Graph, nodeScope, treatPublicApiAsEntryPoint, filter, Math.Clamp(maxItems, 1, 200),
            plugins.EntryPointDetectors.Count > 0 ? plugins.EntryPointDetectors : null);
        return Serialize(report);
    }

    [McpServerTool(Name = "query")]
    [Description("Run a read-only SQL query against the codebase's in-memory SQLite projection (tables: nodes, edges, metrics, projects, di_registrations, analyzer_diagnostics, custom_metrics). See resource archlens://schema/sql for schema and examples.")]
    public async Task<string> QueryGraphSqlAsync(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Read-only SQL SELECT / WITH query (e.g. 'SELECT name, lcom4, efferent_coupling FROM metrics WHERE kind = ''Type'' ORDER BY lcom4 DESC LIMIT 10').")] string sql,
        [Description("Maximum rows to return (default: 100, max: 500).")] int maxRows = 100,
        [Description("Include method-level nodes and call graph edges (default: false).")] bool includeMethods = false,
        [Description("Include test projects (default: false).")] bool includeTests = false,
        [Description("Include Roslyn analyzer diagnostics table (default: false).")] bool includeAnalyzers = false,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        [Description("Optional extraction backend: 'auto' (default), 'roslyn', or 'il'.")] string? backend = null,
        CancellationToken cancellationToken = default)
    {
        var opts = new ExtractionOptions(
            IncludeTestProjects: includeTests,
            IncludeMethods: includeMethods,
            TargetFramework: targetFramework,
            IncludeAnalyzerDiagnostics: includeAnalyzers,
            Backend: backend);
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
        using var plugins = LoadRepoPlugins(snapshot.Graph.RootPath, out _);
        var result = sqlEngine.Execute(snapshot.Graph, sql, maxRows, plugins.MetricCalculators, cancellationToken: cancellationToken);
        return Serialize(result);
    }

    [McpServerTool(Name = "export_diagram")]
    [Description("Export the dependency graph in human-readable diagram formats: 'mermaid', 'dot' (Graphviz), or 'structurizr' DSL, highlighting dependency cycles.")]
    public async Task<string> ExportDiagramAsync(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Output format: 'mermaid' (default), 'dot', or 'structurizr'.")] string format = "mermaid",
        [Description("Granularity: 'project', 'namespace' (default), 'type', or 'method'.")] string scope = "namespace",
        [Description("Optional glob filter (e.g. 'MyApp.*').")] string? filter = null,
        [Description("Maximum nodes to include (default: 40).")] int maxNodes = 40,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        [Description("Optional extraction backend: 'auto' (default), 'roslyn', or 'il'.")] string? backend = null,
        CancellationToken cancellationToken = default)
    {
        var nodeScope = ParseScope(scope);
        var opts = new ExtractionOptions(IncludeMethods: nodeScope == NodeKind.Method, TargetFramework: targetFramework, Backend: backend);
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
        return GraphVisualizer.Export(snapshot.Graph, format, nodeScope, filter, Math.Clamp(maxNodes, 1, 150));
    }

    [McpServerTool(Name = "di_analysis")]
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

    [McpServerTool(Name = "analyzers")]
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

    [McpServerTool(Name = "inspect_symbol")]
    [Description("Inspect a single type or method in detail: metrics (CC, cognitive complexity, LCOM4, coupling, custom metrics), location, outgoing dependencies, and incoming dependents.")]
    public async Task<string> InspectSymbolAsync(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Full type/method name (e.g. 'ArchLens.Analysis.CycleDetector') or short class name.")] string symbol,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Include method-level call graph nodes (default: false).")] bool includeMethods = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        [Description("Optional extraction backend: 'auto' (default), 'roslyn', or 'il'.")] string? backend = null,
        CancellationToken cancellationToken = default)
    {
        var opts = new ExtractionOptions(IncludeMethods: includeMethods, TargetFramework: targetFramework, Backend: backend);
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
        using var plugins = LoadRepoPlugins(snapshot.Graph.RootPath, out _);
        var report = analyzer.InspectSymbol(snapshot.Graph, symbol, plugins.MetricCalculators);
        if (report is null)
        {
            return Serialize(new { error = $"Symbol '{symbol}' was not found in '{snapshot.TargetPath}'." });
        }

        return Serialize(report);
    }

    [McpServerTool(Name = "impact")]
    [Description("Analyze the transitive blast radius of modifying a type or method: direct and transitive dependents and affected projects.")]
    public async Task<string> AnalyzeImpactAsync(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Full type/method name or short class name.")] string symbol,
        [Description("Maximum traversal depth (default: 4).")] int maxDepth = 4,
        [Description("Maximum impacted items to list (default: 40).")] int maxItems = 40,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Include method-level call graph nodes (default: false).")] bool includeMethods = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        [Description("Optional extraction backend: 'auto' (default), 'roslyn', or 'il'.")] string? backend = null,
        CancellationToken cancellationToken = default)
    {
        var opts = new ExtractionOptions(IncludeMethods: includeMethods, TargetFramework: targetFramework, Backend: backend);
        var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
        var result = analyzer.AnalyzeImpact(snapshot.Graph, symbol, Math.Clamp(maxDepth, 1, 10), Math.Clamp(maxItems, 1, 200));
        if (result is null)
        {
            return Serialize(new { error = $"Symbol '{symbol}' was not found in '{snapshot.TargetPath}'." });
        }

        return Serialize(result);
    }

    [McpServerTool(Name = "diff")]
    [Description("Incrementally reload changed files from disk and compare architectural health against the session baseline or a saved .archlens-baseline.json file. Can also persist a new baseline to disk.")]
    public async Task<string> DiffAgainstBaselineAsync(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Optional path to .archlens.yaml rule file.")] string? rulesPath = null,
        [Description("Optional built-in architectural preset: 'clean-architecture', 'vertical-slice', 'modular-monolith', or 'ddd'.")] string? preset = null,
        [Description("Optional root namespace prefix for presets (e.g. 'MyApp').")] string? rootNamespace = null,
        [Description("Set true to reset the baseline to the newly loaded snapshot instead of comparing.")] bool resetBaseline = false,
        [Description("Optional path to a saved .archlens-baseline.json file (auto-discovered if no in-memory baseline exists).")] string? baselineFile = null,
        [Description("Set true to persist the current snapshot as .archlens-baseline.json on disk.")] bool saveBaseline = false,
        [Description("Include test projects in the graph (default: false).")] bool includeTests = false,
        [Description("Include method-level call graph nodes (auto-enabled if rules require method graph).")] bool includeMethods = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        [Description("Optional extraction backend: 'auto' (default), 'roslyn', or 'il'.")] string? backend = null,
        CancellationToken cancellationToken = default)
    {
        var (baseRules, source) = LoadRulesOrDefaults(path, rulesPath, preset, rootNamespace);
        var needsMethods = includeMethods || RulesRequireMethodGraph(baseRules);
        var opts = new ExtractionOptions(IncludeTestProjects: includeTests, IncludeMethods: needsMethods, TargetFramework: targetFramework, Backend: backend);

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

        using var plugins = LoadRepoPlugins(current.Graph.RootPath, out var pluginNote);
        var allRules = plugins.Rules.Count > 0 ? [.. baseRules, .. plugins.Rules] : baseRules;
        var allDiags = MergePluginDiagnostics(plugins.Diagnostics, pluginNote);
        var diff = analyzer.DiffSnapshots(
            baseline,
            current,
            allRules,
            plugins.MetricCalculators,
            plugins.EntryPointDetectors.Count > 0 ? plugins.EntryPointDetectors : null);
        return Serialize(new
        {
            rulesSource = source,
            baselineSource,
            pluginDiagnostics = allDiags.Count > 0 ? allDiags : null,
            diff
        });
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

    private (IReadOnlyList<IArchitectureRule> Rules, string Source) LoadRulesOrDefaults(
        string targetPath,
        string? explicitRulesPath,
        string? preset = null,
        string? rootNamespace = null)
    {
        var rules = new List<IArchitectureRule>();
        var sources = new List<string>();

        if (!string.IsNullOrWhiteSpace(preset))
        {
            rules.AddRange(ArchitecturePresets.CreateRules(preset, rootNamespace));
            sources.Add($"preset:{preset}");
        }

        if (!string.IsNullOrWhiteSpace(explicitRulesPath))
        {
            var fullExplicit = Path.GetFullPath(explicitRulesPath);
            if (!File.Exists(fullExplicit))
            {
                throw new FileNotFoundException($"Rules file not found: '{fullExplicit}'.", fullExplicit);
            }

            var provider = ruleProviders.FirstOrDefault(p => p.CanLoad(fullExplicit))
                ?? throw new NotSupportedException($"No rule provider can load '{fullExplicit}'. Expected a .yaml/.yml file.");
            rules.AddRange(provider.LoadRules(fullExplicit));
            sources.Add(fullExplicit);
        }
        else if (rules.Count == 0)
        {
            var discoveredPath = YamlRuleSetProvider.DiscoverConfigFile(targetPath);
            if (discoveredPath is not null)
            {
                var provider = ruleProviders.FirstOrDefault(p => p.CanLoad(discoveredPath));
                if (provider is not null)
                {
                    rules.AddRange(provider.LoadRules(discoveredPath));
                    sources.Add(discoveredPath);
                }
            }
        }

        if (rules.Count > 0)
        {
            return (rules, string.Join(" + ", sources));
        }

        IReadOnlyList<IArchitectureRule> defaults =
        [
            new NoCyclesRule("BUILTIN-001", "No project cycles", "Projects must form a DAG.", RuleSeverity.Error, NodeKind.Project),
            new NoCyclesRule("BUILTIN-002", "No namespace cycles", "Namespaces must form a DAG.", RuleSeverity.Warning, NodeKind.Namespace)
        ];

        return (defaults, "built-in defaults (no .archlens.yaml found)");
    }

    private LoadedPlugins LoadRepoPlugins(string rootPath, out string? disabledNotice)
    {
        disabledNotice = null;
        if (!string.IsNullOrWhiteSpace(_pluginOptions.ExplicitPluginsDirectory))
        {
            return PluginLoader.LoadFromDirectory(_pluginOptions.ExplicitPluginsDirectory);
        }

        var pluginsDir = PluginLoader.DiscoverPluginsDirectory(rootPath);
        if (pluginsDir is null)
        {
            return LoadedPlugins.Empty;
        }

        if (!_pluginOptions.AllowRepoPlugins)
        {
            disabledNotice = $"Plugins detected in '{pluginsDir}' but not loaded (opt-in required). Start archlens-mcp with --allow-plugins or ARCHLENS_ALLOW_PLUGINS=1 to execute repository plugins.";
            return LoadedPlugins.Empty;
        }

        return PluginLoader.LoadFromDirectory(pluginsDir);
    }

    private static List<string> MergePluginDiagnostics(IReadOnlyList<string> diagnostics, string? notice)
    {
        var list = new List<string>(diagnostics.Count + (notice is null ? 0 : 1));
        if (notice is not null) list.Add(notice);
        list.AddRange(diagnostics);
        return list;
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
