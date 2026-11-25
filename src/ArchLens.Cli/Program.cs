using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArchLens.Analysis;
using ArchLens.Extraction.Metadata;
using ArchLens.Extraction.Roslyn;
using ArchLens.Model;
using ArchLens.Query;
using ArchLens.Rules.Yaml;

MsBuildBootstrapper.EnsureRegistered();

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

var command = args[0].ToLowerInvariant();
var positional = new List<string>();
var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

var knownCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "presets", "init", "overview", "graph", "hotspots", "check",
    "dead-code", "di", "analyzers", "baseline", "diff", "query",
    "export", "report", "inspect", "impact"
};

var booleanFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "json", "sarif", "include-tests", "include-methods", "include-analyzers",
    "fail-on-captive", "fail-on-new-errors", "no-public-api-entry-points",
    "force", "help", "allow-plugins"
};

var valueFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "format", "output", "scope", "filter", "max-nodes", "by", "top",
    "rules", "preset", "root-namespace", "categories", "analyzer-categories",
    "tfm", "target-framework", "backend", "plugins", "baseline",
    "max-rows", "depth", "public-api-entry-points"
};

var parsingOptions = true;
for (var i = 1; i < args.Length; i++)
{
    var arg = args[i];
    if (parsingOptions && arg == "--")
    {
        parsingOptions = false;
        continue;
    }

    if (parsingOptions && (arg is "-h" or "--help"))
    {
        PrintHelp();
        return 0;
    }

    if (parsingOptions && arg.StartsWith("--", StringComparison.Ordinal))
    {
        var key = arg[2..];
        if (booleanFlags.Contains(key))
        {
            options[key] = "true";
            continue;
        }

        if (valueFlags.Contains(key))
        {
            if (i + 1 < args.Length)
            {
                options[key] = args[++i];
            }
            else
            {
                Console.Error.WriteLine($"Option '--{key}' requires a value.");
                return 2;
            }
            continue;
        }
    }

    // Any unrecognized argument (including SQL queries starting with '-- comment') is treated as positional
    positional.Add(arg);
}

if (!knownCommands.Contains(command))
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    PrintHelp();
    return 2;
}

// Validate required positional arguments BEFORE performing expensive extraction or loading plugins
if (command == "query" && positional.Count < 2)
{
    Console.Error.WriteLine("Usage: archlens query <path> \"<SQL>\" [--max-rows 100] [--include-methods] [--json]");
    return 2;
}
if (command == "inspect" && positional.Count < 2)
{
    Console.Error.WriteLine("Usage: archlens inspect <path> <symbol> [--include-methods] [--json]");
    return 2;
}
if (command == "impact" && positional.Count < 2)
{
    Console.Error.WriteLine("Usage: archlens impact <path> <symbol> [--depth 4] [--include-methods] [--json]");
    return 2;
}

var format = ResolveFormat(options);
var jsonOutput = format == "json";
var sarifOutput = format == "sarif";
var outputFile = options.GetValueOrDefault("output");
var includeTests = options.ContainsKey("include-tests");
var scopeOpt = options.GetValueOrDefault("scope");
var includeMethods = options.ContainsKey("include-methods") ||
                     string.Equals(scopeOpt, "method", StringComparison.OrdinalIgnoreCase);
var includeAnalyzers = options.ContainsKey("include-analyzers") || command == "analyzers";
var analyzerCategories = ParseCategories(options.GetValueOrDefault("categories") ?? options.GetValueOrDefault("analyzer-categories"));
var targetFramework = options.GetValueOrDefault("tfm") ?? options.GetValueOrDefault("target-framework");
var backend = options.GetValueOrDefault("backend");
var targetPath = positional.Count > 0 ? positional[0] : Directory.GetCurrentDirectory();

var allowPlugins = options.ContainsKey("allow-plugins") ||
                   options.ContainsKey("plugins") ||
                   string.Equals(Environment.GetEnvironmentVariable("ARCHLENS_ALLOW_PLUGINS"), "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(Environment.GetEnvironmentVariable("ARCHLENS_ALLOW_PLUGINS"), "true", StringComparison.OrdinalIgnoreCase);

using var roslynExtractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
var metadataExtractor = new MetadataCodeGraphExtractor();
var analyzer = new ArchitectureAnalyzerService();
var yamlProvider = new YamlRuleSetProvider();

IReadOnlyList<IArchitectureRule>? preloadedRules = null;
string? preloadedRulesSource = null;
string? resolvedBaselinePath = null;

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};

