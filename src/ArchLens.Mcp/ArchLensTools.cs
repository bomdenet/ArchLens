using System.ComponentModel;
using ArchLens.Analysis;
using ArchLens.Extraction.Roslyn;
using ArchLens.Model;
using ArchLens.Query;
using ArchLens.Rules.Yaml;
using ModelContextProtocol.Protocol;
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

    [McpServerTool(Name = "overview")]
    [Description("Load (or read cached with auto-invalidation and persistent SQLite cache) C# solution/project/assemblies and return a compact architectural overview with load health status. Reports actionable namespaceCycleCount (matching check_rules defaults) plus namespaceCycleCountRaw and suppressedNamespaceCycles when noise cycles (EF migrations, parent-child namespaces) are suppressed.")]
    public Task<CallToolResult> GetOverviewAsync(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Set true to force a full reload from disk (disk changes are normally auto-detected).")] bool refresh = false,
        [Description("Include test projects in the graph (default: false).")] bool includeTests = false,
        [Description("Include method-level call graph nodes (default: false).")] bool includeMethods = false,
        [Description("Return only summary counts and loadStatus without project/namespace arrays (default: false).")] bool summaryOnly = false,
        [Description("Include NuGet packageReferences lists on each project (default: false).")] bool includePackages = false,
        [Description("Return full raw MSBuild diagnostic messages instead of 1-line summaries (default: false).")] bool includeFullDiagnostics = false,
        [Description("Maximum top namespaces to return (default: 25).")] int topNamespacesLimit = 25,
        [Description("Optional target framework moniker for multi-targeted projects (e.g. 'net8.0', 'net10.0').")] string? targetFramework = null,
        [Description("Optional extraction backend: 'auto' (default), 'roslyn' (full source semantics), or 'il' (fast compiled .dll+.pdb reader).")] string? backend = null,
        CancellationToken cancellationToken = default) =>
        ExecuteSafeAsync(path, async () =>
        {
            var opts = new ExtractionOptions(IncludeTestProjects: includeTests, IncludeMethods: includeMethods, TargetFramework: targetFramework, Backend: backend);
            var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
            var report = analyzer.GetOverview(snapshot, Math.Clamp(topNamespacesLimit, 1, 200));
            return McpResponseFormatter.Ok(McpResponseFormatter.FormatOverview(report, summaryOnly, includePackages, includeFullDiagnostics));
        }, cancellationToken);

    [McpServerTool(Name = "dependency_graph")]
    [Description("Return the dependency graph and detected cycles at project, namespace, type, or method scope. Defaults to compact node-index edges [[fromIdx,toIdx]] strictly scoped to returned nodes.")]
    public Task<CallToolResult> GetDependencyGraphAsync(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Granularity: 'project', 'namespace' (default), 'type', or 'method'.")] string scope = "namespace",
        [Description("Optional glob filter (e.g. 'MyApp.Domain.*').")] string? filter = null,
        [Description("Maximum number of nodes to return (default: 50).")] int maxNodes = 50,
        [Description("Maximum number of edges between returned nodes to return (default: auto ~3x maxNodes, max: 500).")] int? maxEdges = null,
        [Description("Include detected cycles (default: true).")] bool includeCycles = true,
        [Description("Maximum number of cycles to return (default: 20).")] int maxCycles = 20,
        [Description("Include suppressed noise namespace cycles (EF migrations, parent-child) in cycles list (default: false — matches overview/check_rules).")] bool includeSuppressedCycles = false,
        [Description("Output edge format: 'compact' (default, edges as [fromIdx, toIdx] pairs), 'full' ({from, to} objects), or 'names' (nodes only, no edges).")] string format = "compact",
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        [Description("Optional extraction backend: 'auto' (default), 'roslyn', or 'il'.")] string? backend = null,
        CancellationToken cancellationToken = default) =>
        ExecuteSafeAsync(path, async () =>
        {
            var nodeScope = ParseScope(scope);
            var normalizedFormat = ValidateDependencyGraphFormat(format);
            var opts = new ExtractionOptions(IncludeMethods: nodeScope == NodeKind.Method, TargetFramework: targetFramework, Backend: backend);
            var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
            var clampedNodes = Math.Clamp(maxNodes, 1, 200);
            var effectiveMaxEdges = maxEdges.HasValue
                ? Math.Clamp(maxEdges.Value, 0, 500)
                : Math.Clamp(clampedNodes * 3, 20, 300);
            var report = analyzer.GetDependencyGraph(
                snapshot.Graph, nodeScope, filter, clampedNodes, effectiveMaxEdges, includeCycles, Math.Clamp(maxCycles, 0, 100), includeSuppressedCycles);
            return McpResponseFormatter.Ok(McpResponseFormatter.FormatDependencyGraph(report, normalizedFormat, includeCycles, snapshot.Graph.LoadStatus));
        }, cancellationToken);

    [McpServerTool(Name = "hotspots")]
    [Description("Find top architectural hotspots (cyclomatic/cognitive complexity, LCOM4 cohesion, coupling, god classes, unstable namespaces, bloated constructors, or custom plugin metrics). Returns compact metric projections by default. Note: 'complexity' and 'cognitive' rank by peak method complexity; use 'total_complexity' or 'total_cognitive' to rank by type total.")]
    public Task<CallToolResult> GetHotspotsAsync(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Metric to rank by: 'complexity' (default, peak method CC), 'total_complexity', 'god_class', 'cognitive' (peak method cognitive), 'total_cognitive', 'lcom4', 'coupling', 'afferent', 'instability', 'distance' (namespace scope only), 'lines', 'ctor', or any custom plugin metric name.")] string sortBy = "complexity",
        [Description("Granularity: 'type' (default), 'namespace', or 'method'.")] string scope = "type",
        [Description("Optional glob filter (e.g. 'MyApp.*').")] string? filter = null,
        [Description("Number of top hotspots to return (default: 15).")] int topN = 15,
        [Description("Zero-based pagination offset (default: 0).")] int offset = 0,
        [Description("Optional comma-separated fields to return (e.g. 'cognitiveComplexity,maxMethodCognitiveComplexity,lineCount,location' or 'all'). Omit for compact sort-tailored fields.")] string? fields = null,
        [Description("Return only summary counts without item list (default: false).")] bool summaryOnly = false,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        [Description("Optional extraction backend: 'auto' (default), 'roslyn', or 'il'.")] string? backend = null,
        CancellationToken cancellationToken = default) =>
        ExecuteSafeAsync(path, async () =>
        {
            var nodeScope = ParseHotspotsScope(scope);
            McpResponseFormatter.ValidateHotspotFields(fields);
            var opts = new ExtractionOptions(IncludeMethods: nodeScope == NodeKind.Method, TargetFramework: targetFramework, Backend: backend);
            var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
            using var plugins = LoadRepoPlugins(snapshot.Graph.RootPath, out _);
            var clampedTopN = Math.Clamp(topN, 1, 100);
            var report = analyzer.GetHotspots(snapshot.Graph, sortBy, filter, nodeScope, int.MaxValue, plugins.MetricCalculators);
            return McpResponseFormatter.Ok(McpResponseFormatter.FormatHotspots(snapshot.Graph, report, offset, clampedTopN, fields, summaryOnly));
        }, cancellationToken);

    [McpServerTool(Name = "check_rules")]
    [Description("Evaluate architecture rules from .archlens.yaml, built-in architectural presets, and enabled plugins, optionally aggregating Roslyn analyzer diagnostics.")]
    public Task<CallToolResult> CheckRulesAsync(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Optional path to a .archlens.yaml rule file. Auto-discovered if omitted.")] string? rulesPath = null,
        [Description("Optional built-in architectural preset: 'clean-architecture' (aliases: clean, onion), 'vertical-slice' (vsa, slices), 'modular-monolith' (modular, modules), or 'ddd' (domain-driven-design).")] string? preset = null,
        [Description("Optional root namespace prefix for presets (e.g. 'MyApp').")] string? rootNamespace = null,
        [Description("Maximum violations to return (default: 50).")] int maxViolations = 50,
        [Description("Zero-based pagination offset (default: 0).")] int offset = 0,
        [Description("Return only violation counts without violation items (default: false).")] bool summaryOnly = false,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Include test projects in the graph (default: false).")] bool includeTests = false,
        [Description("Include method-level call graph nodes (auto-enabled if rules require method graph).")] bool includeMethods = false,
        [Description("Include Roslyn analyzer diagnostics (Design, Maintainability, Performance) in the report (default: false).")] bool includeAnalyzers = false,
        [Description("Optional comma-separated Roslyn analyzer categories (default: 'Design,Maintainability,Performance'; use 'all' for everything).")] string? analyzerCategories = null,
        [Description("Include violations from degraded projects that had MSBuild load failures (default: false).")] bool includeDegraded = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        [Description("Optional extraction backend: 'auto' (default), 'roslyn', or 'il'.")] string? backend = null,
        CancellationToken cancellationToken = default) =>
        ExecuteSafeAsync(path, async () =>
        {
            var (baseRules, source, yamlRootNamespace) = LoadRulesOrDefaults(path, rulesPath, preset, rootNamespace);
            var effectiveRootNamespace = !string.IsNullOrWhiteSpace(rootNamespace) ? rootNamespace : yamlRootNamespace;
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
            var clampedMax = Math.Clamp(maxViolations, 1, 200);
            var report = analyzer.CheckRules(
                snapshot.Graph,
                allRules,
                int.MaxValue,
                includeAnalyzers,
                plugins.MetricCalculators,
                plugins.EntryPointDetectors.Count > 0 ? plugins.EntryPointDetectors : null,
                excludeDegradedProjects: !includeDegraded,
                rootNamespace: effectiveRootNamespace);
            return McpResponseFormatter.Ok(McpResponseFormatter.FormatCheckRules(
                source, plugins.LoadedPluginFiles, allDiags, report, snapshot.Graph.LoadStatus, offset, clampedMax, summaryOnly,
                snapshot.SnapshotId, snapshot.CapturedAt, report.Note));
        }, cancellationToken);

    [McpServerTool(Name = "dead_code")]
    [Description("Detect potentially dead / unreferenced types or methods using reachability analysis from built-in (including Blazor/Razor, WPF/XAML/UI, ASP.NET, EF Core, DI, messaging, tests) and enabled plugin entry-point detectors. Defaults to scope='type' (recommended highest-signal starting point) and profile='auto' ('app' for solutions with executables, 'lib' for pure libraries). scope='method' skips implicit/parameterless/DTO-record serializer constructors on reachable types but is inherently noisier than type scope due to reflection and test-only callers.")]
    public Task<CallToolResult> AnalyzeDeadCodeAsync(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Granularity: 'type' (default, recommended first — highest signal) or 'method' (finer-grained; skips default/DTO serializer constructors on reachable types, may still report reflection-invoked or test-only methods).")] string scope = "type",
        [Description("Entry-point profile: 'auto' (default — 'app' when solution has an executable, 'lib' for pure libraries), 'app' (public library types are NOT automatic entry points), 'lib' (public library types ARE entry points), or 'strict' (neither public library APIs nor DI registrations are automatic entry points).")] string? profile = null,
        [Description("Explicit override for treating public symbols in library (DLL) projects as entry points. Omit to let 'profile' decide.")] bool? treatPublicApiAsEntryPoint = null,
        [Description("Optional glob filter (e.g. 'MyApp.*').")] string? filter = null,
        [Description("Maximum candidates to return (default: 50).")] int maxItems = 50,
        [Description("Zero-based pagination offset (default: 0).")] int offset = 0,
        [Description("Return only summary counts without candidate list (default: false).")] bool summaryOnly = false,
        [Description("Include test projects in the graph (default: false). Set true to count references from test projects.")] bool includeTests = false,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        [Description("Optional extraction backend: 'auto' (default), 'roslyn', or 'il'.")] string? backend = null,
        CancellationToken cancellationToken = default) =>
        ExecuteSafeAsync(path, async () =>
        {
            var nodeScope = ParseDeadCodeScope(scope);
            var opts = new ExtractionOptions(
                IncludeTestProjects: includeTests,
                IncludeMethods: nodeScope == NodeKind.Method,
                TargetFramework: targetFramework,
                Backend: backend);
            var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
            using var plugins = LoadRepoPlugins(snapshot.Graph.RootPath, out _);
            var clampedMax = Math.Clamp(maxItems, 1, 200);
            var effectiveProfile = !string.IsNullOrWhiteSpace(profile)
                ? profile
                : treatPublicApiAsEntryPoint.HasValue
                    ? null
                    : "auto";
            IReadOnlyList<DeadCodeAcknowledgement>? acknowledged = null;
            var discoveredConfig = YamlRuleSetProvider.DiscoverConfigFile(path)
                ?? YamlRuleSetProvider.DiscoverConfigFile(snapshot.Graph.RootPath);
            if (discoveredConfig is not null && ruleProviders.OfType<YamlRuleSetProvider>().FirstOrDefault() is { } yamlProvider)
            {
                var loaded = yamlProvider.LoadDeadCodeAcknowledgements(discoveredConfig);
                if (loaded.Count > 0) acknowledged = loaded;
            }
            var report = analyzer.AnalyzeDeadCode(
                snapshot.Graph,
                nodeScope,
                treatPublicApiAsEntryPoint ?? true,
                filter,
                int.MaxValue,
                plugins.EntryPointDetectors.Count > 0 ? plugins.EntryPointDetectors : null,
                testsExcluded: !includeTests,
                profile: effectiveProfile,
                acknowledged: acknowledged);
            return McpResponseFormatter.Ok(McpResponseFormatter.FormatDeadCode(report, snapshot.Graph.LoadStatus, offset, clampedMax, summaryOnly));
        }, cancellationToken);

    [McpServerTool(Name = "query")]
    [Description("Run a read-only SQL query against the codebase's in-memory SQLite projection (tables: nodes, edges, metrics, projects, di_registrations, analyzer_diagnostics, custom_metrics, schema_info). Pass includeSchema: true (or query SELECT * FROM schema_info) to inspect columns without PRAGMA.")]
    public Task<CallToolResult> QueryGraphSqlAsync(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory (optional if includeSchema is true and sql is omitted).")] string? path = null,
        [Description("Read-only SQL SELECT / WITH query (e.g. 'SELECT name, lcom4, efferent_coupling FROM metrics WHERE kind = ''Type'' ORDER BY lcom4 DESC LIMIT 10'). Omit with includeSchema: true to return only the table/column schema.")] string? sql = null,
        [Description("Return the compact table/column schema map (and skip loading the solution when sql is omitted). Default: false.")] bool includeSchema = false,
        [Description("Maximum rows to return (default: 100, max: 500).")] int maxRows = 100,
        [Description("Include method-level nodes and call graph edges (default: false).")] bool includeMethods = false,
        [Description("Include test projects (default: false).")] bool includeTests = false,
        [Description("Include Roslyn analyzer diagnostics table (default: false).")] bool includeAnalyzers = false,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        [Description("Optional extraction backend: 'auto' (default), 'roslyn', or 'il'.")] string? backend = null,
        CancellationToken cancellationToken = default) =>
        ExecuteSafeAsync(path, async () =>
        {
            if (includeSchema && string.IsNullOrWhiteSpace(sql))
            {
                return McpResponseFormatter.Ok(new
                {
                    tables = GraphSqlQueryEngine.GetCompactSchema(),
                    note = "PRAGMA is disabled in read-only mode. Query 'SELECT * FROM schema_info' or use the tables/columns listed above."
                });
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("Parameter 'path' is required when executing a SQL query.", nameof(path));
            }

            if (string.IsNullOrWhiteSpace(sql))
            {
                throw new ArgumentException("Parameter 'sql' is required (or pass includeSchema: true to inspect the schema).", nameof(sql));
            }

            GraphSqlQueryEngine.ValidateReadOnlySql(sql);
            var opts = new ExtractionOptions(
                IncludeTestProjects: includeTests,
                IncludeMethods: includeMethods,
                TargetFramework: targetFramework,
                IncludeAnalyzerDiagnostics: includeAnalyzers,
                AnalyzerCategories: includeAnalyzers ? ["all"] : null,
                Backend: backend);
            var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
            using var plugins = LoadRepoPlugins(snapshot.Graph.RootPath, out _);
            var result = sqlEngine.Execute(snapshot.Graph, sql, maxRows, plugins.MetricCalculators, cancellationToken: cancellationToken);
            if (includeSchema)
            {
                return McpResponseFormatter.Ok(new
                {
                    result.Columns,
                    result.Rows,
                    result.RowCount,
                    result.Truncated,
                    schema = GraphSqlQueryEngine.GetCompactSchema()
                });
            }
            return McpResponseFormatter.Ok(result);
        }, cancellationToken);

    [McpServerTool(Name = "export_diagram")]
    [Description("Export the dependency graph in human-readable diagram formats: 'mermaid', 'dot' (Graphviz), or 'structurizr' DSL, highlighting dependency cycles. Returns raw diagram text (not JSON).")]
    public Task<CallToolResult> ExportDiagramAsync(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Output format: 'mermaid' (default), 'dot', or 'structurizr'.")] string format = "mermaid",
        [Description("Granularity: 'project', 'namespace' (default), 'type', or 'method'.")] string scope = "namespace",
        [Description("Optional glob filter (e.g. 'MyApp.*').")] string? filter = null,
        [Description("Maximum nodes to include (default: 40).")] int maxNodes = 40,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        [Description("Optional extraction backend: 'auto' (default), 'roslyn', or 'il'.")] string? backend = null,
        CancellationToken cancellationToken = default) =>
        ExecuteSafeAsync(path, async () =>
        {
            var nodeScope = ParseScope(scope);
            var opts = new ExtractionOptions(IncludeMethods: nodeScope == NodeKind.Method, TargetFramework: targetFramework, Backend: backend);
            var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
            return McpResponseFormatter.OkRaw(GraphVisualizer.Export(snapshot.Graph, format, nodeScope, filter, Math.Clamp(maxNodes, 1, 150)));
        }, cancellationToken);

    [McpServerTool(Name = "di_analysis")]
    [Description("Analyze IServiceCollection dependency injection registrations, detect captive dependencies (Scoped captured by Singleton directly or via Transient), and list single-implementation interfaces. Returns compact lifetime/project counts by default (includeRegistrations defaults to false); supports lifetime/project/serviceType/onlyCaptive/onlyUnregistered filters.")]
    public Task<CallToolResult> AnalyzeDiAsync(
        [Description("Path to .slnx, .sln, .csproj, or repository directory.")] string path,
        [Description("Include the raw registrations array (default: false — returns compact lifetimeCounts, topProjects, captiveDependencies, and singleImplementationInterfaces).")] bool includeRegistrations = false,
        [Description("Return only summary counts without arrays (default: false).")] bool summaryOnly = false,
        [Description("Optional lifetime filter for registrations: 'singleton', 'scoped', or 'transient'.")] string? lifetime = null,
        [Description("Optional project name or glob filter for registrations.")] string? project = null,
        [Description("Optional service/implementation type substring or glob filter.")] string? serviceType = null,
        [Description("Return only captive dependencies and summary counts (default: false).")] bool onlyCaptive = false,
        [Description("Filter singleImplementationInterfaces to those NOT registered in DI (default: false).")] bool onlyUnregistered = false,
        [Description("Zero-based pagination offset for singleImplementationInterfaces / registrations (default: 0).")] int offset = 0,
        [Description("Maximum items to return per paginated list (default: 50, max: 200).")] int limit = 50,
        [Description("Include test projects in the graph (default: false).")] bool includeTests = false,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        CancellationToken cancellationToken = default) =>
        ExecuteSafeAsync(path, async () =>
        {
            if (!string.IsNullOrWhiteSpace(lifetime) &&
                !Enum.TryParse<ServiceLifetimeKind>(lifetime.Trim(), ignoreCase: true, out _))
            {
                throw new ArgumentException($"Unknown DI lifetime '{lifetime}'. Supported values: 'singleton', 'scoped', 'transient'.", nameof(lifetime));
            }

            var opts = new ExtractionOptions(IncludeTestProjects: includeTests, TargetFramework: targetFramework);
            var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
            var report = analyzer.AnalyzeDi(snapshot.Graph);
            return McpResponseFormatter.Ok(McpResponseFormatter.FormatDiAnalysis(
                report, snapshot.Graph.LoadStatus, includeRegistrations, summaryOnly, offset, limit,
                lifetime, project, serviceType, onlyCaptive, onlyUnregistered));
        }, cancellationToken);

    [McpServerTool(Name = "analyzers")]
    [Description("Run Roslyn DiagnosticAnalyzers (Microsoft.CodeAnalysis.NetAnalyzers, Roslynator, etc.) on the solution and aggregate architectural diagnostics filtered by category and severity. Defaults to minSeverity='warning' and skips generated files and EF Core migrations.")]
    public Task<CallToolResult> GetAnalyzerDiagnosticsAsync(
        [Description("Path to .slnx, .sln, .csproj, or repository directory.")] string path,
        [Description("Comma-separated categories (default: 'Design,Maintainability,Performance'; use 'all' for everything).")] string? categories = null,
        [Description("Minimum severity to return: 'warning' (default), 'info', or 'error'.")] string? minSeverity = "warning",
        [Description("Elevate Info/suppressed analyzer rules in the requested categories to Warning severity (default: false).")] bool elevateToWarning = false,
        [Description("Optional glob filter on rule ID, file path, or project name.")] string? filter = null,
        [Description("Maximum diagnostics to return (default: 60).")] int maxItems = 60,
        [Description("Zero-based pagination offset (default: 0).")] int offset = 0,
        [Description("Return only counts and categories without diagnostics list (default: false).")] bool summaryOnly = false,
        [Description("Include diagnostics from degraded projects that had MSBuild load failures (default: false).")] bool includeDegraded = false,
        [Description("Include test projects in the graph (default: false).")] bool includeTests = false,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        CancellationToken cancellationToken = default) =>
        ExecuteSafeAsync(path, async () =>
        {
            var catList = ParseCategories(categories);
            var parsedMinSeverity = ParseSeverityFilter(minSeverity);
            var opts = new ExtractionOptions(
                IncludeTestProjects: includeTests,
                TargetFramework: targetFramework,
                IncludeAnalyzerDiagnostics: true,
                AnalyzerCategories: catList,
                ElevateAnalyzerSeverity: elevateToWarning);
            var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
            var clampedMax = Math.Clamp(maxItems, 1, 200);
            var report = analyzer.GetAnalyzerDiagnostics(
                snapshot.Graph, catList, int.MaxValue, parsedMinSeverity, filter, excludeDegradedProjects: !includeDegraded);
            return McpResponseFormatter.Ok(McpResponseFormatter.FormatAnalyzerDiagnostics(
                report, snapshot.Graph.LoadStatus, offset, clampedMax, summaryOnly,
                snapshot.SnapshotId, snapshot.CapturedAt));
        }, cancellationToken);

    [McpServerTool(Name = "inspect_symbol")]
    [Description("Inspect a single type or method in detail: deduplicated node info, metrics (CC, cognitive complexity, LCOM4, coupling, custom metrics), location, outgoing dependencies, and incoming dependents.")]
    public Task<CallToolResult> InspectSymbolAsync(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Full type/method name (e.g. 'ArchLens.Analysis.CycleDetector') or short class name.")] string symbol,
        [Description("Optional comma-separated top-level fields to return (e.g. 'metrics,outgoingDependencies,incomingDependents' or 'all').")] string? fields = null,
        [Description("Maximum outgoing/incoming dependencies to return per direction (default: 50).")] int maxEdges = 50,
        [Description("Zero-based pagination offset for dependencies (default: 0).")] int offset = 0,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Include method-level call graph nodes (default: false).")] bool includeMethods = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        [Description("Optional extraction backend: 'auto' (default), 'roslyn', or 'il'.")] string? backend = null,
        CancellationToken cancellationToken = default) =>
        ExecuteSafeAsync(path, async () =>
        {
            McpResponseFormatter.ValidateInspectSymbolFields(fields);
            var opts = new ExtractionOptions(IncludeMethods: includeMethods, TargetFramework: targetFramework, Backend: backend);
            var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
            var (resolvedNode, ambiguous) = ArchitectureAnalyzerService.ResolveNodeWithAmbiguity(snapshot.Graph, symbol);
            if (ambiguous.Count > 1)
            {
                return McpResponseFormatter.ErrorWithCandidates(
                    "ambiguous_symbol",
                    $"Symbol '{symbol}' matched {ambiguous.Count} symbols in '{snapshot.TargetPath}'.",
                    ambiguous.Take(15).Select(c => c.FullName).ToList(),
                    path,
                    "Pass one of the fully-qualified candidate symbol names.");
            }
            if (resolvedNode is null)
            {
                var baseHint = includeMethods
                    ? "Verify the exact symbol name or use 'query' / 'hotspots' to search available symbols."
                    : "Verify the symbol name, or pass includeMethods: true if inspecting a method.";
                if (snapshot.Graph.LoadStatus is { Degraded: true } ls)
                {
                    baseHint += $" Note: solution loaded in degraded mode ({ls.ProjectsLoaded}/{ls.ProjectsExpected} projects loaded); symbol lookup may be incomplete.";
                }
                return McpResponseFormatter.Error(
                    "symbol_not_found",
                    $"Symbol '{symbol}' was not found in '{snapshot.TargetPath}'.",
                    path,
                    baseHint);
            }

            using var plugins = LoadRepoPlugins(snapshot.Graph.RootPath, out _);
            var report = analyzer.InspectSymbol(snapshot.Graph, resolvedNode.Id, plugins.MetricCalculators)!;
            return McpResponseFormatter.Ok(McpResponseFormatter.FormatSymbolInspection(report, snapshot.Graph.LoadStatus, fields, maxEdges, offset));
        }, cancellationToken);

    [McpServerTool(Name = "impact")]
    [Description("Analyze the transitive blast radius of modifying a type or method: direct and transitive consumers and affected projects. Auto-summarizes when totalTransitiveDependentsCount > 20 unless summaryOnly: false or maxItems is specified.")]
    public Task<CallToolResult> AnalyzeImpactAsync(
        [Description("Path to .slnx, .sln, .csproj, .dll, or repository/binary directory.")] string path,
        [Description("Full type/method name or short class name.")] string symbol,
        [Description("Maximum traversal depth (default: 4).")] int maxDepth = 4,
        [Description("Maximum impacted items to list (default: 40; setting an explicit value disables auto-summary).")] int? maxItems = null,
        [Description("Zero-based pagination offset (default: 0).")] int offset = 0,
        [Description("Return only summary counts and affectedProjects without impactedNodes. Omit to auto-summarize when fan-in > 20; pass false for the full list.")] bool? summaryOnly = null,
        [Description("Exclude DI registration / composition-root edges from dependents (default: true). Set false to include them categorized as 'registration'.")] bool excludeRegistrations = true,
        [Description("Include test and test-support projects in dependents (default: false).")] bool includeTests = false,
        [Description("Follow only strong structural edges (MethodCall, FieldOrProperty, Inherits, Implements, ObjectCreation) (default: false).")] bool strict = false,
        [Description("Optional comma-separated fields for impactedNodes (e.g. 'project,distance,category,location').")] string? fields = null,
        [Description("Set true to force a full reload from disk.")] bool refresh = false,
        [Description("Include method-level call graph nodes (default: false).")] bool includeMethods = false,
        [Description("Optional target framework moniker for multi-targeted projects.")] string? targetFramework = null,
        [Description("Optional extraction backend: 'auto' (default), 'roslyn', or 'il'.")] string? backend = null,
        CancellationToken cancellationToken = default) =>
        ExecuteSafeAsync(path, async () =>
        {
            McpResponseFormatter.ValidateImpactFields(fields);
            var opts = new ExtractionOptions(IncludeTestProjects: includeTests, IncludeMethods: includeMethods, TargetFramework: targetFramework, Backend: backend);
            var snapshot = await sessions.GetOrLoadAsync(path, refresh, opts, cancellationToken);
            var (resolvedNode, ambiguous) = ArchitectureAnalyzerService.ResolveNodeWithAmbiguity(snapshot.Graph, symbol);
            if (ambiguous.Count > 1)
            {
                return McpResponseFormatter.ErrorWithCandidates(
                    "ambiguous_symbol",
                    $"Symbol '{symbol}' matched {ambiguous.Count} symbols in '{snapshot.TargetPath}'.",
                    ambiguous.Take(15).Select(c => c.FullName).ToList(),
                    path,
                    "Pass one of the fully-qualified candidate symbol names.");
            }
            if (resolvedNode is null)
            {
                var baseHint = includeMethods
                    ? "Verify the exact symbol name."
                    : "Verify the symbol name, or pass includeMethods: true if analyzing a method.";
                if (snapshot.Graph.LoadStatus is { Degraded: true } ls)
                {
                    baseHint += $" Note: solution loaded in degraded mode ({ls.ProjectsLoaded}/{ls.ProjectsExpected} projects loaded); symbol lookup may be incomplete.";
                }
                return McpResponseFormatter.Error(
                    "symbol_not_found",
                    $"Symbol '{symbol}' was not found in '{snapshot.TargetPath}'.",
                    path,
                    baseHint);
            }

            var clampedMax = Math.Clamp(maxItems ?? 40, 1, 200);
            var result = analyzer.AnalyzeImpact(
                snapshot.Graph, resolvedNode.Id, Math.Clamp(maxDepth, 1, 10), int.MaxValue,
                excludeRegistrations: excludeRegistrations,
                includeTests: includeTests,
                strict: strict)!;

            var autoSummarized = summaryOnly is null && !maxItems.HasValue && offset == 0 && result.TotalTransitiveDependentsCount > 20;
            var effectiveSummaryOnly = summaryOnly ?? autoSummarized;
            return McpResponseFormatter.Ok(McpResponseFormatter.FormatImpact(result, snapshot.Graph.LoadStatus, offset, clampedMax, effectiveSummaryOnly, fields, autoSummarized));
        }, cancellationToken);

    [McpServerTool(Name = "diff")]
    [Description("Incrementally reload changed files from disk and compare architectural health against the session baseline or a saved .archlens-baseline.json file. Can also persist a new baseline to disk.")]
    public Task<CallToolResult> DiffAgainstBaselineAsync(
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
        CancellationToken cancellationToken = default) =>
        ExecuteSafeAsync(path, async () =>
        {
            var (baseRules, source, _) = LoadRulesOrDefaults(path, rulesPath, preset, rootNamespace);
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
                return McpResponseFormatter.Ok(new
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
                return McpResponseFormatter.Ok(new
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
                if (!File.Exists(resolved))
                {
                    return McpResponseFormatter.Error(
                        "path_not_found",
                        $"Baseline file not found: '{resolved}'.",
                        resolved,
                        "Omit baselineFile to use the session baseline, or pass saveBaseline: true to create a baseline file.");
                }
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
                return McpResponseFormatter.Ok(new
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
            var identicalSnapshot = string.Equals(baseline.SnapshotId, current.SnapshotId, StringComparison.Ordinal);
            return McpResponseFormatter.Ok(McpResponseFormatter.FormatDiff(
                source, baselineSource, identicalSnapshot, allDiags, diff));
        }, cancellationToken);

    private static async Task<CallToolResult> ExecuteSafeAsync(
        string? path,
        Func<Task<CallToolResult>> action,
        CancellationToken cancellationToken)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return McpResponseFormatter.FromException(ex, path);
        }
    }

    private static bool RulesRequireMethodGraph(IReadOnlyList<IArchitectureRule> rules) =>
        rules.OfType<NoCyclesRule>().Any(r => r.Scope == NodeKind.Method) ||
        rules.OfType<NoDeadCodeRule>().Any(r => r.Scope == NodeKind.Method) ||
        rules.OfType<MaxMetricRule>().Any(r => r.Scope == NodeKind.Method);

    private static IReadOnlyList<string>? ParseCategories(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static RuleSeverity? ParseSeverityFilter(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "error" or "err" => RuleSeverity.Error,
            "warning" or "warn" => RuleSeverity.Warning,
            "info" or "information" or "note" => RuleSeverity.Info,
            _ => throw new ArgumentException($"Unknown minSeverity '{raw}'. Supported values: 'info', 'warning', 'error'.", "minSeverity")
        };
    }

    private (IReadOnlyList<IArchitectureRule> Rules, string Source, string? YamlRootNamespace) LoadRulesOrDefaults(
        string targetPath,
        string? explicitRulesPath,
        string? preset = null,
        string? rootNamespace = null)
    {
        var rules = new List<IArchitectureRule>();
        var sources = new List<string>();
        string? yamlRootNamespace = null;

        if (!string.IsNullOrWhiteSpace(preset))
        {
            var seenPresets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawPreset in preset.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var canonicalPreset = ArchitecturePresets.TryResolvePresetId(rawPreset, out var resolvedPreset) ? resolvedPreset : rawPreset;
                if (seenPresets.Add(canonicalPreset))
                {
                    rules.AddRange(ArchitecturePresets.CreateRules(rawPreset, rootNamespace));
                    sources.Add($"preset:{canonicalPreset}");
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(explicitRulesPath))
        {
            var fullExplicit = Path.GetFullPath(explicitRulesPath);
            if (!File.Exists(fullExplicit))
            {
                throw new InvalidDataException($"Rules file not found: '{fullExplicit}'. Verify rulesPath points to an existing .archlens.yaml/.yml file, or omit rulesPath to auto-discover or use a preset.");
            }

            var provider = ruleProviders.FirstOrDefault(p => p.CanLoad(fullExplicit))
                ?? throw new NotSupportedException($"No rule provider can load '{fullExplicit}'. Expected a .yaml/.yml file.");
            rules.AddRange(provider.LoadRules(fullExplicit));
            sources.Add(fullExplicit);
            if (provider is YamlRuleSetProvider yamlProvider)
            {
                yamlRootNamespace = yamlProvider.LoadRootNamespace(fullExplicit);
            }
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
                    if (provider is YamlRuleSetProvider yamlProvider)
                    {
                        yamlRootNamespace = yamlProvider.LoadRootNamespace(discoveredPath);
                    }
                }
            }
        }

        if (rules.Count > 0)
        {
            return (rules, string.Join(" + ", sources), yamlRootNamespace);
        }

        IReadOnlyList<IArchitectureRule> defaults =
        [
            new NoCyclesRule("BUILTIN-001", "No project cycles", "Projects must form a DAG.", RuleSeverity.Error, NodeKind.Project),
            new NoCyclesRule("BUILTIN-002", "No namespace cycles", "Namespaces must form a DAG.", RuleSeverity.Warning, NodeKind.Namespace)
        ];

        return (defaults, "built-in defaults (no .archlens.yaml found)", null);
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

    private static NodeKind ParseScope(string scope) => scope.Trim().ToLowerInvariant() switch
    {
        "project" => NodeKind.Project,
        "namespace" => NodeKind.Namespace,
        "type" => NodeKind.Type,
        "method" => NodeKind.Method,
        _ => throw new ArgumentException($"Unknown scope '{scope}'. Supported values: 'project', 'namespace', 'type', 'method'.", nameof(scope))
    };

    private static NodeKind ParseHotspotsScope(string scope) => scope.Trim().ToLowerInvariant() switch
    {
        "type" => NodeKind.Type,
        "namespace" => NodeKind.Namespace,
        "method" => NodeKind.Method,
        _ => throw new ArgumentException($"Unknown hotspots scope '{scope}'. Supported values: 'type', 'namespace', 'method'.", nameof(scope))
    };

    private static NodeKind ParseDeadCodeScope(string scope) => scope.Trim().ToLowerInvariant() switch
    {
        "type" => NodeKind.Type,
        "method" => NodeKind.Method,
        _ => throw new ArgumentException($"Unknown dead_code scope '{scope}'. Supported values: 'type', 'method'.", nameof(scope))
    };

    private static string ValidateDependencyGraphFormat(string format) => format.Trim().ToLowerInvariant() switch
    {
        "compact" => "compact",
        "full" or "objects" => "full",
        "names" => "names",
        _ => throw new ArgumentException($"Unknown dependency_graph format '{format}'. Supported values: 'compact', 'full', 'names'.", nameof(format))
    };
}
