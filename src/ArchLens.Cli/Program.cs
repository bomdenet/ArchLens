using System.Text.Json;
using System.Text.Json.Serialization;
using ArchLens.Analysis;
using ArchLens.Extraction.Metadata;
using ArchLens.Extraction.Roslyn;
using ArchLens.Model;
using ArchLens.Query;
using ArchLens.Rules.Yaml;

MsBuildBootstrapper.EnsureRegistered();

if (args.Length == 0 || args[0] is "-h" or "--help" or "help" || args.Any(a => a is "-h" or "--help"))
{
    PrintHelp();
    return 0;
}

var command = args[0].ToLowerInvariant();
var positional = new List<string>();
var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
var booleanFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "json", "sarif", "include-tests", "include-methods", "include-analyzers",
    "fail-on-captive", "fail-on-new-errors", "no-public-api-entry-points", "force", "help"
};

for (var i = 1; i < args.Length; i++)
{
    if (args[i].StartsWith("--", StringComparison.Ordinal))
    {
        var key = args[i][2..];
        if (booleanFlags.Contains(key))
        {
            options[key] = "true";
        }
        else if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
        {
            options[key] = args[++i];
        }
        else
        {
            options[key] = "true";
        }
    }
    else
    {
        positional.Add(args[i]);
    }
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

using var roslynExtractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
var metadataExtractor = new MetadataCodeGraphExtractor();
var analyzer = new ArchitectureAnalyzerService();
var yamlProvider = new YamlRuleSetProvider();

IReadOnlyList<IArchitectureRule>? preloadedRules = null;
string? preloadedRulesSource = null;

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
        var outPath = !string.IsNullOrWhiteSpace(outputFile)
            ? Path.GetFullPath(outputFile)
            : Path.Combine(Directory.Exists(targetPath) ? Path.GetFullPath(targetPath) : Path.GetDirectoryName(Path.GetFullPath(targetPath))!, ".archlens.yaml");

        if (File.Exists(outPath) && !options.ContainsKey("force"))
        {
            Console.Error.WriteLine($"File already exists: '{outPath}'. Pass --force to overwrite.");
            return 1;
        }

        File.WriteAllText(outPath, yamlText);
        Console.WriteLine($"Generated '{outPath}' from preset '{presetName}'.");
        return 0;
    }

    ICodeGraphExtractor extractor = ResolveCliExtractor(targetPath, backend, roslynExtractor, metadataExtractor);
    var presetOption = options.GetValueOrDefault("preset");
    var rootNamespaceOption = options.GetValueOrDefault("root-namespace");

    if (command is "check" or "diff" or "report")
    {
        if (!string.IsNullOrWhiteSpace(presetOption))
        {
            preloadedRulesSource = $"preset:{presetOption}";
            preloadedRules = ArchitecturePresets.CreateRules(presetOption, rootNamespaceOption);
        }
        else
        {
            var rulesPath = options.GetValueOrDefault("rules") ?? YamlRuleSetProvider.DiscoverConfigFile(targetPath);
            preloadedRulesSource = rulesPath ?? "built-in";
            preloadedRules = rulesPath is not null
                ? yamlProvider.LoadRules(rulesPath)
                : [
                    new NoCyclesRule("BUILTIN-001", "No project cycles", "Projects must form a DAG.", RuleSeverity.Error, NodeKind.Project),
                    new NoCyclesRule("BUILTIN-002", "No namespace cycles", "Namespaces must form a DAG.", RuleSeverity.Warning, NodeKind.Namespace)
                  ];
        }

        if (preloadedRules.OfType<NoCyclesRule>().Any(r => r.Scope == NodeKind.Method) ||
            preloadedRules.OfType<NoDeadCodeRule>().Any(r => r.Scope == NodeKind.Method))
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
    var pluginsDir = options.GetValueOrDefault("plugins") ?? PluginLoader.DiscoverPluginsDirectory(snapshot.Graph.RootPath);
    using var plugins = pluginsDir is not null ? PluginLoader.LoadFromDirectory(pluginsDir) : LoadedPlugins.Empty;

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
                var methodPart = report.MethodCount > 0 ? $" | Methods: {report.MethodCount}" : "";
                Console.WriteLine($"Projects: {report.ProjectCount} | Namespaces: {report.NamespaceCount} | Types: {report.TypeCount}{methodPart} | Edges: {report.DependencyEdgeCount}");
                Console.WriteLine($"Cycles: {report.ProjectCycleCount} project, {report.NamespaceCycleCount} namespace");
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
                    Console.WriteLine($"  - {item.Name}: maxCC={item.MaxMethodComplexity}, maxCog={item.MaxMethodCognitiveComplexity}, LCOM4={item.Lcom4}, Ce={item.EfferentCoupling}, Ca={item.AfferentCoupling}, I={item.Instability:0.00}, LOC={item.LineCount}, ctor={item.ConstructorParameterCount}{loc}");
                }
            }
            return 0;
        }

        case "check":
        {
            var rules = plugins.Rules.Count > 0 ? [.. preloadedRules!, .. plugins.Rules] : preloadedRules!;
            var rulesSource = preloadedRulesSource!;
            var ruleOnlyReport = analyzer.CheckRules(snapshot.Graph, rules, maxViolations: int.MaxValue, includeAnalyzerDiagnostics: false, extraMetricCalculators: plugins.MetricCalculators);
            var report = includeAnalyzers
                ? analyzer.CheckRules(snapshot.Graph, rules, maxViolations: int.MaxValue, includeAnalyzerDiagnostics: true, extraMetricCalculators: plugins.MetricCalculators)
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
                WriteOutput(JsonSerializer.Serialize(new { rulesSource, report }, jsonOpts), outputFile);
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
                plugins.EntryPointDetectors.Count > 0 ? plugins.EntryPointDetectors : null);

            if (jsonOutput)
            {
                WriteOutput(JsonSerializer.Serialize(report, jsonOpts), outputFile);
            }
            else
            {
                Console.WriteLine($"Dead Code ({report.Scope}): {report.UnreferencedCount} candidates / {report.TotalEvaluated} evaluated ({report.EntryPointCount} entry points)");
                Console.WriteLine($"Active detectors: {string.Join(", ", report.ActiveDetectors)}");
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
            var baselinePath = options.GetValueOrDefault("baseline") ?? BaselineStore.DiscoverBaselineFile(targetPath);
            if (string.IsNullOrWhiteSpace(baselinePath))
            {
                Console.Error.WriteLine($"No baseline file specified or found (expected '{BaselineStore.DefaultFileName}'). Run 'archlens baseline <path>' first.");
                return 2;
            }

            var baseline = BaselineStore.Load(baselinePath);
            var diffRules = plugins.Rules.Count > 0 ? [.. preloadedRules!, .. plugins.Rules] : preloadedRules!;
            var diff = analyzer.DiffSnapshots(baseline, snapshot, diffRules, plugins.MetricCalculators);

            if (sarifOutput)
            {
                WriteOutput(SarifExporter.Export(violations: diff.NewViolations), outputFile);
            }
            else if (jsonOutput)
            {
                WriteOutput(JsonSerializer.Serialize(new { baselinePath = Path.GetFullPath(baselinePath), rulesSource = preloadedRulesSource, diff }, jsonOpts), outputFile);
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
            if (positional.Count < 2)
            {
                Console.Error.WriteLine("Usage: archlens query <path> \"<SQL>\" [--max-rows 100] [--include-methods] [--json]");
                return 2;
            }
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
            var rulesReport = analyzer.CheckRules(snapshot.Graph, rules, maxViolations: 500, includeAnalyzerDiagnostics: includeAnalyzers, extraMetricCalculators: plugins.MetricCalculators);
            var diReport = analyzer.AnalyzeDi(snapshot.Graph);
            var deadReport = analyzer.AnalyzeDeadCode(snapshot.Graph, NodeKind.Type, treatPublicApiAsEntryPoint: true, maxItems: 100,
                extraDetectors: plugins.EntryPointDetectors.Count > 0 ? plugins.EntryPointDetectors : null);

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

            var html = HtmlReportGenerator.Generate(snapshot, rulesReport, diReport, deadReport, baselineSnap, rules, matrixScope, extraMetricCalculators: plugins.MetricCalculators);
            var reportPath = !string.IsNullOrWhiteSpace(outputFile)
                ? Path.GetFullPath(outputFile)
                : Path.Combine(Directory.GetCurrentDirectory(), "archlens-report.html");
            WriteOutput(html, reportPath);
            return 0;
        }

        case "inspect":
        {
            if (positional.Count < 2)
            {
                Console.Error.WriteLine("Usage: archlens inspect <path> <symbol> [--include-methods] [--json]");
                return 2;
            }
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
                    Console.WriteLine($"Metrics: maxCC={report.Metrics.MaxMethodComplexity}, maxCog={report.Metrics.MaxMethodCognitiveComplexity}, LCOM4={report.Metrics.Lcom4}, Ca={report.Metrics.AfferentCoupling}, Ce={report.Metrics.EfferentCoupling}, I={report.Metrics.Instability:0.00}, LOC={report.Metrics.LineCount}");
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
            if (positional.Count < 2)
            {
                Console.Error.WriteLine("Usage: archlens impact <path> <symbol> [--depth 4] [--include-methods] [--json]");
                return 2;
            }
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

    Usage:
      archlens overview  <path> [--include-tests] [--include-methods] [--tfm <tfm>] [--json]
      archlens graph     <path> [--scope project|namespace|type|method] [--filter <glob>] [--max-nodes 60] [--include-tests] [--tfm <tfm>] [--json]
      archlens hotspots  <path> [--by complexity|cognitive|lcom4|coupling|afferent|instability|distance|lines|ctor] [--scope type|namespace|method] [--filter <glob>] [--top 15] [--include-tests] [--tfm <tfm>] [--json]
      archlens check     <path> [--rules <yaml>] [--preset <name>] [--root-namespace <ns>] [--include-analyzers] [--analyzer-categories Design,Maintainability,Performance] [--format text|json|sarif] [--output <file>] [--include-tests] [--include-methods] [--tfm <tfm>]
      archlens query     <path> "<sql>" [--max-rows 100] [--include-methods] [--include-analyzers] [--json]
      archlens export    <path> [--format mermaid|dot|structurizr] [--scope project|namespace|type|method] [--filter <glob>] [--max-nodes 60] [--output <file>]
      archlens report    <path> [--output archlens-report.html] [--rules <yaml>] [--preset <name>] [--baseline .archlens-baseline.json] [--scope project|namespace|type]
      archlens presets   [--json]
      archlens init      [dir] [--preset clean-architecture|vertical-slice|modular-monolith|ddd] [--root-namespace <ns>] [--force]
      archlens dead-code <path> [--scope type|method] [--filter <glob>] [--public-api-entry-points true|false] [--top 100] [--include-tests] [--tfm <tfm>] [--json]
      archlens di        <path> [--fail-on-captive] [--include-tests] [--tfm <tfm>] [--json]
      archlens analyzers <path> [--categories Design,Maintainability,Performance] [--format text|json|sarif] [--output <file>] [--top 100] [--include-tests] [--tfm <tfm>]
      archlens baseline  <path> [--output .archlens-baseline.json] [--include-tests] [--include-methods] [--tfm <tfm>] [--json]
      archlens diff      <path> [--baseline .archlens-baseline.json] [--rules <yaml>] [--format text|json|sarif] [--output <file>] [--fail-on-new-errors] [--include-tests] [--include-methods] [--tfm <tfm>]
      archlens inspect   <path> <symbol> [--include-methods] [--tfm <tfm>] [--json]
      archlens impact    <path> <symbol> [--depth 4] [--include-methods] [--tfm <tfm>] [--json]
    """);
}