try
{
    // Commands that do not require extracting a solution first
    if (command == "presets")
    {
        if (jsonOutput)
        {
            WriteOutput(JsonSerializer.Serialize(ArchitecturePresets.All, jsonOpts), outputFile);
        }
        else
        {
            Console.WriteLine("Available Architecture Presets:");
            foreach (var p in ArchitecturePresets.All)
            {
                Console.WriteLine($"  - {p.Id} ({p.Name}) [aliases: {string.Join(", ", p.Aliases)}]");
                Console.WriteLine($"      {p.Description}");
            }
        }
        return 0;
    }

    if (command == "init")
    {
        var presetName = options.GetValueOrDefault("preset", "clean-architecture");
        var rootNs = options.GetValueOrDefault("root-namespace");
        var yamlText = ArchitecturePresets.GenerateYamlTemplate(presetName, rootNs);
        string outPath;
        if (!string.IsNullOrWhiteSpace(outputFile))
        {
            outPath = Path.GetFullPath(outputFile);
        }
        else
        {
            var fullTarget = Path.GetFullPath(targetPath);
            var targetDir = File.Exists(fullTarget)
                ? Path.GetDirectoryName(fullTarget)!
                : fullTarget;
            outPath = Path.Combine(targetDir, ".archlens.yaml");
        }

        if (File.Exists(outPath) && !options.ContainsKey("force"))
        {
            Console.Error.WriteLine($"File already exists: '{outPath}'. Pass --force to overwrite.");
            return 1;
        }

        var parentDir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrWhiteSpace(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        File.WriteAllText(outPath, yamlText);
        Console.WriteLine($"Generated '{outPath}' from preset '{presetName}'.");
        return 0;
    }

    // Validate baseline file BEFORE extraction for `diff`
    if (command == "diff")
    {
        resolvedBaselinePath = options.GetValueOrDefault("baseline") ?? BaselineStore.DiscoverBaselineFile(targetPath);
        if (string.IsNullOrWhiteSpace(resolvedBaselinePath) || !File.Exists(resolvedBaselinePath))
        {
            Console.Error.WriteLine($"No baseline file specified or found (expected '{BaselineStore.DefaultFileName}'). Run 'archlens baseline <path>' first.");
            return 2;
        }
    }

    // Validate explicit --plugins directory BEFORE extraction
    var explicitPluginsOpt = options.GetValueOrDefault("plugins");
    if (!string.IsNullOrWhiteSpace(explicitPluginsOpt) && !Directory.Exists(explicitPluginsOpt))
    {
        Console.Error.WriteLine($"Plugins directory not found: '{Path.GetFullPath(explicitPluginsOpt)}'.");
        return 2;
    }

    // Validate SQL read-only syntax BEFORE extraction
    if (command == "query")
    {
        GraphSqlQueryEngine.ValidateReadOnlySql(positional[1]);
    }

    ICodeGraphExtractor extractor = ResolveCliExtractor(targetPath, backend, roslynExtractor, metadataExtractor);
    var presetOption = options.GetValueOrDefault("preset");
    var rootNamespaceOption = options.GetValueOrDefault("root-namespace");

    if (command is "check" or "diff" or "report")
    {
        var loadedRules = new List<IArchitectureRule>();
        var ruleSources = new List<string>();

        if (!string.IsNullOrWhiteSpace(presetOption))
        {
            loadedRules.AddRange(ArchitecturePresets.CreateRules(presetOption, rootNamespaceOption));
            ruleSources.Add($"preset:{presetOption}");
        }

        var explicitRulesOpt = options.GetValueOrDefault("rules");
        if (!string.IsNullOrWhiteSpace(explicitRulesOpt))
        {
            var fullRules = Path.GetFullPath(explicitRulesOpt);
            if (!File.Exists(fullRules))
            {
                Console.Error.WriteLine($"Rules file not found: '{fullRules}'.");
                return 2;
            }
            loadedRules.AddRange(yamlProvider.LoadRules(fullRules));
            ruleSources.Add(fullRules);
        }
        else if (loadedRules.Count == 0)
        {
            var discovered = YamlRuleSetProvider.DiscoverConfigFile(targetPath);
            if (discovered is not null)
            {
                loadedRules.AddRange(yamlProvider.LoadRules(discovered));
                ruleSources.Add(discovered);
            }
        }

        if (loadedRules.Count == 0)
        {
            loadedRules.Add(new NoCyclesRule("BUILTIN-001", "No project cycles", "Projects must form a DAG.", RuleSeverity.Error, NodeKind.Project));
            loadedRules.Add(new NoCyclesRule("BUILTIN-002", "No namespace cycles", "Namespaces must form a DAG.", RuleSeverity.Warning, NodeKind.Namespace));
            ruleSources.Add("built-in");
        }

        preloadedRules = loadedRules;
        preloadedRulesSource = string.Join(" + ", ruleSources);

        if (preloadedRules.OfType<NoCyclesRule>().Any(r => r.Scope == NodeKind.Method) ||
            preloadedRules.OfType<NoDeadCodeRule>().Any(r => r.Scope == NodeKind.Method) ||
            preloadedRules.OfType<MaxMetricRule>().Any(r => r.Scope == NodeKind.Method))
        {
            includeMethods = true;
        }
    }

    var extractionOptions = new ExtractionOptions(
        IncludeTestProjects: includeTests,
        IncludeMethods: includeMethods,
        TargetFramework: targetFramework,
        IncludeAnalyzerDiagnostics: includeAnalyzers,
        AnalyzerCategories: analyzerCategories,
        Backend: backend);
    var snapshot = await extractor.ExtractAsync(targetPath, extractionOptions);

    // Only load plugins for commands that consume them AND only when explicitly opted in (--allow-plugins / --plugins / ARCHLENS_ALLOW_PLUGINS=1)
    var commandUsesPlugins = command is "hotspots" or "check" or "dead-code" or "diff" or "query" or "report" or "inspect";
    using var plugins = LoadCliPlugins(snapshot.Graph.RootPath, commandUsesPlugins, allowPlugins, explicitPluginsOpt);
    foreach (var diag in plugins.Diagnostics)
    {
        Console.Error.WriteLine($"[Plugin] {diag}");
    }

    switch (command)
    {
        case "overview":
        {
            var report = analyzer.GetOverview(snapshot);
            if (jsonOutput)
            {
                WriteOutput(JsonSerializer.Serialize(report, jsonOpts), outputFile);
            }
            else
            {
                Console.WriteLine($"ArchLens Overview: {report.TargetPath}");
                var methodPart = report.MethodsIncluded ? $" | Methods: {report.MethodCount ?? 0}" : "";
                Console.WriteLine($"Projects: {report.ProjectCount} | Namespaces: {report.NamespaceCount} | Types: {report.TypeCount}{methodPart} | Edges: {report.DependencyEdgeCount}");
                Console.WriteLine($"Cycles: {report.ProjectCycleCount} project, {report.NamespaceCycleCount} namespace");
                if (report.LoadStatus is { } ls && (ls.Degraded || ls.Approximate))
                {
                    Console.WriteLine($"Load Status: backend={ls.Backend}, loaded={ls.ProjectsLoaded}/{ls.ProjectsExpected}, degraded={ls.Degraded}, approximate={ls.Approximate}");
                    if (!string.IsNullOrWhiteSpace(ls.Note))
                    {
                        Console.WriteLine($"  Note: {ls.Note}");
                    }
                }
                Console.WriteLine();
                Console.WriteLine("Projects:");
                foreach (var p in report.Projects)
                {
                    var refs = p.ProjectReferences.Count > 0 ? $" -> [{string.Join(", ", p.ProjectReferences)}]" : "";
                    Console.WriteLine($"  - {p.Name} ({p.TargetFramework ?? "?"}): {p.TypeCount} types{refs}");
                }

                if (report.Diagnostics.Count > 0)
                {
                    Console.WriteLine();
                    Console.WriteLine($"Workspace Diagnostics ({report.Diagnostics.Count}):");
                    foreach (var d in report.Diagnostics.Take(5))
                    {
                        Console.WriteLine($"  [{d.Severity}] {d.Message}");
                    }
                }
            }
            return 0;
        }

        case "graph":
        {
            var scopeStr = options.GetValueOrDefault("scope", "namespace");
            var scope = scopeStr.ToLowerInvariant() switch
            {
                "project" => NodeKind.Project,
                "type" => NodeKind.Type,
                "method" => NodeKind.Method,
                _ => NodeKind.Namespace
            };
            var filter = options.GetValueOrDefault("filter");
            var maxNodes = int.TryParse(options.GetValueOrDefault("max-nodes"), out var m) ? m : 60;
            var report = analyzer.GetDependencyGraph(snapshot.Graph, scope, filter, maxNodes);

            if (jsonOutput)
            {
                WriteOutput(JsonSerializer.Serialize(report, jsonOpts), outputFile);
            }
            else
            {
                Console.WriteLine($"Dependency Graph ({report.Scope}): {report.ReturnedNodeCount}/{report.TotalNodesInScope} nodes, {report.Edges.Count} edges, {report.Cycles.Count} cycles");
                if (report.Cycles.Count > 0)
                {
                    Console.WriteLine("\nCycles:");
                    foreach (var c in report.Cycles)
                    {
                        Console.WriteLine($"  ! {string.Join(" -> ", c.Nodes)}");
                    }
                }

                Console.WriteLine("\nEdges:");
                foreach (var e in report.Edges)
                {
                    Console.WriteLine($"  {e.From} -> {e.To}");
                }
            }
            return 0;
        }

        case "hotspots":
        {
            var sortBy = options.GetValueOrDefault("by", "complexity");
            var scope = options.GetValueOrDefault("scope", "type").ToLowerInvariant() switch
            {
                "namespace" => NodeKind.Namespace,
                "method" => NodeKind.Method,
                _ => NodeKind.Type
            };
            var filter = options.GetValueOrDefault("filter");
            var top = int.TryParse(options.GetValueOrDefault("top"), out var t) ? t : 15;
            var report = analyzer.GetHotspots(snapshot.Graph, sortBy, filter, scope, top, plugins.MetricCalculators);

            if (jsonOutput)
            {
                WriteOutput(JsonSerializer.Serialize(report, jsonOpts), outputFile);
            }
            else
            {
                Console.WriteLine($"Top {report.Items.Count} Hotspots (scope={report.Scope}, sortBy={report.SortBy}):");
                foreach (var item in report.Items)
                {
                    var loc = item.Location is not null ? $" ({item.Location.ToShortString()})" : "";
                    var customPart = item.CustomMetrics is { Count: > 0 }
                        ? $", {string.Join(", ", item.CustomMetrics.Select(kv => $"{kv.Key}={kv.Value.ToString("0.##", CultureInfo.InvariantCulture)}"))}"
                        : "";
                    Console.WriteLine($"  - {item.Name}: maxCC={item.MaxMethodComplexity}, maxCog={item.MaxMethodCognitiveComplexity}, LCOM4={item.Lcom4}, Ce={item.EfferentCoupling}, Ca={item.AfferentCoupling}, I={item.Instability.ToString("0.00", CultureInfo.InvariantCulture)}, LOC={item.LineCount}, ctor={item.ConstructorParameterCount}{customPart}{loc}");
                }
            }
            return 0;
        }

        case "check":
        {
            var rules = plugins.Rules.Count > 0 ? [.. preloadedRules!, .. plugins.Rules] : preloadedRules!;
            var rulesSource = preloadedRulesSource!;
            var extraDetectors = plugins.EntryPointDetectors.Count > 0 ? plugins.EntryPointDetectors : null;

            // Evaluate architecture rules once (avoids duplicating rule/plugin execution when --include-analyzers is set)
            var ruleOnlyReport = analyzer.CheckRules(
                snapshot.Graph,
                rules,
                maxViolations: int.MaxValue,
                includeAnalyzerDiagnostics: false,
                extraMetricCalculators: plugins.MetricCalculators,
                extraEntryPointDetectors: extraDetectors);

            var report = includeAnalyzers && snapshot.Graph.AnalyzerDiagnostics.Count > 0
                ? MergeAnalyzerDiagnostics(ruleOnlyReport, snapshot.Graph.AnalyzerDiagnostics)
                : ruleOnlyReport;

            if (sarifOutput)
            {
                WriteOutput(
                    SarifExporter.Export(
                        violations: ruleOnlyReport.Violations,
                        analyzerDiagnostics: includeAnalyzers ? snapshot.Graph.AnalyzerDiagnostics : null),
                    outputFile);
            }
            else if (jsonOutput)
            {
                WriteOutput(JsonSerializer.Serialize(new
                {
                    rulesSource,
                    loadedPlugins = plugins.LoadedPluginFiles.Count > 0 ? plugins.LoadedPluginFiles : null,
                    pluginDiagnostics = plugins.Diagnostics.Count > 0 ? plugins.Diagnostics : null,
                    report
                }, jsonOpts), outputFile);
            }
            else
            {
                Console.WriteLine($"Rules Check ({rulesSource}): {report.RulesEvaluated} rules | {report.ErrorCount} errors, {report.WarningCount} warnings, {report.InfoCount} info");
                foreach (var v in report.Violations)
                {
                    var loc = v.Location is not null ? $" at {v.Location.ToShortString()}" : "";
                    Console.WriteLine($"  [{v.Severity}] {v.RuleId}: {v.Message}{loc}");
                }
            }
            return report.ErrorCount > 0 ? 1 : 0;
        }

        case "dead-code":
        {
            var scope = string.Equals(options.GetValueOrDefault("scope", "type"), "method", StringComparison.OrdinalIgnoreCase)
                ? NodeKind.Method
                : NodeKind.Type;
            var treatPublicApi = !options.ContainsKey("no-public-api-entry-points") &&
                                 !string.Equals(options.GetValueOrDefault("public-api-entry-points"), "false", StringComparison.OrdinalIgnoreCase);
            var filter = options.GetValueOrDefault("filter");
            var top = int.TryParse(options.GetValueOrDefault("top"), out var t) ? t : 100;
            var report = analyzer.AnalyzeDeadCode(snapshot.Graph, scope, treatPublicApi, filter, top,
                plugins.EntryPointDetectors.Count > 0 ? plugins.EntryPointDetectors : null,
                testsExcluded: !includeTests);

            if (jsonOutput)
            {
                WriteOutput(JsonSerializer.Serialize(report, jsonOpts), outputFile);
            }
            else
            {
                Console.WriteLine($"Dead Code ({report.Scope}): {report.UnreferencedCount} candidates / {report.TotalEvaluated} evaluated ({report.EntryPointCount} entry points)");
                Console.WriteLine($"Active detectors: {string.Join(", ", report.ActiveDetectors)}");
                if (!string.IsNullOrWhiteSpace(report.Note))
                {
                    Console.WriteLine($"Note: {report.Note}");
                }
                foreach (var c in report.Candidates)
                {
                    var loc = c.Location is not null ? $" at {c.Location.ToShortString()}" : "";
                    Console.WriteLine($"  - [{c.Accessibility}] {c.FullName} — {c.Reason}{loc}");
                }
            }
            return 0;
        }

        case "di":
        {
            if (extractor.Name == "IL")
            {
                Console.Error.WriteLine("Warning: 'di' analysis requires Roslyn source extraction (--backend roslyn); IL backend does not parse IServiceCollection call sites.");
            }
            var report = analyzer.AnalyzeDi(snapshot.Graph);
            if (jsonOutput)
            {
                WriteOutput(JsonSerializer.Serialize(report, jsonOpts), outputFile);
            }
            else
            {
                Console.WriteLine($"DI Analysis: {report.RegistrationCount} registrations | {report.CaptiveDependencyCount} captive dependencies | {report.SingleImplementationCount} single-implementation interfaces");
                if (report.CaptiveDependencies.Count > 0)
                {
                    Console.WriteLine("\nCaptive Dependencies:");
                    foreach (var issue in report.CaptiveDependencies)
                    {
                        var loc = issue.Location is not null ? $" at {issue.Location.ToShortString()}" : "";
                        Console.WriteLine($"  ! {issue.DependencyPath}{loc}");
                    }
                }

                if (report.SingleImplementationInterfaces.Count > 0)
                {
                    Console.WriteLine("\nSingle-Implementation Interfaces:");
                    foreach (var s in report.SingleImplementationInterfaces.Take(25))
                    {
                        var diTag = s.IsDiRegistered ? "DI" : "non-DI";
                        Console.WriteLine($"  - {s.InterfaceTypeId} -> {s.ImplementationTypeId} ({diTag}, {s.ConsumerCount} consumers)");
                    }
                }
            }
            return options.ContainsKey("fail-on-captive") && report.CaptiveDependencyCount > 0 ? 1 : 0;
        }

        case "analyzers":
        {
            if (extractor.Name == "IL")
            {
                Console.Error.WriteLine("Warning: 'analyzers' requires Roslyn source extraction (--backend roslyn); IL backend does not run Roslyn DiagnosticAnalyzers.");
            }
            var top = int.TryParse(options.GetValueOrDefault("top"), out var t) ? t : 100;
            var report = analyzer.GetAnalyzerDiagnostics(snapshot.Graph, analyzerCategories, sarifOutput ? int.MaxValue : top);
            if (sarifOutput)
            {
                WriteOutput(SarifExporter.Export(analyzerDiagnostics: report.Diagnostics), outputFile);
            }
            else if (jsonOutput)
            {
                WriteOutput(JsonSerializer.Serialize(report, jsonOpts), outputFile);
            }
            else
            {
                Console.WriteLine($"Roslyn Analyzers: {report.TotalDiagnostics} diagnostics ({report.ErrorCount} errors, {report.WarningCount} warnings, {report.InfoCount} info)");
                foreach (var d in report.Diagnostics)
                {
                    var loc = d.Location is not null ? $" at {d.Location.ToShortString()}" : "";
                    Console.WriteLine($"  [{d.Severity}] {d.Id} ({d.Category}): {d.Message}{loc}");
                }
            }
            return report.ErrorCount > 0 ? 1 : 0;
        }

        case "baseline":
        {
            var savePath = !string.IsNullOrWhiteSpace(outputFile)
                ? Path.GetFullPath(outputFile)
                : Path.Combine(snapshot.Graph.RootPath, BaselineStore.DefaultFileName);
            BaselineStore.Save(savePath, snapshot);

            if (jsonOutput)
            {
                WriteOutput(JsonSerializer.Serialize(new
                {
                    status = "baseline_saved",
                    baselineFile = savePath,
                    snapshotId = snapshot.SnapshotId,
                    capturedAt = snapshot.CapturedAt,
                    typeCount = snapshot.Graph.GetNodes(NodeKind.Type).Count(),
                    edgeCount = snapshot.Graph.Edges.Count
                }, jsonOpts), null);
            }
            else
            {
                Console.WriteLine($"Saved baseline '{snapshot.SnapshotId}' to {savePath} ({snapshot.Graph.GetNodes(NodeKind.Type).Count()} types, {snapshot.Graph.Edges.Count} edges).");
            }
            return 0;
        }

        case "diff":
        {
            var baseline = BaselineStore.Load(resolvedBaselinePath!);
            var diffRules = plugins.Rules.Count > 0 ? [.. preloadedRules!, .. plugins.Rules] : preloadedRules!;
            var extraDetectors = plugins.EntryPointDetectors.Count > 0 ? plugins.EntryPointDetectors : null;
            var diff = analyzer.DiffSnapshots(baseline, snapshot, diffRules, plugins.MetricCalculators, extraDetectors);

            if (sarifOutput)
            {
                WriteOutput(SarifExporter.Export(violations: diff.NewViolations), outputFile);
            }
            else if (jsonOutput)
            {
                WriteOutput(JsonSerializer.Serialize(new { baselinePath = Path.GetFullPath(resolvedBaselinePath!), rulesSource = preloadedRulesSource, diff }, jsonOpts), outputFile);
            }
            else
            {
                Console.WriteLine($"Architecture Diff ({diff.BaselineSnapshotId} -> {diff.CurrentSnapshotId}):");
                Console.WriteLine($"  Types delta: {diff.TypeCountDelta:+#;-#;0} | Edges delta: {diff.EdgeCountDelta:+#;-#;0} | Cycles delta: {diff.CycleCountDelta:+#;-#;0} | Violations delta: {diff.ViolationCountDelta:+#;-#;0}");
                if (diff.NewViolations.Count > 0)
                {
                    Console.WriteLine($"\nNew Violations ({diff.NewViolations.Count}):");
                    foreach (var v in diff.NewViolations)
                    {
                        var loc = v.Location is not null ? $" at {v.Location.ToShortString()}" : "";
                        Console.WriteLine($"  + [{v.Severity}] {v.RuleId}: {v.Message}{loc}");
                    }
                }
                if (diff.ResolvedViolations.Count > 0)
                {
                    Console.WriteLine($"\nResolved Violations ({diff.ResolvedViolations.Count}):");
                    foreach (var v in diff.ResolvedViolations)
                    {
                        Console.WriteLine($"  - [{v.Severity}] {v.RuleId}: {v.Message}");
                    }
                }
            }

            var hasNewErrors = diff.NewViolations.Any(v => v.Severity == RuleSeverity.Error) || diff.AddedCycles.Count > 0;
            return options.ContainsKey("fail-on-new-errors") && hasNewErrors ? 1 : 0;
        }

        case "query":
        {
            var sqlEngine = new GraphSqlQueryEngine();
            var maxRows = int.TryParse(options.GetValueOrDefault("max-rows"), out var mr) ? mr : 100;
            var result = sqlEngine.Execute(snapshot.Graph, positional[1], maxRows, plugins.MetricCalculators);
            if (jsonOutput)
            {
                WriteOutput(JsonSerializer.Serialize(result, jsonOpts), outputFile);
            }
            else
            {
                Console.WriteLine(string.Join(" | ", result.Columns));
                Console.WriteLine(new string('-', Math.Min(120, result.Columns.Sum(c => c.Length + 3))));
                foreach (var row in result.Rows)
                {
                    Console.WriteLine(string.Join(" | ", result.Columns.Select(c => row.GetValueOrDefault(c)?.ToString() ?? "NULL")));
                }
                if (result.Truncated)
                {
                    Console.WriteLine($"... (truncated at {result.RowCount} rows)");
                }
            }
            return 0;
        }

        case "export":
        {
            var diagramFormat = options.GetValueOrDefault("format", "mermaid");
            var scopeStr = options.GetValueOrDefault("scope", "namespace");
            var scope = scopeStr.ToLowerInvariant() switch
            {
                "project" => NodeKind.Project,
                "type" => NodeKind.Type,
                "method" => NodeKind.Method,
                _ => NodeKind.Namespace
            };
            var filter = options.GetValueOrDefault("filter");
            var maxNodes = int.TryParse(options.GetValueOrDefault("max-nodes"), out var m) ? m : 60;
            var diagram = GraphVisualizer.Export(snapshot.Graph, diagramFormat, scope, filter, maxNodes);
            WriteOutput(diagram, outputFile);
            return 0;
        }

        case "report":
        {
            var rules = plugins.Rules.Count > 0 ? [.. preloadedRules!, .. plugins.Rules] : preloadedRules!;
            var extraDetectors = plugins.EntryPointDetectors.Count > 0 ? plugins.EntryPointDetectors : null;
            var rulesReport = analyzer.CheckRules(snapshot.Graph, rules, maxViolations: 500, includeAnalyzerDiagnostics: includeAnalyzers, extraMetricCalculators: plugins.MetricCalculators, extraEntryPointDetectors: extraDetectors);
            var diReport = analyzer.AnalyzeDi(snapshot.Graph);
            var deadReport = analyzer.AnalyzeDeadCode(snapshot.Graph, NodeKind.Type, treatPublicApiAsEntryPoint: true, maxItems: 100, extraDetectors: extraDetectors);

            ArchitectureSnapshot? baselineSnap = null;
            var baselinePath = options.GetValueOrDefault("baseline") ?? BaselineStore.DiscoverBaselineFile(targetPath);
            if (!string.IsNullOrWhiteSpace(baselinePath) && File.Exists(baselinePath))
            {
                baselineSnap = BaselineStore.Load(baselinePath);
            }

            var scopeStr = options.GetValueOrDefault("scope", "namespace");
            var matrixScope = scopeStr.ToLowerInvariant() switch
            {
                "project" => NodeKind.Project,
                "type" => NodeKind.Type,
                _ => NodeKind.Namespace
            };

            var html = HtmlReportGenerator.Generate(snapshot, rulesReport, diReport, deadReport, baselineSnap, rules, matrixScope, extraMetricCalculators: plugins.MetricCalculators, extraEntryPointDetectors: extraDetectors);
            var reportPath = !string.IsNullOrWhiteSpace(outputFile)
                ? Path.GetFullPath(outputFile)
                : Path.Combine(Directory.GetCurrentDirectory(), "archlens-report.html");
            WriteOutput(html, reportPath);
            return 0;
        }

        case "inspect":
        {
            var report = analyzer.InspectSymbol(snapshot.Graph, positional[1], plugins.MetricCalculators);
            if (report is null)
            {
                Console.Error.WriteLine($"Symbol '{positional[1]}' not found.");
                return 1;
            }

            if (jsonOutput)
            {
                WriteOutput(JsonSerializer.Serialize(report, jsonOpts), outputFile);
            }
            else
            {
                Console.WriteLine($"{report.Node.FullName} ({report.Node.TypeKind?.ToString() ?? report.Node.Kind.ToString()}, {report.Node.Location?.ToShortString() ?? "no source"})");
                if (report.Metrics is not null)
                {
                    var customPart = report.Metrics.CustomMetrics is { Count: > 0 }
                        ? $", {string.Join(", ", report.Metrics.CustomMetrics.Select(kv => $"{kv.Key}={kv.Value.ToString("0.##", CultureInfo.InvariantCulture)}"))}"
                        : "";
                    Console.WriteLine($"Metrics: maxCC={report.Metrics.MaxMethodComplexity}, maxCog={report.Metrics.MaxMethodCognitiveComplexity}, LCOM4={report.Metrics.Lcom4}, Ca={report.Metrics.AfferentCoupling}, Ce={report.Metrics.EfferentCoupling}, I={report.Metrics.Instability.ToString("0.00", CultureInfo.InvariantCulture)}, LOC={report.Metrics.LineCount}{customPart}");
                }
                Console.WriteLine($"Outgoing ({report.OutgoingDependencies.Count}):");
                foreach (var d in report.OutgoingDependencies)
                {
                    Console.WriteLine($"  -> {d.TargetId} [{string.Join(", ", d.Kinds)}]");
                }
                Console.WriteLine($"Incoming ({report.IncomingDependents.Count}):");
                foreach (var d in report.IncomingDependents)
                {
                    Console.WriteLine($"  <- {d.TargetId} [{string.Join(", ", d.Kinds)}]");
                }
            }
            return 0;
        }

        case "impact":
        {
            var depth = int.TryParse(options.GetValueOrDefault("depth"), out var d) ? d : 4;
            var result = analyzer.AnalyzeImpact(snapshot.Graph, positional[1], depth);
            if (result is null)
            {
                Console.Error.WriteLine($"Symbol '{positional[1]}' not found.");
                return 1;
            }

            if (jsonOutput)
            {
                WriteOutput(JsonSerializer.Serialize(result, jsonOpts), outputFile);
            }
            else
            {
                Console.WriteLine($"Blast Radius for {result.TargetFullName}:");
                Console.WriteLine($"Direct dependents: {result.DirectDependentsCount} | Total transitive: {result.TotalTransitiveDependentsCount}");
                Console.WriteLine($"Affected projects: {string.Join(", ", result.AffectedProjects)}");
                foreach (var node in result.ImpactedNodes)
                {
                    Console.WriteLine($"  [d={node.Distance}] {node.FullName} ({node.Location?.ToShortString() ?? node.ProjectName})");
                }
            }
            return 0;
        }

        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            PrintHelp();
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

static LoadedPlugins LoadCliPlugins(string rootPath, bool commandUsesPlugins, bool allowPlugins, string? explicitPluginsDir)
{
    if (!commandUsesPlugins)
    {
        return LoadedPlugins.Empty;
    }

    if (!string.IsNullOrWhiteSpace(explicitPluginsDir))
    {
        return PluginLoader.LoadFromDirectory(explicitPluginsDir);
    }

    var discovered = PluginLoader.DiscoverPluginsDirectory(rootPath);
    if (discovered is null)
    {
        return LoadedPlugins.Empty;
    }

    if (!allowPlugins)
    {
        Console.Error.WriteLine($"[Plugin] Detected plugins in '{discovered}' but skipped for safety. Pass --allow-plugins (or set ARCHLENS_ALLOW_PLUGINS=1) to load repository plugins.");
        return LoadedPlugins.Empty;
    }

    return PluginLoader.LoadFromDirectory(discovered);
}

static RuleCheckReport MergeAnalyzerDiagnostics(RuleCheckReport baseReport, IReadOnlyList<AnalyzerDiagnostic> diagnostics)
{
    var combined = baseReport.Violations.ToList();
    foreach (var diag in diagnostics)
    {
        combined.Add(new RuleViolation(
            RuleId: diag.Id,
            RuleName: string.IsNullOrWhiteSpace(diag.Title) ? diag.Id : diag.Title,
            Severity: diag.Severity,
            Message: $"[{diag.Category}] {diag.Message}",
            NodeId: diag.ProjectName,
            Location: diag.Location));
    }

    var ordered = combined
        .OrderByDescending(v => v.Severity)
        .ThenBy(v => v.RuleId, StringComparer.Ordinal)
        .ThenBy(v => v.NodeId, StringComparer.Ordinal)
        .ToList();

    return new RuleCheckReport(
        RulesEvaluated: baseReport.RulesEvaluated,
        TotalViolations: ordered.Count,
        ErrorCount: ordered.Count(v => v.Severity == RuleSeverity.Error),
        WarningCount: ordered.Count(v => v.Severity == RuleSeverity.Warning),
        InfoCount: ordered.Count(v => v.Severity == RuleSeverity.Info),
        Truncated: false,
        Violations: ordered);
}

static ICodeGraphExtractor ResolveCliExtractor(
    string targetPath,
    string? backend,
    RoslynCodeGraphExtractor roslyn,
    MetadataCodeGraphExtractor metadata)
{
    if (!string.IsNullOrWhiteSpace(backend) && !backend.Equals("auto", StringComparison.OrdinalIgnoreCase))
    {
        if (backend.Equals("il", StringComparison.OrdinalIgnoreCase) ||
            backend.Equals("metadata", StringComparison.OrdinalIgnoreCase) ||
            backend.Equals("cecil", StringComparison.OrdinalIgnoreCase))
        {
            return metadata;
        }
        if (backend.Equals("roslyn", StringComparison.OrdinalIgnoreCase))
        {
            return roslyn;
        }
        throw new NotSupportedException($"Unknown backend '{backend}'. Supported values: auto, roslyn, il.");
    }

    return metadata.CanHandle(targetPath) ? metadata : roslyn;
}

static string ResolveFormat(Dictionary<string, string> options)
{
    if (options.TryGetValue("format", out var explicitFmt))
    {
        return explicitFmt.Trim().ToLowerInvariant();
    }
    if (options.ContainsKey("sarif")) return "sarif";
    if (options.ContainsKey("json")) return "json";
    return "text";
}

static IReadOnlyList<string>? ParseCategories(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return null;
    return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

static void WriteOutput(string content, string? outputFile)
{
    if (!string.IsNullOrWhiteSpace(outputFile))
    {
        var full = Path.GetFullPath(outputFile);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(full, content);
        Console.WriteLine($"Wrote output to {full}");
    }
    else
    {
        Console.WriteLine(content);
    }
}

static void PrintHelp()
{
    Console.WriteLine("""
    ArchLens CLI - Agent-friendly C# Architecture Analyzer

    Global options:
      --backend auto|roslyn|il   Choose extraction backend ('il' reads compiled .dll+.pdb 5-10x faster)
      --tfm <tfm>                Target framework for multi-targeted projects
      --include-tests            Include test projects
      --allow-plugins            Opt-in to load repository plugins from .archlens/plugins/ (or set ARCHLENS_ALLOW_PLUGINS=1)
      --plugins <dir>            Load plugins from an explicit directory (implies --allow-plugins)

    Usage:
      archlens overview  <path> [--include-tests] [--include-methods] [--tfm <tfm>] [--json]
      archlens graph     <path> [--scope project|namespace|type|method] [--filter <glob>] [--max-nodes 60] [--include-tests] [--tfm <tfm>] [--json]
      archlens hotspots  <path> [--by complexity|cognitive|lcom4|coupling|afferent|instability|distance|lines|ctor|<custom>] [--scope type|namespace|method] [--filter <glob>] [--top 15] [--include-tests] [--tfm <tfm>] [--json]
      archlens check     <path> [--rules <yaml>] [--preset <name>] [--root-namespace <ns>] [--include-analyzers] [--analyzer-categories Design,Maintainability,Performance] [--format text|json|sarif] [--output <file>] [--include-tests] [--include-methods] [--tfm <tfm>]
      archlens query     <path> "<sql>" [--max-rows 100] [--include-methods] [--include-analyzers] [--json]
      archlens export    <path> [--format mermaid|dot|structurizr] [--scope project|namespace|type|method] [--filter <glob>] [--max-nodes 60] [--output <file>]
      archlens report    <path> [--output archlens-report.html] [--rules <yaml>] [--preset <name>] [--root-namespace <ns>] [--baseline .archlens-baseline.json] [--scope project|namespace|type]
      archlens presets   [--json]
      archlens init      [dir] [--preset clean-architecture|vertical-slice|modular-monolith|ddd] [--root-namespace <ns>] [--output <file>] [--force]
      archlens dead-code <path> [--scope type|method] [--filter <glob>] [--public-api-entry-points true|false] [--top 100] [--include-tests] [--tfm <tfm>] [--json]
      archlens di        <path> [--fail-on-captive] [--include-tests] [--tfm <tfm>] [--json]
      archlens analyzers <path> [--categories Design,Maintainability,Performance] [--format text|json|sarif] [--output <file>] [--top 100] [--include-tests] [--tfm <tfm>]
      archlens baseline  <path> [--output .archlens-baseline.json] [--include-tests] [--include-methods] [--tfm <tfm>] [--json]
      archlens diff      <path> [--baseline .archlens-baseline.json] [--rules <yaml>] [--preset <name>] [--root-namespace <ns>] [--format text|json|sarif] [--output <file>] [--fail-on-new-errors] [--include-tests] [--include-methods] [--tfm <tfm>]
      archlens inspect   <path> <symbol> [--include-methods] [--tfm <tfm>] [--json]
      archlens impact    <path> <symbol> [--depth 4] [--include-methods] [--tfm <tfm>] [--json]
    """);
}
