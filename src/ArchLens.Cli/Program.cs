using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArchLens.Analysis;
using ArchLens.Extraction.Metadata;
using ArchLens.Extraction.Roslyn;
using ArchLens.Model;
using ArchLens.Query;
using ArchLens.Rules.Yaml;

MsBuildBootstrapper.EnsureRegistered();

if (args.Length == 0 || (args.Length == 1 && args[0] is "-h" or "--help" or "help"))
{
    PrintHelp();
    return 0;
}

if (args.Length == 1 && args[0] is "-v" or "--version" or "version")
{
    var ver = typeof(ArchitectureAnalyzerService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(ArchitectureAnalyzerService).Assembly.GetName().Version?.ToString(3)
        ?? "0.12.0";
    Console.WriteLine($"ArchLens {ver}");
    return 0;
}

string? command = null;
var positional = new List<string>();
var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

var knownCommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "presets", "init", "overview", "graph", "hotspots", "check",
    "dead-code", "di", "analyzers", "baseline", "diff", "query",
    "export", "report", "inspect", "impact", "batch"
};

var booleanFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "json", "sarif", "include-tests", "include-methods", "include-analyzers",
    "fail-on-captive", "fail-on-new-errors", "fail-on-warning", "no-public-api-entry-points",
    "force", "help", "allow-plugins", "summary-only", "schema", "no-cache",
    "refresh", "elevate-warnings", "include-registrations", "only-captive",
    "only-unregistered", "strict", "include-degraded", "include-generated"
};

var valueFlags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "format", "output", "scope", "filter", "max-nodes", "by", "top",
    "rules", "preset", "root-namespace", "categories", "analyzer-categories",
    "tfm", "target-framework", "backend", "plugins", "baseline",
    "max-rows", "depth", "public-api-entry-points", "profile",
    "min-severity", "lifetime", "project", "service-type",
    "exclude-registrations", "commands", "file"
};

var parsingOptions = true;
for (var i = 0; i < args.Length; i++)
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

    if (parsingOptions && (arg is "-v" or "--version"))
    {
        var ver = typeof(ArchitectureAnalyzerService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
            ?? typeof(ArchitectureAnalyzerService).Assembly.GetName().Version?.ToString(3)
            ?? "0.12.0";
        Console.WriteLine($"ArchLens {ver}");
        return 0;
    }

    if (parsingOptions && arg.StartsWith("--", StringComparison.Ordinal))
    {
        // Allow SQL line comments (e.g. "-- comment\nSELECT ...") as the positional query string for `query`
        if (command == "query" && positional.Count >= 1 && (arg.StartsWith("-- ", StringComparison.Ordinal) || arg.Contains('\n') || arg.Contains(' ')))
        {
            positional.Add(arg);
            continue;
        }

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
                var nextVal = args[i + 1];
                if (nextVal.StartsWith("--", StringComparison.Ordinal) &&
                    nextVal.Length > 2 &&
                    (booleanFlags.Contains(nextVal[2..]) || valueFlags.Contains(nextVal[2..])))
                {
                    Console.Error.WriteLine($"Option '--{key}' requires a value (got flag '{nextVal}').");
                    return 2;
                }
                options[key] = args[++i];
            }
            else
            {
                Console.Error.WriteLine($"Option '--{key}' requires a value.");
                return 2;
            }
            continue;
        }

        Console.Error.WriteLine($"Unknown option '{arg}'. Run 'archlens --help' to inspect valid options.");
        return 2;
    }

    if (command is null)
    {
        command = arg.ToLowerInvariant();
    }
    else
    {
        positional.Add(arg);
    }
}

if (string.IsNullOrWhiteSpace(command) || command is "help")
{
    PrintHelp();
    return 0;
}

if (command is "version")
{
    var ver = typeof(ArchitectureAnalyzerService).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(ArchitectureAnalyzerService).Assembly.GetName().Version?.ToString(3)
        ?? "0.12.0";
    Console.WriteLine($"ArchLens {ver}");
    return 0;
}

if (!knownCommands.Contains(command))
{
    Console.Error.WriteLine($"Unknown command '{command}'.");
    PrintHelp();
    return 2;
}

var maxPositionals = command switch
{
    "presets" => 0,
    "inspect" or "impact" or "query" => 2,
    _ => 1
};
if (positional.Count > maxPositionals)
{
    Console.Error.WriteLine($"Unexpected extra argument '{positional[maxPositionals]}' for command '{command}'.");
    Console.Error.WriteLine("Hint: Run 'archlens --help' to inspect valid command usage.");
    return 2;
}

// Validate integer/boolean/enum option values eagerly before performing expensive extraction
try
{
    ValidateCliOptions(command, options);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine("Hint: Run 'archlens --help' to inspect valid options and syntax.");
    return 2;
}

// Validate required positional arguments BEFORE performing expensive extraction or loading plugins
if (command == "query" && !options.ContainsKey("schema") && positional.Count < 2)
{
    Console.Error.WriteLine("Usage: archlens query <path> \"<SQL>\" [--max-rows 100] [--include-methods] [--json]");
    Console.Error.WriteLine("       archlens query --schema [--json]");
    return 2;
}
if (command == "inspect" && positional.Count < 2)
{
    Console.Error.WriteLine("Usage: archlens inspect <path> <symbol> [--include-methods] [--json]");
    return 2;
}
if (command == "impact" && positional.Count < 2)
{
    Console.Error.WriteLine("Usage: archlens impact <path> <symbol> [--depth 4] [--exclude-registrations true|false] [--strict] [--include-methods] [--json]");
    return 2;
}
if (command == "batch" && !options.ContainsKey("commands") && !options.ContainsKey("file"))
{
    Console.Error.WriteLine("Usage: archlens batch <path> --commands \"overview --summary-only;hotspots --top 5;check\" [--json]");
    return 2;
}

var format = ResolveFormat(options);
var jsonOutput = format == "json";
var sarifOutput = format == "sarif";
var outputFile = options.GetValueOrDefault("output");
var includeTests = options.ContainsKey("include-tests");
var includeGenerated = options.ContainsKey("include-generated");
var includeDegraded = options.ContainsKey("include-degraded");
var summaryOnly = options.ContainsKey("summary-only");
var noCache = options.ContainsKey("no-cache");
var forceRefresh = options.ContainsKey("refresh");
var elevateWarnings = options.ContainsKey("elevate-warnings");
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
using var persistentCache = noCache ? null : new SqliteGraphCache();
using var sessionManager = new WorkspaceSessionManager([roslynExtractor, metadataExtractor], persistentCache);
var analyzer = new ArchitectureAnalyzerService();
var yamlProvider = new YamlRuleSetProvider();

IReadOnlyList<IArchitectureRule>? preloadedRules = null;
string? preloadedRulesSource = null;
string? effectiveRootNamespace = null;
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
    if (command == "query" && options.ContainsKey("schema"))
    {
        if (jsonOutput)
        {
            WriteOutput(JsonSerializer.Serialize(new
            {
                tables = GraphSqlQueryEngine.GetCompactSchema(),
                note = "PRAGMA is disabled in read-only mode. Query 'SELECT * FROM schema_info' or use the tables/columns listed above."
            }, jsonOpts), outputFile);
        }
        else
        {
            WriteOutput(GraphSqlQueryEngine.SchemaDocumentation, outputFile);
        }
        return 0;
    }

    if (command == "presets")
    {
        if (jsonOutput)
        {
            WriteOutput(JsonSerializer.Serialize(ArchitecturePresets.All, jsonOpts), outputFile);
        }
        else
        {
            WriteTextOutput(w =>
            {
                w.WriteLine("Available Architecture Presets:");
                foreach (var p in ArchitecturePresets.All)
                {
                    w.WriteLine($"  - {p.Id} ({p.Name}) [aliases: {string.Join(", ", p.Aliases)}]");
                    w.WriteLine($"      {p.Description}");
                }
            }, outputFile);
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
            outPath = ResolveOutputFilePath(outputFile, ".archlens.yaml");
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
            return 2;
        }
        if (string.IsNullOrWhiteSpace(outputFile) && !options.ContainsKey("force"))
        {
            var altYmlPath = Path.Combine(Path.GetDirectoryName(outPath)!, ".archlens.yml");
            if (File.Exists(altYmlPath))
            {
                Console.Error.WriteLine($"Config file already exists: '{altYmlPath}'. Pass --force to create '{outPath}' anyway or --output to choose a different path.");
                return 2;
            }
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

    // Validate baseline file BEFORE extraction for `diff` and explicit `report --baseline`
    if (command == "diff")
    {
        resolvedBaselinePath = options.GetValueOrDefault("baseline") ?? BaselineStore.DiscoverBaselineFile(targetPath);
        if (!string.IsNullOrWhiteSpace(resolvedBaselinePath) && Directory.Exists(resolvedBaselinePath))
        {
            Console.Error.WriteLine($"Baseline path '{Path.GetFullPath(resolvedBaselinePath)}' is a directory. Pass a .archlens-baseline.json file path.");
            return 2;
        }
        if (string.IsNullOrWhiteSpace(resolvedBaselinePath) || !File.Exists(resolvedBaselinePath))
        {
            Console.Error.WriteLine($"No baseline file specified or found (expected '{BaselineStore.DefaultFileName}'). Run 'archlens baseline <path>' first.");
            return 2;
        }
    }
    if (command == "report" && options.TryGetValue("baseline", out var explicitReportBaseline))
    {
        var fullReportBaseline = Path.GetFullPath(explicitReportBaseline);
        if (Directory.Exists(fullReportBaseline))
        {
            Console.Error.WriteLine($"Baseline path '{fullReportBaseline}' is a directory. Pass a .archlens-baseline.json file path.");
            return 2;
        }
        if (!File.Exists(fullReportBaseline))
        {
            Console.Error.WriteLine($"Baseline file not found: '{fullReportBaseline}'. Run 'archlens baseline <path>' first or omit --baseline.");
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
    if (extractor.Name == "IL")
    {
        if (command == "di")
        {
            Console.Error.WriteLine("Error: 'di' analysis requires Roslyn source extraction (--backend roslyn on a .slnx/.sln/.csproj/source directory); IL backend does not extract IServiceCollection registrations.");
            return 2;
        }
        if (command == "analyzers")
        {
            Console.Error.WriteLine("Error: 'analyzers' requires Roslyn source extraction (--backend roslyn on a .slnx/.sln/.csproj/source directory); IL backend does not run Roslyn DiagnosticAnalyzers.");
            return 2;
        }
        if (command == "report")
        {
            Console.Error.WriteLine("Error: 'report' requires Roslyn source extraction (--backend roslyn on a .slnx/.sln/.csproj/source directory); IL backend does not extract DI registrations or full source metrics for HTML reports.");
            return 2;
        }
    }
    var presetOption = options.GetValueOrDefault("preset");
    var rootNamespaceOption = options.GetValueOrDefault("root-namespace");

    if (command is "check" or "diff" or "report" or "batch")
    {
        var loadedRules = new List<IArchitectureRule>();
        var ruleSources = new List<string>();
        string? yamlRootNamespace = null;

        if (options.TryGetValue("preset", out var rawPresetOption))
        {
            var presetTokens = rawPresetOption.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (presetTokens.Length == 0)
            {
                Console.Error.WriteLine("Error: '--preset' value cannot be empty. Supported presets: clean-architecture, vertical-slice, modular-monolith, ddd.");
                return 2;
            }
            var seenPresets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rawPreset in presetTokens)
            {
                var canonicalPreset = ArchitecturePresets.TryResolvePresetId(rawPreset, out var resolvedPreset) ? resolvedPreset : rawPreset;
                if (seenPresets.Add(canonicalPreset))
                {
                    loadedRules.AddRange(ArchitecturePresets.CreateRules(rawPreset, rootNamespaceOption));
                    ruleSources.Add($"preset:{canonicalPreset}");
                }
            }
        }

        if (options.TryGetValue("rules", out var explicitRulesOpt))
        {
            if (string.IsNullOrWhiteSpace(explicitRulesOpt))
            {
                Console.Error.WriteLine("Error: '--rules' path cannot be empty. Pass an existing .archlens.yaml/.yml file or omit '--rules' to auto-discover.");
                return 2;
            }
            var fullRules = Path.GetFullPath(explicitRulesOpt);
            if (!File.Exists(fullRules))
            {
                Console.Error.WriteLine($"Rules file not found: '{fullRules}'.");
                Console.Error.WriteLine("Hint: Pass an existing .archlens.yaml/.yml file to --rules, or omit --rules to auto-discover.");
                return 2;
            }
            var loadedFromExplicit = yamlProvider.LoadRules(fullRules, rootNamespaceOption);
            if (loadedFromExplicit.Count == 0 && loadedRules.Count == 0)
            {
                Console.Error.WriteLine($"Error: Rules file '{fullRules}' defines 0 rules and no preset. Add at least one rule or 'preset:' entry, or omit --rules to use built-in defaults.");
                return 2;
            }
            loadedRules.AddRange(loadedFromExplicit);
            yamlRootNamespace = yamlProvider.LoadRootNamespace(fullRules);
            ruleSources.Add(fullRules);
        }
        else if (loadedRules.Count == 0)
        {
            var discovered = YamlRuleSetProvider.DiscoverConfigFile(targetPath);
            if (discovered is not null)
            {
                var loadedDiscovered = yamlProvider.LoadRules(discovered, rootNamespaceOption);
                yamlRootNamespace = yamlProvider.LoadRootNamespace(discovered);
                if (loadedDiscovered.Count > 0)
                {
                    loadedRules.AddRange(loadedDiscovered);
                    ruleSources.Add(discovered);
                }
                else
                {
                    ruleSources.Add($"built-in ('{discovered}' defines 0 rules)");
                }
            }
        }

        effectiveRootNamespace = !string.IsNullOrWhiteSpace(rootNamespaceOption)
            ? rootNamespaceOption
            : yamlRootNamespace;

        if (loadedRules.Count == 0)
        {
            loadedRules.Add(new NoCyclesRule("BUILTIN-001", "No project cycles", "Projects must form a DAG.", RuleSeverity.Error, NodeKind.Project));
            loadedRules.Add(new NoCyclesRule("BUILTIN-002", "No namespace cycles", "Namespaces must form a DAG.", RuleSeverity.Warning, NodeKind.Namespace));
            if (ruleSources.Count == 0)
            {
                ruleSources.Add("built-in");
            }
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

    if (command == "batch")
    {
        string rawBatch;
        if (options.TryGetValue("commands", out var cmdInline))
        {
            rawBatch = cmdInline;
        }
        else
        {
            var batchFilePath = Path.GetFullPath(options["file"]);
            if (!File.Exists(batchFilePath))
            {
                Console.Error.WriteLine($"Error: Batch commands file not found: '{batchFilePath}'.");
                return 2;
            }
            rawBatch = File.ReadAllText(batchFilePath);
        }

        var preSubCommands = SplitBatchCommands(rawBatch);
        if (preSubCommands.Count == 0)
        {
            Console.Error.WriteLine("Error: Batch '--commands' / '--file' contains no subcommands to execute.");
            return 2;
        }

        foreach (var sub in preSubCommands)
        {
            var subParts = SplitBatchArgs(sub);
            if (subParts.Length == 0) continue;
            var scopeIdx = Array.FindIndex(subParts, p => p.Equals("--scope", StringComparison.OrdinalIgnoreCase));
            if ((scopeIdx >= 0 && scopeIdx + 1 < subParts.Length && subParts[scopeIdx + 1].Trim().Equals("method", StringComparison.OrdinalIgnoreCase)) ||
                Array.FindIndex(subParts, p => p.Equals("--include-methods", StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                includeMethods = true;
            }
            if (subParts[0].Equals("analyzers", StringComparison.OrdinalIgnoreCase) ||
                Array.FindIndex(subParts, p => p.Equals("--include-analyzers", StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                includeAnalyzers = true;
            }
            // Inspect YAML files passed via subcommand `check --rules <file>` for method-scope rules
            if (subParts[0].Equals("check", StringComparison.OrdinalIgnoreCase))
            {
                var rulesIdx = Array.FindIndex(subParts, p => p.Equals("--rules", StringComparison.OrdinalIgnoreCase));
                if (rulesIdx >= 0 && rulesIdx + 1 < subParts.Length)
                {
                    var fp = Path.GetFullPath(subParts[rulesIdx + 1]);
                    if (File.Exists(fp))
                    {
                        try
                        {
                            var subLoaded = yamlProvider.LoadRules(fp);
                            if (subLoaded.OfType<NoCyclesRule>().Any(r => r.Scope == NodeKind.Method) ||
                                subLoaded.OfType<NoDeadCodeRule>().Any(r => r.Scope == NodeKind.Method) ||
                                subLoaded.OfType<MaxMetricRule>().Any(r => r.Scope == NodeKind.Method))
                            {
                                includeMethods = true;
                            }
                        }
                        catch
                        {
                            // Let subcommand execution surface the exact YAML error cleanly
                        }
                    }
                }
            }
        }
    }

    if (includeAnalyzers && extractor.Name == "IL")
    {
        Console.Error.WriteLine($"Error: '{command}' with '--include-analyzers' requires Roslyn source extraction (--backend roslyn on a .slnx/.sln/.csproj/source directory); IL backend does not run Roslyn DiagnosticAnalyzers.");
        return 2;
    }

    var extractionOptions = new ExtractionOptions(
        IncludeTestProjects: includeTests,
        IncludeGeneratedCode: includeGenerated,
        IncludeMethods: includeMethods,
        TargetFramework: targetFramework,
        IncludeAnalyzerDiagnostics: includeAnalyzers,
        AnalyzerCategories: analyzerCategories,
        Backend: backend,
        ElevateAnalyzerSeverity: elevateWarnings);
    var snapshot = await sessionManager.GetOrLoadAsync(targetPath, forceRefresh: forceRefresh, extractionOptions);

    // Fail loudly (exit 2) when extraction produced an empty degraded graph so CI cannot mistake a load failure for a clean codebase.
    if (snapshot.Graph.LoadStatus is { Degraded: true, ProjectsLoaded: 0 } emptyLoadStatus &&
        !snapshot.Graph.GetNodes(NodeKind.Type).Any())
    {
        Console.Error.WriteLine($"Error: Extraction produced an empty degraded graph (backend={emptyLoadStatus.Backend}, projectsLoaded=0/{emptyLoadStatus.ProjectsExpected}).");
        if (!string.IsNullOrWhiteSpace(emptyLoadStatus.Note))
        {
            Console.Error.WriteLine($"Details: {emptyLoadStatus.Note}");
        }
        return 2;
    }

    // Only load plugins for commands that consume them AND only when explicitly opted in (--allow-plugins / --plugins / ARCHLENS_ALLOW_PLUGINS=1)
    var commandUsesPlugins = command is "hotspots" or "check" or "dead-code" or "diff" or "query" or "report" or "inspect" or "batch";
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
                object payload = summaryOnly
                    ? new
                    {
                        report.SnapshotId,
                        report.TargetPath,
                        report.CapturedAt,
                        report.LoadStatus,
                        report.ProjectCount,
                        report.NamespaceCount,
                        report.TypeCount,
                        report.MethodCount,
                        report.MethodsIncluded,
                        report.DependencyEdgeCount,
                        report.ProjectCycleCount,
                        report.NamespaceCycleCount,
                        report.NamespaceCycleCountRaw,
                        report.SuppressedNamespaceCycles
                    }
                    : report;
                WriteOutput(JsonSerializer.Serialize(payload, jsonOpts), outputFile);
            }
            else
            {
                WriteTextOutput(w =>
                {
                    w.WriteLine($"ArchLens Overview: {report.TargetPath} (snapshot: {report.SnapshotId})");
                    var methodPart = report.MethodsIncluded ? $" | Methods: {report.MethodCount ?? 0}" : "";
                    w.WriteLine($"Projects: {report.ProjectCount} | Namespaces: {report.NamespaceCount} | Types: {report.TypeCount}{methodPart} | Edges: {report.DependencyEdgeCount}");
                    var suppressedPart = report.SuppressedNamespaceCycles is { Count: > 0 }
                        ? $" ({report.NamespaceCycleCountRaw} raw; suppressed: {string.Join(", ", report.SuppressedNamespaceCycles.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))})"
                        : "";
                    w.WriteLine($"Cycles: {report.ProjectCycleCount} project, {report.NamespaceCycleCount} namespace{suppressedPart}");
                    if (report.LoadStatus is { } ls && (ls.Degraded || ls.Approximate || ls.RazorComponentCount > 0))
                    {
                        var razorPart = ls.RazorComponentCount > 0 ? $", razorComponents={ls.RazorComponentCount}" : "";
                        w.WriteLine($"Load Status: backend={ls.Backend}, projectsLoaded={ls.ProjectsLoaded}/{ls.ProjectsExpected}, degraded={ls.Degraded}, approximate={ls.Approximate}{razorPart}");
                        if (!string.IsNullOrWhiteSpace(ls.Note))
                        {
                            w.WriteLine($"  Note: {ls.Note}");
                        }
                    }
                    if (!summaryOnly)
                    {
                        w.WriteLine();
                        w.WriteLine("Projects:");
                        foreach (var p in report.Projects)
                        {
                            var refs = p.ProjectReferences.Count > 0 ? $" -> [{string.Join(", ", p.ProjectReferences)}]" : "";
                            w.WriteLine($"  - {p.Name} ({p.TargetFramework ?? "?"}): {p.TypeCount} types{refs}");
                        }
                    }
                    if (report.Diagnostics.Count > 0 && (!summaryOnly || report.Diagnostics.Any(d => d.Severity >= ExtractionDiagnosticSeverity.Warning || d.Message.Contains("TargetFramework", StringComparison.OrdinalIgnoreCase))))
                    {
                        w.WriteLine();
                        w.WriteLine($"Workspace Diagnostics ({report.Diagnostics.Count}):");
                        foreach (var d in report.Diagnostics.Take(5))
                        {
                            w.WriteLine($"  [{d.Severity}] {d.Message}");
                        }
                        if (report.Diagnostics.Count > 5)
                        {
                            w.WriteLine($"  ... ({report.Diagnostics.Count - 5} more diagnostic(s) omitted; showing 5 of {report.Diagnostics.Count}; pass --json for full list)");
                        }
                    }
                }, outputFile);
            }
            return 0;
        }

        case "graph":
        {
            var scopeStr = options.GetValueOrDefault("scope", "namespace").Trim();
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
                object payload = snapshot.Graph.LoadStatus is { } grLs && (grLs.Degraded || grLs.Approximate)
                    ? new
                    {
                        report.Scope,
                        report.TotalNodesInScope,
                        report.ReturnedNodeCount,
                        report.Truncated,
                        report.TotalEdgeCount,
                        report.ReturnedEdgeCount,
                        report.EdgesTruncated,
                        report.TotalCycleCount,
                        report.SuppressedCycles,
                        loadStatus = grLs,
                        report.Cycles,
                        report.Nodes,
                        report.Edges
                    }
                    : report;
                WriteOutput(JsonSerializer.Serialize(payload, jsonOpts), outputFile);
            }
            else
            {
                WriteTextOutput(w =>
                {
                    var edgesPart = report.EdgesTruncated == true || (report.TotalEdgeCount.HasValue && report.ReturnedEdgeCount < report.TotalEdgeCount.Value)
                        ? $"{report.ReturnedEdgeCount ?? report.Edges.Count}/{report.TotalEdgeCount ?? report.Edges.Count} edges (truncated)"
                        : $"{report.Edges.Count} edges";
                    w.WriteLine($"Dependency Graph ({report.Scope}): {report.ReturnedNodeCount}/{report.TotalNodesInScope} nodes, {edgesPart}, {report.Cycles.Count} cycles");
                    WriteLoadStatusTextNotice(w, snapshot.Graph.LoadStatus);
                    if (report.SuppressedCycles is { Count: > 0 })
                    {
                        var parts = report.SuppressedCycles
                            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                            .Select(kv => $"{kv.Key}={kv.Value}");
                        w.WriteLine($"Hidden cycles (suppressed by default): {string.Join(", ", parts)}");
                    }
                    if (report.Cycles.Count > 0)
                    {
                        w.WriteLine("\nCycles:");
                        foreach (var c in report.Cycles)
                        {
                            var display = c.CyclePath is { Count: >= 2 } ? string.Join(" -> ", c.CyclePath) : string.Join(" -> ", c.Nodes);
                            if (c.Nodes.Count > 2)
                            {
                                w.WriteLine($"  ! ({c.Nodes.Count} nodes, {c.InternalEdgeCount} edges) {display} [SCC: {string.Join(", ", c.Nodes)}]");
                            }
                            else
                            {
                                w.WriteLine($"  ! {display}");
                            }
                        }
                    }

                    w.WriteLine("\nEdges:");
                    foreach (var e in report.Edges)
                    {
                        w.WriteLine($"  {e.From} -> {e.To}");
                    }
                }, outputFile);
            }
            return 0;
        }

        case "hotspots":
        {
            var sortBy = options.GetValueOrDefault("by", "complexity").Trim();
            var scope = options.GetValueOrDefault("scope", "type").Trim().ToLowerInvariant() switch
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
                object payload = snapshot.Graph.LoadStatus is { } hsLs && (hsLs.Degraded || hsLs.Approximate)
                    ? new
                    {
                        report.Scope,
                        report.SortBy,
                        report.TotalEvaluated,
                        loadStatus = hsLs,
                        report.Items
                    }
                    : report;
                WriteOutput(JsonSerializer.Serialize(payload, jsonOpts), outputFile);
            }
            else
            {
                WriteTextOutput(w =>
                {
                    w.WriteLine($"Top {report.Items.Count} Hotspots (scope={report.Scope}, sortBy={report.SortBy}):");
                    if (snapshot.Graph.LoadStatus is { Approximate: true } approxLs)
                    {
                        w.WriteLine($"Note: {approxLs.Note}");
                    }
                    var sortKey = sortBy.Trim().ToLowerInvariant();
                    foreach (var item in report.Items)
                    {
                        var loc = item.Location is not null ? $" ({item.Location.ToShortString()})" : "";
                        var extraMetricPart = sortKey switch
                        {
                            "total_complexity" or "total_cyclomatic" => $"totalCC={item.CyclomaticComplexity}, ",
                            "total_cognitive" => $"totalCog={item.CognitiveComplexity}, ",
                            "distance" when item.DistanceFromMainSequence.HasValue => $"D={item.DistanceFromMainSequence.Value.ToString("0.00", CultureInfo.InvariantCulture)}, A={(item.Abstractness ?? 0).ToString("0.00", CultureInfo.InvariantCulture)}, ",
                            "god_class" or "godclass" => $"godScore={ArchitectureAnalyzerService.ComputeGodClassScore(item).ToString("0.00", CultureInfo.InvariantCulture)}, totalCC={item.CyclomaticComplexity}, ",
                            _ => string.Empty
                        };
                        var customPart = item.CustomMetrics is { Count: > 0 }
                            ? $", {string.Join(", ", item.CustomMetrics.Select(kv => $"{kv.Key}={kv.Value.ToString("0.##", CultureInfo.InvariantCulture)}"))}"
                            : "";
                        w.WriteLine($"  - {item.Name}: {extraMetricPart}maxCC={item.MaxMethodComplexity}, maxCog={item.MaxMethodCognitiveComplexity}, LCOM4={item.Lcom4}, Ce={item.EfferentCoupling}, Ca={item.AfferentCoupling}, I={item.Instability.ToString("0.00", CultureInfo.InvariantCulture)}, LOC={item.LineCount}, ctor={item.ConstructorParameterCount}{customPart}{loc}");
                    }
                }, outputFile);
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
                extraEntryPointDetectors: extraDetectors,
                excludeDegradedProjects: !includeDegraded,
                rootNamespace: effectiveRootNamespace);

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
                    snapshotId = snapshot.SnapshotId,
                    capturedAt = snapshot.CapturedAt,
                    rulesSource,
                    loadStatus = snapshot.Graph.LoadStatus,
                    loadedPlugins = plugins.LoadedPluginFiles.Count > 0 ? plugins.LoadedPluginFiles : null,
                    pluginDiagnostics = plugins.Diagnostics.Count > 0 ? plugins.Diagnostics : null,
                    report
                }, jsonOpts), outputFile);
            }
            else
            {
                WriteTextOutput(w =>
                {
                    w.WriteLine($"Rules Check ({rulesSource}) [snapshot: {snapshot.SnapshotId}]: {report.RulesEvaluated} rules | {report.ErrorCount} errors, {report.WarningCount} warnings, {report.InfoCount} info");
                    if (snapshot.Graph.LoadStatus is { Degraded: true } degradedLs)
                    {
                        w.WriteLine($"  Load Status: backend={degradedLs.Backend}, projectsLoaded={degradedLs.ProjectsLoaded}/{degradedLs.ProjectsExpected}, degraded=True");
                        if (!string.IsNullOrWhiteSpace(degradedLs.Note))
                        {
                            w.WriteLine($"  Warning: {degradedLs.Note}");
                        }
                    }
                    else if (snapshot.Graph.LoadStatus is { Approximate: true } approxLs && !string.IsNullOrWhiteSpace(approxLs.Note))
                    {
                        w.WriteLine($"  Note: {approxLs.Note}");
                    }
                    if (!string.IsNullOrWhiteSpace(report.Note))
                    {
                        w.WriteLine($"  Warning: {report.Note}");
                    }
                    if (report.SkippedDegradedProjects is { Count: > 0 })
                    {
                        w.WriteLine($"  Skipped degraded projects (pass --include-degraded to include): {string.Join(", ", report.SkippedDegradedProjects)}");
                    }
                    if (report.SuppressedCycles is { Count: > 0 })
                    {
                        var parts = report.SuppressedCycles
                            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                            .Select(kv => $"{kv.Key}={kv.Value}");
                        w.WriteLine($"  Hidden cycles (filtered by rule options): {string.Join(", ", parts)}");
                    }
                    if (report.AcknowledgedDeadCode is { Count: > 0 })
                    {
                        w.WriteLine($"  Acknowledged dead-code exclusions ({report.AcknowledgedDeadCode.Count}):");
                        foreach (var ack in report.AcknowledgedDeadCode)
                        {
                            var reasonPart = !string.IsNullOrWhiteSpace(ack.Reason) ? $" — {ack.Reason}" : "";
                            w.WriteLine($"    - {ack.Symbol}{reasonPart}");
                        }
                    }
                    if (!summaryOnly)
                    {
                        foreach (var v in report.Violations)
                        {
                            var loc = v.Location is not null ? $" at {v.Location.ToShortString()}" : "";
                            w.WriteLine($"  [{v.Severity}] {v.RuleId}: {v.Message}{loc}");
                        }
                    }
                }, outputFile);
            }
            if (report.VacuousConfiguration || report.ConfigurationError)
            {
                return 2;
            }
            if (snapshot.Graph.LoadStatus is { Degraded: true } degradedCheck && !includeDegraded)
            {
                if (string.Equals(degradedCheck.Backend, "il", StringComparison.OrdinalIgnoreCase) &&
                    degradedCheck.ProjectsLoaded < degradedCheck.ProjectsExpected)
                {
                    Console.Error.WriteLine($"Error: Partially built IL solution ({degradedCheck.ProjectsLoaded}/{degradedCheck.ProjectsExpected} projects loaded; missing: {string.Join(", ", degradedCheck.DegradedProjects ?? [])}). Build all projects, pass --backend roslyn, or pass --include-degraded.");
                    return 2;
                }

                if (!string.Equals(degradedCheck.Backend, "il", StringComparison.OrdinalIgnoreCase))
                {
                    var degradedNames = degradedCheck.DegradedProjects is { Count: > 0 }
                        ? $"; degraded: {string.Join(", ", degradedCheck.DegradedProjects)}"
                        : "";
                    Console.Error.WriteLine($"Error: Degraded Roslyn workspace ({degradedCheck.ProjectsLoaded}/{degradedCheck.ProjectsExpected} projects loaded cleanly{degradedNames}). Fix restore/compile errors or pass --include-degraded to evaluate rules against partial data.");
                    return 2;
                }
            }
            if (report.ErrorCount > 0 || (options.ContainsKey("fail-on-warning") && report.WarningCount > 0))
            {
                return 1;
            }
            return 0;
        }

        case "dead-code":
        {
            var scope = string.Equals(options.GetValueOrDefault("scope", "type"), "method", StringComparison.OrdinalIgnoreCase)
                ? NodeKind.Method
                : NodeKind.Type;
            var explicitNoPublic = options.ContainsKey("no-public-api-entry-points");
            var explicitPublicFlag = options.TryGetValue("public-api-entry-points", out var pubStr)
                ? !string.Equals(pubStr, "false", StringComparison.OrdinalIgnoreCase)
                : (bool?)null;
            var profile = options.GetValueOrDefault("profile");
            var effectiveProfile = !string.IsNullOrWhiteSpace(profile)
                ? profile
                : (explicitNoPublic || explicitPublicFlag.HasValue ? null : "auto");
            var treatPublicApi = !explicitNoPublic && (explicitPublicFlag ?? true);
            var filter = options.GetValueOrDefault("filter");
            var top = int.TryParse(options.GetValueOrDefault("top"), out var t) ? t : 100;
            IReadOnlyList<DeadCodeAcknowledgement>? acknowledged = null;
            var discoveredConfig = options.GetValueOrDefault("rules")
                ?? YamlRuleSetProvider.DiscoverConfigFile(targetPath)
                ?? YamlRuleSetProvider.DiscoverConfigFile(snapshot.Graph.RootPath);
            if (!string.IsNullOrWhiteSpace(discoveredConfig) && yamlProvider.CanLoad(discoveredConfig))
            {
                var loadedAcks = yamlProvider.LoadDeadCodeAcknowledgements(discoveredConfig);
                if (loadedAcks.Count > 0) acknowledged = loadedAcks;
            }
            var explicitPublicOverride = explicitNoPublic ? false : explicitPublicFlag;
            var report = analyzer.AnalyzeDeadCode(snapshot.Graph, scope, treatPublicApi, filter, top,
                plugins.EntryPointDetectors.Count > 0 ? plugins.EntryPointDetectors : null,
                testsExcluded: !includeTests,
                profile: effectiveProfile,
                acknowledged: acknowledged,
                treatPublicApiOverride: explicitPublicOverride);

            if (jsonOutput)
            {
                object payload = snapshot.Graph.LoadStatus is { } dcLs && (dcLs.Degraded || dcLs.Approximate)
                    ? new
                    {
                        report.Scope,
                        report.TotalEvaluated,
                        report.EntryPointCount,
                        report.UnreferencedCount,
                        report.Truncated,
                        report.ActiveDetectors,
                        report.ReferencesFromTestsExcluded,
                        report.ExcludedTestProjectCount,
                        report.Note,
                        report.Profile,
                        report.AcknowledgedCount,
                        report.Acknowledged,
                        loadStatus = dcLs,
                        report.Candidates
                    }
                    : report;
                WriteOutput(JsonSerializer.Serialize(payload, jsonOpts), outputFile);
            }
            else
            {
                WriteTextOutput(w =>
                {
                    var ackPart = report.AcknowledgedCount is > 0 ? $", {report.AcknowledgedCount} acknowledged" : "";
                    w.WriteLine($"Dead Code ({report.Scope}, profile={report.Profile}): {report.UnreferencedCount} candidates{ackPart} / {report.TotalEvaluated} evaluated ({report.EntryPointCount} entry points)");
                    w.WriteLine($"Active detectors: {string.Join(", ", report.ActiveDetectors)}");
                    WriteLoadStatusTextNotice(w, snapshot.Graph.LoadStatus);
                    if (!string.IsNullOrWhiteSpace(report.Note))
                    {
                        w.WriteLine($"Note: {report.Note}");
                    }
                    foreach (var c in report.Candidates)
                    {
                        var loc = c.Location is not null ? $" at {c.Location.ToShortString()}" : "";
                        w.WriteLine($"  - [{c.Accessibility}] {c.FullName} — {c.Reason}{loc}");
                    }
                    if (report.Truncated || report.UnreferencedCount > report.Candidates.Count)
                    {
                        w.WriteLine($"  ... ({report.UnreferencedCount - report.Candidates.Count} more candidate(s) truncated; showing {report.Candidates.Count} of {report.UnreferencedCount}; pass --top to adjust)");
                    }
                }, outputFile);
            }
            return 0;
        }

        case "di":
        {
            if (extractor.Name == "IL")
            {
                Console.Error.WriteLine("Error: 'di' analysis requires Roslyn source extraction (--backend roslyn on a .slnx/.sln/.csproj/source directory); IL backend does not extract IServiceCollection registrations.");
                return 2;
            }
            var report = analyzer.AnalyzeDi(snapshot.Graph);
            var lifetimeFilter = options.GetValueOrDefault("lifetime");
            var projectFilter = options.GetValueOrDefault("project");
            var serviceFilter = options.GetValueOrDefault("service-type");
            var onlyCaptive = options.ContainsKey("only-captive");
            var onlyUnregistered = options.ContainsKey("only-unregistered");
            var includeRegs = options.ContainsKey("include-registrations");

            bool MatchesDiProject(string? typeId)
            {
                if (string.IsNullOrWhiteSpace(projectFilter)) return true;
                var proj = typeId is not null ? snapshot.Graph.FindNode(typeId)?.ProjectName : null;
                return proj is not null && (proj.Equals(projectFilter, StringComparison.OrdinalIgnoreCase) || GlobMatcher.IsMatch(proj, projectFilter));
            }

            bool MatchesDiService(string? typeId)
            {
                if (string.IsNullOrWhiteSpace(serviceFilter)) return true;
                return typeId is not null &&
                       (typeId.Contains(serviceFilter, StringComparison.OrdinalIgnoreCase) ||
                        GlobMatcher.IsMatch(typeId, serviceFilter));
            }

            ServiceLifetimeKind? parsedLifetimeFilter = null;
            if (!string.IsNullOrWhiteSpace(lifetimeFilter) && Enum.TryParse<ServiceLifetimeKind>(lifetimeFilter, true, out var lt))
            {
                parsedLifetimeFilter = lt;
            }

            var filteredRegs = report.Registrations.AsEnumerable();
            if (parsedLifetimeFilter.HasValue)
            {
                filteredRegs = filteredRegs.Where(r => r.Lifetime == parsedLifetimeFilter.Value);
            }
            if (!string.IsNullOrWhiteSpace(projectFilter))
            {
                filteredRegs = filteredRegs.Where(r =>
                    (r.ProjectName is not null && (r.ProjectName.Equals(projectFilter, StringComparison.OrdinalIgnoreCase) || GlobMatcher.IsMatch(r.ProjectName, projectFilter))) ||
                    MatchesDiProject(r.ServiceTypeId) ||
                    MatchesDiProject(r.ImplementationTypeId));
            }
            if (!string.IsNullOrWhiteSpace(serviceFilter))
            {
                filteredRegs = filteredRegs.Where(r =>
                    MatchesDiService(r.ServiceTypeId) ||
                    MatchesDiService(r.ImplementationTypeId));
            }
            var matchedRegs = filteredRegs.ToList();

            var matchedCaptives = report.CaptiveDependencies
                .Where(c => string.IsNullOrWhiteSpace(projectFilter) ||
                            MatchesDiProject(c.ConsumerTypeId) ||
                            MatchesDiProject(c.ResolvedImplementationTypeId))
                .Where(c => string.IsNullOrWhiteSpace(serviceFilter) ||
                            MatchesDiService(c.ConsumerTypeId) ||
                            MatchesDiService(c.DependencyTypeId) ||
                            MatchesDiService(c.ResolvedImplementationTypeId))
                .Where(c => !parsedLifetimeFilter.HasValue ||
                            c.ConsumerLifetime == parsedLifetimeFilter.Value ||
                            c.DependencyLifetime == parsedLifetimeFilter.Value ||
                            (parsedLifetimeFilter.Value == ServiceLifetimeKind.Transient &&
                             c.DependencyPath.Contains("(Transient)", StringComparison.Ordinal)))
                .ToList();

            var matchedSingles = report.SingleImplementationInterfaces
                .Where(s => !onlyUnregistered || !s.IsDiRegistered)
                .Where(s => string.IsNullOrWhiteSpace(projectFilter) ||
                            MatchesDiProject(s.InterfaceTypeId) ||
                            MatchesDiProject(s.ImplementationTypeId))
                .Where(s => string.IsNullOrWhiteSpace(serviceFilter) ||
                            MatchesDiService(s.InterfaceTypeId) ||
                            MatchesDiService(s.ImplementationTypeId))
                .ToList();

            var hasDiFilters = parsedLifetimeFilter.HasValue || !string.IsNullOrWhiteSpace(projectFilter) || !string.IsNullOrWhiteSpace(serviceFilter);
            var hasSingleFilters = onlyUnregistered || !string.IsNullOrWhiteSpace(serviceFilter) || !string.IsNullOrWhiteSpace(projectFilter);
            if (jsonOutput)
            {
                WriteOutput(JsonSerializer.Serialize(new
                {
                    registrationCount = report.RegistrationCount,
                    matchedRegistrationCount = hasDiFilters ? (int?)matchedRegs.Count : null,
                    lifetimeCounts = new
                    {
                        singleton = matchedRegs.Count(r => r.Lifetime == ServiceLifetimeKind.Singleton),
                        scoped = matchedRegs.Count(r => r.Lifetime == ServiceLifetimeKind.Scoped),
                        transient = matchedRegs.Count(r => r.Lifetime == ServiceLifetimeKind.Transient)
                    },
                    captiveDependencyCount = report.CaptiveDependencyCount,
                    matchedCaptiveDependencyCount = hasDiFilters ? (int?)matchedCaptives.Count : null,
                    singleImplementationCount = report.SingleImplementationCount,
                    matchedSingleImplementationCount = hasSingleFilters ? (int?)matchedSingles.Count : null,
                    captiveDependencies = onlyUnregistered && !onlyCaptive ? null : matchedCaptives,
                    singleImplementationInterfaces = onlyCaptive ? null : matchedSingles,
                    registrations = includeRegs && !onlyCaptive ? matchedRegs : null
                }, jsonOpts), outputFile);
            }
            else
            {
                WriteTextOutput(w =>
                {
                    var regCountLabel = hasDiFilters
                        ? $"{matchedRegs.Count}/{report.RegistrationCount}"
                        : $"{report.RegistrationCount}";
                    var captiveCountLabel = hasDiFilters
                        ? $"{matchedCaptives.Count}/{report.CaptiveDependencyCount}"
                        : $"{report.CaptiveDependencyCount}";
                    var singleCountLabel = hasSingleFilters
                        ? $"{matchedSingles.Count}/{report.SingleImplementationCount}"
                        : $"{report.SingleImplementationCount}";
                    w.WriteLine($"DI Analysis: {regCountLabel} registrations (Singleton: {matchedRegs.Count(r => r.Lifetime == ServiceLifetimeKind.Singleton)}, Scoped: {matchedRegs.Count(r => r.Lifetime == ServiceLifetimeKind.Scoped)}, Transient: {matchedRegs.Count(r => r.Lifetime == ServiceLifetimeKind.Transient)}) | {captiveCountLabel} captive | {singleCountLabel} single-impl interfaces");
                    if ((!onlyUnregistered || onlyCaptive) && matchedCaptives.Count > 0)
                    {
                        w.WriteLine("\nCaptive Dependencies:");
                        foreach (var issue in matchedCaptives)
                        {
                            var loc = issue.Location is not null ? $" at {issue.Location.ToShortString()}" : "";
                            w.WriteLine($"  ! {issue.DependencyPath}{loc}");
                        }
                    }

                    if (!onlyCaptive && matchedSingles.Count > 0)
                    {
                        w.WriteLine("\nSingle-Implementation Interfaces:");
                        foreach (var s in matchedSingles.Take(25))
                        {
                            var diTag = s.IsDiRegistered ? "DI" : "non-DI";
                            w.WriteLine($"  - {s.InterfaceTypeId} -> {s.ImplementationTypeId} ({diTag}, {s.ConsumerCount} consumers)");
                        }
                        if (matchedSingles.Count > 25)
                        {
                            w.WriteLine($"  ... ({matchedSingles.Count - 25} more single-implementation interface(s) omitted; showing 25 of {matchedSingles.Count}; pass --json for full list)");
                        }
                    }

                    if (includeRegs && !onlyCaptive && matchedRegs.Count > 0)
                    {
                        w.WriteLine($"\nRegistrations ({matchedRegs.Count}):");
                        foreach (var r in matchedRegs.Take(100))
                        {
                            var keyPart = !string.IsNullOrWhiteSpace(r.ServiceKey) ? $", key={r.ServiceKey}" : "";
                            var loc = r.Location is not null ? $" at {r.Location.ToShortString()}" : "";
                            w.WriteLine($"  - [{r.Lifetime}{keyPart}] {r.ServiceTypeId} -> {r.ImplementationTypeId}{loc}");
                        }
                        if (matchedRegs.Count > 100)
                        {
                            w.WriteLine($"  ... ({matchedRegs.Count - 100} more registration(s) omitted; showing 100 of {matchedRegs.Count}; pass --json for full list)");
                        }
                    }
                }, outputFile);
            }
            return options.ContainsKey("fail-on-captive") && matchedCaptives.Count > 0 ? 1 : 0;
        }

        case "analyzers":
        {
            if (extractor.Name == "IL")
            {
                Console.Error.WriteLine("Error: 'analyzers' requires Roslyn source extraction (--backend roslyn on a .slnx/.sln/.csproj/source directory); IL backend does not run Roslyn DiagnosticAnalyzers.");
                return 2;
            }
            var top = int.TryParse(options.GetValueOrDefault("top"), out var t) ? t : 100;
            var effectiveMax = options.ContainsKey("top") ? top : (sarifOutput ? int.MaxValue : top);
            var minSev = ParseCliSeverity(options.GetValueOrDefault("min-severity") ?? "warning");
            var filter = options.GetValueOrDefault("filter");
            var report = analyzer.GetAnalyzerDiagnostics(
                snapshot.Graph, analyzerCategories, effectiveMax,
                minSeverity: minSev, filterPattern: filter, excludeDegradedProjects: !includeDegraded);
            if (sarifOutput)
            {
                WriteOutput(SarifExporter.Export(analyzerDiagnostics: report.Diagnostics), outputFile);
            }
            else if (jsonOutput)
            {
                WriteOutput(JsonSerializer.Serialize(new
                {
                    snapshotId = snapshot.SnapshotId,
                    capturedAt = snapshot.CapturedAt,
                    loadStatus = snapshot.Graph.LoadStatus,
                    report
                }, jsonOpts), outputFile);
            }
            else
            {
                WriteTextOutput(w =>
                {
                    w.WriteLine($"Roslyn Analyzers [snapshot: {snapshot.SnapshotId}]: {report.TotalDiagnostics} diagnostics ({report.ErrorCount} errors, {report.WarningCount} warnings, {report.InfoCount} info)");
                    if (!string.IsNullOrWhiteSpace(report.Note))
                    {
                        w.WriteLine($"Note: {report.Note}");
                    }
                    foreach (var d in report.Diagnostics)
                    {
                        var loc = d.Location is not null ? $" at {d.Location.ToShortString()}" : "";
                        w.WriteLine($"  [{d.Severity}] {d.Id} ({d.Category}): {d.Message}{loc}");
                    }
                    if (report.Truncated || report.TotalDiagnostics > report.Diagnostics.Count)
                    {
                        w.WriteLine($"  ... ({report.TotalDiagnostics - report.Diagnostics.Count} more diagnostic(s) truncated; showing {report.Diagnostics.Count} of {report.TotalDiagnostics}; pass --top to adjust)");
                    }
                }, outputFile);
            }
            if (snapshot.Graph.LoadStatus is { Degraded: true } degradedAn && !includeDegraded)
            {
                var degradedNames = degradedAn.DegradedProjects is { Count: > 0 }
                    ? $"; degraded: {string.Join(", ", degradedAn.DegradedProjects)}"
                    : "";
                Console.Error.WriteLine($"Error: Degraded Roslyn workspace ({degradedAn.ProjectsLoaded}/{degradedAn.ProjectsExpected} projects loaded cleanly{degradedNames}). Fix restore/compile errors or pass --include-degraded to run analyzers against partial data.");
                return 2;
            }
            return report.ErrorCount > 0 ? 1 : 0;
        }

        case "baseline":
        {
            if (snapshot.Graph.LoadStatus is { Degraded: true } degradedBase && !includeDegraded)
            {
                var degradedNames = degradedBase.DegradedProjects is { Count: > 0 }
                    ? $"; degraded: {string.Join(", ", degradedBase.DegradedProjects)}"
                    : "";
                Console.Error.WriteLine($"Error: Refusing to save baseline from degraded workspace ({degradedBase.ProjectsLoaded}/{degradedBase.ProjectsExpected} projects loaded cleanly{degradedNames}). Fix restore/compile errors or pass --include-degraded to save a partial baseline.");
                return 2;
            }
            var savePath = !string.IsNullOrWhiteSpace(outputFile)
                ? ResolveOutputFilePath(outputFile, BaselineStore.DefaultFileName)
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
                    edgeCount = snapshot.Graph.Edges.Count,
                    loadStatus = snapshot.Graph.LoadStatus is { } blLs && (blLs.Degraded || blLs.Approximate) ? blLs : null
                }, jsonOpts), null);
            }
            else
            {
                Console.WriteLine($"Saved baseline '{snapshot.SnapshotId}' to {savePath} ({snapshot.Graph.GetNodes(NodeKind.Type).Count()} types, {snapshot.Graph.Edges.Count} edges).");
                if (snapshot.Graph.LoadStatus is { } blLs && (blLs.Degraded || blLs.Approximate))
                {
                    WriteLoadStatusTextNotice(Console.Out, blLs);
                }
            }
            return 0;
        }

        case "diff":
        {
            var baseline = BaselineStore.Load(resolvedBaselinePath!);
            var scopeWarning = BaselineStore.GetScopeMismatchWarning(baseline, snapshot);
            var diffRules = plugins.Rules.Count > 0 ? [.. preloadedRules!, .. plugins.Rules] : preloadedRules!;
            var extraDetectors = plugins.EntryPointDetectors.Count > 0 ? plugins.EntryPointDetectors : null;
            var diff = analyzer.DiffSnapshots(
                baseline, snapshot, diffRules, plugins.MetricCalculators, extraDetectors,
                excludeDegradedProjects: !includeDegraded,
                rootNamespace: effectiveRootNamespace);

            if (sarifOutput)
            {
                var sarifViolations = new List<RuleViolation>(diff.NewViolations);
                var existingErrorCycleKeys = diff.NewViolations
                    .Where(v => v.Severity == RuleSeverity.Error && v.Details is not null && v.Details.TryGetValue("cycleKey", out _))
                    .Select(v => v.Details!["cycleKey"])
                    .ToHashSet(StringComparer.Ordinal);
                foreach (var c in diff.AddedCycles)
                {
                    var ck = $"{c.Scope}:{string.Join("|", c.Nodes)}";
                    if (!existingErrorCycleKeys.Contains(ck))
                    {
                        var display = c.CyclePath is { Count: >= 2 } ? string.Join(" -> ", c.CyclePath) : string.Join(" -> ", c.Nodes);
                        sarifViolations.Add(new RuleViolation(
                            RuleId: "ARCHLENS-CYCLE-DIFF",
                            RuleName: "New dependency cycle introduced",
                            Severity: RuleSeverity.Error,
                            Message: $"New {c.Scope} cycle detected ({c.Nodes.Count} nodes, {c.InternalEdgeCount} edges): {display}",
                            NodeId: c.Nodes.FirstOrDefault(),
                            Location: c.SampleEdges?.FirstOrDefault()?.Location));
                    }
                }
                WriteOutput(SarifExporter.Export(violations: sarifViolations), outputFile);
            }
            else if (jsonOutput)
            {
                WriteOutput(JsonSerializer.Serialize(new { baselinePath = Path.GetFullPath(resolvedBaselinePath!), rulesSource = preloadedRulesSource, warning = scopeWarning, diff }, jsonOpts), outputFile);
            }
            else
            {
                WriteTextOutput(w =>
                {
                    w.WriteLine($"Architecture Diff ({diff.BaselineSnapshotId} -> {diff.CurrentSnapshotId}):");
                    if (snapshot.Graph.LoadStatus is { Approximate: true } diffApproxLs && !string.IsNullOrWhiteSpace(diffApproxLs.Note))
                    {
                        w.WriteLine($"  Note: {diffApproxLs.Note}");
                    }
                    if (!string.IsNullOrWhiteSpace(scopeWarning))
                    {
                        w.WriteLine($"  {scopeWarning}");
                    }
                    if (!string.IsNullOrWhiteSpace(diff.Note))
                    {
                        w.WriteLine($"  Warning: {diff.Note}");
                    }
                    w.WriteLine($"  Types delta: {diff.TypeCountDelta:+#;-#;0} | Edges delta: {diff.EdgeCountDelta:+#;-#;0} | Cycles delta: {diff.CycleCountDelta:+#;-#;0} | Violations delta: {diff.ViolationCountDelta:+#;-#;0}");
                    if (diff.AddedCycles.Count > 0)
                    {
                        w.WriteLine($"\nAdded Cycles ({diff.AddedCycles.Count}):");
                        foreach (var c in diff.AddedCycles)
                        {
                            var display = c.CyclePath is { Count: >= 2 } ? string.Join(" -> ", c.CyclePath) : string.Join(" -> ", c.Nodes);
                            w.WriteLine($"  + ({c.Nodes.Count} nodes, {c.InternalEdgeCount} edges) {display}");
                        }
                    }
                    if (diff.ResolvedCycles.Count > 0)
                    {
                        w.WriteLine($"\nResolved Cycles ({diff.ResolvedCycles.Count}):");
                        foreach (var c in diff.ResolvedCycles)
                        {
                            var display = c.CyclePath is { Count: >= 2 } ? string.Join(" -> ", c.CyclePath) : string.Join(" -> ", c.Nodes);
                            w.WriteLine($"  - ({c.Nodes.Count} nodes, {c.InternalEdgeCount} edges) {display}");
                        }
                    }
                    if (diff.NewViolations.Count > 0)
                    {
                        w.WriteLine($"\nNew Violations ({diff.NewViolations.Count}):");
                        foreach (var v in diff.NewViolations)
                        {
                            var loc = v.Location is not null ? $" at {v.Location.ToShortString()}" : "";
                            w.WriteLine($"  + [{v.Severity}] {v.RuleId}: {v.Message}{loc}");
                        }
                    }
                    if (diff.ResolvedViolations.Count > 0)
                    {
                        w.WriteLine($"\nResolved Violations ({diff.ResolvedViolations.Count}):");
                        foreach (var v in diff.ResolvedViolations)
                        {
                            w.WriteLine($"  - [{v.Severity}] {v.RuleId}: {v.Message}");
                        }
                    }
                }, outputFile);
            }

            if (diff.VacuousConfiguration || diff.ConfigurationError)
            {
                return 2;
            }
            if (snapshot.Graph.LoadStatus is { Degraded: true } degradedDiff && !includeDegraded)
            {
                var degradedNames = degradedDiff.DegradedProjects is { Count: > 0 }
                    ? $"; degraded: {string.Join(", ", degradedDiff.DegradedProjects)}"
                    : "";
                Console.Error.WriteLine($"Error: Degraded workspace during diff ({degradedDiff.ProjectsLoaded}/{degradedDiff.ProjectsExpected} projects loaded cleanly{degradedNames}). Fix restore/compile errors or pass --include-degraded to compare partial data.");
                return 2;
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
                object payload = snapshot.Graph.LoadStatus is { } qLs && (qLs.Degraded || qLs.Approximate)
                    ? new
                    {
                        result.Columns,
                        result.Rows,
                        result.RowCount,
                        result.Truncated,
                        loadStatus = qLs
                    }
                    : result;
                WriteOutput(JsonSerializer.Serialize(payload, jsonOpts), outputFile);
            }
            else
            {
                WriteTextOutput(w =>
                {
                    w.WriteLine(string.Join(" | ", result.Columns));
                    w.WriteLine(new string('-', Math.Min(120, result.Columns.Sum(c => c.Length + 3))));
                    foreach (var row in result.Rows)
                    {
                        w.WriteLine(string.Join(" | ", result.Columns.Select(c =>
                        {
                            var val = row.GetValueOrDefault(c);
                            return val switch
                            {
                                null => "NULL",
                                IFormattable fmt => fmt.ToString(null, CultureInfo.InvariantCulture),
                                _ => val.ToString() ?? "NULL"
                            };
                        })));
                    }
                    if (result.Truncated)
                    {
                        w.WriteLine($"... (truncated at {result.RowCount} rows)");
                    }
                    WriteLoadStatusTextNotice(w, snapshot.Graph.LoadStatus);
                }, outputFile);
            }
            return 0;
        }

        case "export":
        {
            var diagramFormat = options.GetValueOrDefault("format", "mermaid");
            var scopeStr = options.GetValueOrDefault("scope", "namespace").Trim();
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
            var defaultExt = diagramFormat.ToLowerInvariant() switch { "dot" => "archlens.dot", "structurizr" => "archlens.dsl", _ => "archlens.mmd" };
            WriteOutput(diagram, string.IsNullOrWhiteSpace(outputFile) ? null : ResolveOutputFilePath(outputFile, defaultExt));
            return 0;
        }

        case "report":
        {
            var rules = plugins.Rules.Count > 0 ? [.. preloadedRules!, .. plugins.Rules] : preloadedRules!;
            var extraDetectors = plugins.EntryPointDetectors.Count > 0 ? plugins.EntryPointDetectors : null;
            var rulesReport = analyzer.CheckRules(snapshot.Graph, rules, maxViolations: 500, includeAnalyzerDiagnostics: includeAnalyzers, extraMetricCalculators: plugins.MetricCalculators, extraEntryPointDetectors: extraDetectors, excludeDegradedProjects: !includeDegraded, rootNamespace: effectiveRootNamespace);
            var diReport = analyzer.AnalyzeDi(snapshot.Graph);
            var deadReport = analyzer.AnalyzeDeadCode(snapshot.Graph, NodeKind.Type, treatPublicApiAsEntryPoint: true, maxItems: 100, extraDetectors: extraDetectors, profile: "auto");

            ArchitectureSnapshot? baselineSnap = null;
            var baselinePath = options.GetValueOrDefault("baseline") ?? BaselineStore.DiscoverBaselineFile(targetPath);
            if (!string.IsNullOrWhiteSpace(baselinePath) && File.Exists(baselinePath))
            {
                baselineSnap = BaselineStore.Load(baselinePath);
            }

            var scopeStr = options.GetValueOrDefault("scope", "namespace").Trim();
            var matrixScope = scopeStr.ToLowerInvariant() switch
            {
                "project" => NodeKind.Project,
                "type" => NodeKind.Type,
                _ => NodeKind.Namespace
            };

            var html = HtmlReportGenerator.Generate(snapshot, rulesReport, diReport, deadReport, baselineSnap, rules, matrixScope, extraMetricCalculators: plugins.MetricCalculators, extraEntryPointDetectors: extraDetectors);
            var reportPath = !string.IsNullOrWhiteSpace(outputFile)
                ? ResolveOutputFilePath(outputFile, "archlens-report.html")
                : Path.Combine(Directory.GetCurrentDirectory(), "archlens-report.html");
            WriteOutput(html, reportPath);
            if (rulesReport.VacuousConfiguration || rulesReport.ConfigurationError)
            {
                Console.Error.WriteLine($"Error: {rulesReport.Note ?? "Invalid rule configuration detected."}");
                return 2;
            }
            return 0;
        }

        case "inspect":
        {
            var (resolvedNode, ambiguous) = ArchitectureAnalyzerService.ResolveNodeWithAmbiguity(snapshot.Graph, positional[1]);
            if (ambiguous.Count > 1)
            {
                Console.Error.WriteLine($"Ambiguous symbol '{positional[1]}' matched {ambiguous.Count} symbols. Pass a fully-qualified name:");
                foreach (var c in ambiguous.Take(15))
                {
                    Console.Error.WriteLine($"  - {c.FullName}");
                }
                if (ambiguous.Count > 15)
                {
                    Console.Error.WriteLine($"  ... ({ambiguous.Count - 15} more matching symbol(s) omitted; showing 15 of {ambiguous.Count})");
                }
                return 2;
            }
            if (resolvedNode is null)
            {
                Console.Error.WriteLine($"Symbol '{positional[1]}' not found.");
                return 1;
            }

            var report = analyzer.InspectSymbol(snapshot.Graph, resolvedNode.Id, plugins.MetricCalculators)!;

            if (jsonOutput)
            {
                object payload = snapshot.Graph.LoadStatus is { } inspLs && (inspLs.Degraded || inspLs.Approximate)
                    ? new
                    {
                        report.Node,
                        report.Metrics,
                        report.OutgoingDependencies,
                        report.IncomingDependents,
                        loadStatus = inspLs
                    }
                    : report;
                WriteOutput(JsonSerializer.Serialize(payload, jsonOpts), outputFile);
            }
            else
            {
                WriteTextOutput(w =>
                {
                    w.WriteLine($"{report.Node.FullName} ({report.Node.TypeKind?.ToString() ?? report.Node.Kind.ToString()}, {report.Node.Location?.ToShortString() ?? "no source"})");
                    if (snapshot.Graph.LoadStatus is { Approximate: true } approxLs)
                    {
                        w.WriteLine($"Note: {approxLs.Note}");
                    }
                    if (report.Metrics is not null)
                    {
                        var customPart = report.Metrics.CustomMetrics is { Count: > 0 }
                            ? $", {string.Join(", ", report.Metrics.CustomMetrics.Select(kv => $"{kv.Key}={kv.Value.ToString("0.##", CultureInfo.InvariantCulture)}"))}"
                            : "";
                        w.WriteLine($"Metrics: maxCC={report.Metrics.MaxMethodComplexity}, maxCog={report.Metrics.MaxMethodCognitiveComplexity}, LCOM4={report.Metrics.Lcom4}, Ca={report.Metrics.AfferentCoupling}, Ce={report.Metrics.EfferentCoupling}, I={report.Metrics.Instability.ToString("0.00", CultureInfo.InvariantCulture)}, LOC={report.Metrics.LineCount}{customPart}");
                    }
                    w.WriteLine($"Outgoing ({report.OutgoingDependencies.Count}):");
                    foreach (var d in report.OutgoingDependencies)
                    {
                        w.WriteLine($"  -> {d.TargetId} [{string.Join(", ", d.Kinds)}]");
                    }
                    w.WriteLine($"Incoming ({report.IncomingDependents.Count}):");
                    foreach (var d in report.IncomingDependents)
                    {
                        w.WriteLine($"  <- {d.TargetId} [{string.Join(", ", d.Kinds)}]");
                    }
                }, outputFile);
            }
            return 0;
        }

        case "impact":
        {
            var (resolvedNode, ambiguous) = ArchitectureAnalyzerService.ResolveNodeWithAmbiguity(snapshot.Graph, positional[1]);
            if (ambiguous.Count > 1)
            {
                Console.Error.WriteLine($"Ambiguous symbol '{positional[1]}' matched {ambiguous.Count} symbols. Pass a fully-qualified name:");
                foreach (var c in ambiguous.Take(15))
                {
                    Console.Error.WriteLine($"  - {c.FullName}");
                }
                if (ambiguous.Count > 15)
                {
                    Console.Error.WriteLine($"  ... ({ambiguous.Count - 15} more matching symbol(s) omitted; showing 15 of {ambiguous.Count})");
                }
                return 2;
            }
            if (resolvedNode is null)
            {
                Console.Error.WriteLine($"Symbol '{positional[1]}' not found.");
                return 1;
            }

            var depth = int.TryParse(options.GetValueOrDefault("depth"), out var d) ? d : 4;
            var excludeRegs = !string.Equals(options.GetValueOrDefault("exclude-registrations"), "false", StringComparison.OrdinalIgnoreCase);
            var strict = options.ContainsKey("strict");
            var result = analyzer.AnalyzeImpact(snapshot.Graph, resolvedNode.Id, depth, 50, excludeRegs, includeTests, strict)!;

            if (jsonOutput)
            {
                var impLs = snapshot.Graph.LoadStatus is { } ls && (ls.Degraded || ls.Approximate) ? ls : null;
                object payload = summaryOnly
                    ? new
                    {
                        result.TargetNodeId,
                        result.TargetFullName,
                        result.DirectDependentsCount,
                        result.TotalTransitiveDependentsCount,
                        result.AffectedProjects,
                        result.ExcludedRegistrationCount,
                        result.ExcludedTestCount,
                        result.Note,
                        loadStatus = impLs
                    }
                    : impLs is not null
                        ? new
                        {
                            result.TargetNodeId,
                            result.TargetFullName,
                            result.DirectDependentsCount,
                            result.TotalTransitiveDependentsCount,
                            result.AffectedProjects,
                            result.ImpactedNodes,
                            result.Truncated,
                            result.ReturnedCount,
                            result.ExcludedRegistrationCount,
                            result.ExcludedTestCount,
                            result.Note,
                            loadStatus = impLs
                        }
                        : result;
                WriteOutput(JsonSerializer.Serialize(payload, jsonOpts), outputFile);
            }
            else
            {
                WriteTextOutput(w =>
                {
                    w.WriteLine($"Blast Radius for {result.TargetFullName}:");
                    w.WriteLine($"Direct dependents: {result.DirectDependentsCount} | Total transitive: {result.TotalTransitiveDependentsCount}");
                    w.WriteLine($"Affected projects: {string.Join(", ", result.AffectedProjects)}");
                    WriteLoadStatusTextNotice(w, snapshot.Graph.LoadStatus);
                    if (!string.IsNullOrWhiteSpace(result.Note))
                    {
                        w.WriteLine($"Note: {result.Note}");
                    }
                    if (!summaryOnly)
                    {
                        foreach (var node in result.ImpactedNodes)
                        {
                            var cat = node.Category != "consumer" ? $" [{node.Category}]" : "";
                            w.WriteLine($"  [d={node.Distance}]{cat} {node.FullName} ({node.Location?.ToShortString() ?? node.ProjectName})");
                        }
                        if (result.Truncated)
                        {
                            w.WriteLine($"  ... ({result.TotalTransitiveDependentsCount - result.ImpactedNodes.Count} more dependent(s) truncated; showing {result.ImpactedNodes.Count}/{result.TotalTransitiveDependentsCount})");
                        }
                    }
                }, outputFile);
            }
            return 0;
        }

        case "batch":
        {
            var rawCommands = options.TryGetValue("commands", out var cmdStr)
                ? cmdStr
                : File.ReadAllText(Path.GetFullPath(options["file"]));
            var subCommands = SplitBatchCommands(rawCommands);

            var batchResults = new Dictionary<string, object?>(StringComparer.Ordinal);
            var defaultRules = plugins.Rules.Count > 0 ? [.. preloadedRules!, .. plugins.Rules] : preloadedRules!;
            var extraDetectors = plugins.EntryPointDetectors.Count > 0 ? plugins.EntryPointDetectors : null;
            var batchExitCode = 0;

            static string AllocateBatchKey(Dictionary<string, object?> dict, string rawKey)
            {
                if (!dict.ContainsKey(rawKey)) return rawKey;
                var n = 2;
                while (dict.ContainsKey($"{rawKey} #{n}")) n++;
                return $"{rawKey} #{n}";
            }

            foreach (var sub in subCommands)
            {
                var parts = SplitBatchArgs(sub);
                if (parts.Length == 0) continue;
                var subCmd = parts[0].ToLowerInvariant();
                var resultKey = AllocateBatchKey(batchResults, sub);
                try
                {
                    var subOpts = ParseBatchSubOptions(subCmd, parts);
                    switch (subCmd)
                    {
                        case "overview":
                        {
                            var ov = analyzer.GetOverview(snapshot);
                            batchResults[resultKey] = subOpts.ContainsKey("summary-only")
                                ? new { ov.SnapshotId, ov.ProjectCount, ov.NamespaceCount, ov.TypeCount, ov.DependencyEdgeCount, ov.ProjectCycleCount, ov.NamespaceCycleCount, ov.LoadStatus }
                                : ov;
                            break;
                        }
                        case "hotspots":
                        {
                            var sb = subOpts.GetValueOrDefault("by", "complexity").Trim();
                            var tn = subOpts.TryGetValue("top", out var topRaw) ? int.Parse(topRaw, CultureInfo.InvariantCulture) : 10;
                            var sc = subOpts.GetValueOrDefault("scope", "type").Trim().ToLowerInvariant() switch
                            {
                                "namespace" => NodeKind.Namespace,
                                "method" => NodeKind.Method,
                                _ => NodeKind.Type
                            };
                            var flt = subOpts.GetValueOrDefault("filter");
                            batchResults[resultKey] = analyzer.GetHotspots(snapshot.Graph, sb, flt, sc, tn, plugins.MetricCalculators);
                            break;
                        }
                        case "check":
                        {
                            var subPreset = subOpts.GetValueOrDefault("preset");
                            var subRulesPath = subOpts.GetValueOrDefault("rules");
                            var explicitSubRootNs = subOpts.GetValueOrDefault("root-namespace");
                            string? subYamlRootNs = null;
                            IReadOnlyList<IArchitectureRule> subRules = defaultRules;
                            if (!string.IsNullOrWhiteSpace(subPreset) || !string.IsNullOrWhiteSpace(subRulesPath))
                            {
                                var custom = new List<IArchitectureRule>();
                                if (!string.IsNullOrWhiteSpace(subPreset))
                                {
                                    var presetTokens = subPreset.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                                    if (presetTokens.Length == 0)
                                    {
                                        throw new ArgumentException("'--preset' value cannot be empty.");
                                    }
                                    var seenSubPresets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                    foreach (var pName in presetTokens)
                                    {
                                        var canonicalP = ArchitecturePresets.TryResolvePresetId(pName, out var rp) ? rp : pName;
                                        if (seenSubPresets.Add(canonicalP))
                                        {
                                            custom.AddRange(ArchitecturePresets.CreateRules(pName, explicitSubRootNs ?? effectiveRootNamespace));
                                        }
                                    }
                                }
                                if (!string.IsNullOrWhiteSpace(subRulesPath))
                                {
                                    var fp = Path.GetFullPath(subRulesPath);
                                    if (!File.Exists(fp)) throw new InvalidDataException($"Rules file not found: '{fp}'.");
                                    var loadedSub = yamlProvider.LoadRules(fp, explicitSubRootNs ?? rootNamespaceOption);
                                    if (loadedSub.Count == 0 && custom.Count == 0)
                                    {
                                        throw new InvalidDataException($"Rules file '{fp}' defines 0 rules and no preset.");
                                    }
                                    custom.AddRange(loadedSub);
                                    subYamlRootNs = yamlProvider.LoadRootNamespace(fp);
                                }
                                if (plugins.Rules.Count > 0) custom.AddRange(plugins.Rules);
                                subRules = custom;
                            }
                            var subRootNs = explicitSubRootNs ?? rootNamespaceOption ?? subYamlRootNs ?? effectiveRootNamespace;
                            var subIncludeDegraded = includeDegraded || subOpts.ContainsKey("include-degraded");
                            var checkRes = analyzer.CheckRules(snapshot.Graph, subRules, 100, includeAnalyzers, plugins.MetricCalculators, extraDetectors, !subIncludeDegraded, subRootNs);
                            batchResults[resultKey] = checkRes;
                            var failWarn = options.ContainsKey("fail-on-warning") || subOpts.ContainsKey("fail-on-warning");
                            if (checkRes.VacuousConfiguration || checkRes.ConfigurationError) batchExitCode = Math.Max(batchExitCode, 2);
                            else if (snapshot.Graph.LoadStatus is { Degraded: true } && !subIncludeDegraded) batchExitCode = Math.Max(batchExitCode, 2);
                            else if (checkRes.ErrorCount > 0 || (failWarn && checkRes.WarningCount > 0)) batchExitCode = Math.Max(batchExitCode, 1);
                            break;
                        }
                        case "dead-code":
                        {
                            var sc = string.Equals(subOpts.GetValueOrDefault("scope")?.Trim(), "method", StringComparison.OrdinalIgnoreCase) ? NodeKind.Method : NodeKind.Type;
                            var prof = subOpts.GetValueOrDefault("profile") ?? "auto";
                            var flt = subOpts.GetValueOrDefault("filter");
                            var tn = subOpts.TryGetValue("top", out var topRaw) ? int.Parse(topRaw, CultureInfo.InvariantCulture) : 100;
                            batchResults[resultKey] = analyzer.AnalyzeDeadCode(snapshot.Graph, sc, true, flt, tn, extraDetectors, !includeTests, profile: prof);
                            break;
                        }
                        case "di":
                        {
                            var diRes = analyzer.AnalyzeDi(snapshot.Graph);
                            batchResults[resultKey] = diRes;
                            if ((options.ContainsKey("fail-on-captive") || subOpts.ContainsKey("fail-on-captive")) && diRes.CaptiveDependencyCount > 0)
                            {
                                batchExitCode = Math.Max(batchExitCode, 1);
                            }
                            break;
                        }
                        case "graph":
                        {
                            var sc = subOpts.GetValueOrDefault("scope", "namespace").Trim().ToLowerInvariant() switch
                            {
                                "project" => NodeKind.Project,
                                "type" => NodeKind.Type,
                                "method" => NodeKind.Method,
                                _ => NodeKind.Namespace
                            };
                            var flt = subOpts.GetValueOrDefault("filter");
                            var mn = subOpts.TryGetValue("max-nodes", out var maxRaw) ? int.Parse(maxRaw, CultureInfo.InvariantCulture) : 60;
                            batchResults[resultKey] = analyzer.GetDependencyGraph(snapshot.Graph, sc, flt, mn);
                            break;
                        }
                        default:
                            batchResults[resultKey] = new { error = $"Unsupported batch subcommand '{subCmd}'. Supported: overview, hotspots, check, dead-code, di, graph." };
                            batchExitCode = 2;
                            break;
                    }
                }
                catch (Exception subEx)
                {
                    batchResults[resultKey] = new { error = subEx.Message };
                    batchExitCode = 2;
                }
            }

            WriteOutput(JsonSerializer.Serialize(new
            {
                snapshotId = snapshot.SnapshotId,
                capturedAt = snapshot.CapturedAt,
                results = batchResults
            }, jsonOpts), outputFile);
            return batchExitCode;
        }

        default:
            Console.Error.WriteLine($"Unknown command '{command}'.");
            PrintHelp();
            return 2;
    }
}
catch (Exception ex)
{
    WriteFriendlyCliError(ex);
    return ex is FileNotFoundException or DirectoryNotFoundException or InvalidDataException or NotSupportedException or ArgumentException
        || (command == "query" && ex is InvalidOperationException)
        || (ex is InvalidOperationException && ex.Message.Contains("No built .NET assemblies", StringComparison.OrdinalIgnoreCase))
        ? 2
        : 1;
}

static void ValidateCliOptions(string command, Dictionary<string, string> options)
{
    static void RequirePositiveInt(Dictionary<string, string> opts, string flag)
    {
        if (opts.TryGetValue(flag, out var raw) && (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) || v < 1))
        {
            throw new ArgumentException($"Invalid value '{raw}' for '--{flag}'. Expected a positive integer.");
        }
    }

    static void RequireBoolValue(Dictionary<string, string> opts, string flag)
    {
        if (opts.TryGetValue(flag, out var raw) &&
            !raw.Equals("true", StringComparison.OrdinalIgnoreCase) &&
            !raw.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Invalid value '{raw}' for '--{flag}'. Expected 'true' or 'false'.");
        }
    }

    RequirePositiveInt(options, "top");
    RequirePositiveInt(options, "max-nodes");
    RequirePositiveInt(options, "max-rows");
    RequirePositiveInt(options, "depth");
    RequireBoolValue(options, "exclude-registrations");
    RequireBoolValue(options, "public-api-entry-points");

    if (options.TryGetValue("scope", out var rawScope))
    {
        var s = rawScope.Trim().ToLowerInvariant();
        string[] allowed = command switch
        {
            "hotspots" => ["type", "namespace", "method"],
            "dead-code" => ["type", "method"],
            "report" => ["project", "namespace", "type"],
            _ => ["project", "namespace", "type", "method"]
        };
        if (!allowed.Contains(s))
        {
            throw new ArgumentException($"Invalid --scope '{rawScope}' for '{command}'. Supported values: {string.Join(", ", allowed)}.");
        }
    }

    var formatFlagCount = (options.ContainsKey("format") ? 1 : 0) +
                          (options.ContainsKey("json") ? 1 : 0) +
                          (options.ContainsKey("sarif") ? 1 : 0);
    if (formatFlagCount > 1)
    {
        throw new ArgumentException("Conflicting output format options: specify only one of '--format', '--json', or '--sarif'.");
    }

    if (command == "report")
    {
        if (options.ContainsKey("json") || options.ContainsKey("sarif") ||
            (options.TryGetValue("format", out var repFmt) && !repFmt.Trim().Equals("html", StringComparison.OrdinalIgnoreCase)))
        {
            var bad = options.TryGetValue("format", out var rf) ? $"--format {rf}" : (options.ContainsKey("sarif") ? "--sarif" : "--json");
            throw new ArgumentException($"Option '{bad}' is not supported for 'report'. 'report' outputs standalone HTML only.");
        }
    }
    else if (command == "export")
    {
        if (options.ContainsKey("json") || options.ContainsKey("sarif"))
        {
            var bad = options.ContainsKey("sarif") ? "--sarif" : "--json";
            throw new ArgumentException($"Option '{bad}' is not supported for 'export'. Use '--format mermaid|dot|structurizr'.");
        }
        if (options.TryGetValue("format", out var expFmt))
        {
            var ef = expFmt.Trim().ToLowerInvariant();
            if (ef is not ("mermaid" or "dot" or "structurizr"))
            {
                throw new ArgumentException($"Invalid --format '{expFmt}' for 'export'. Supported values: mermaid, dot, structurizr.");
            }
        }
    }
    else
    {
        if (options.ContainsKey("sarif") && command is not ("check" or "diff" or "analyzers"))
        {
            throw new ArgumentException($"Option '--sarif' is not supported for '{command}'. Supported commands for SARIF: check, diff, analyzers.");
        }
        if (options.TryGetValue("format", out var rawFormat))
        {
            var f = rawFormat.Trim().ToLowerInvariant();
            string[] allowed = command switch
            {
                "check" or "diff" or "analyzers" => ["text", "json", "sarif"],
                _ => ["text", "json"]
            };
            if (!allowed.Contains(f))
            {
                throw new ArgumentException($"Invalid --format '{rawFormat}' for '{command}'. Supported values: {string.Join(", ", allowed)}.");
            }
        }
    }

    if (command == "di")
    {
        if (options.TryGetValue("project", out var diProj))
        {
            try { GlobMatcher.ValidatePattern(diProj, "'--project'"); }
            catch (InvalidOperationException ex) { throw new ArgumentException(ex.Message); }
        }
        if (options.TryGetValue("service-type", out var diSvc))
        {
            try { GlobMatcher.ValidatePattern(diSvc, "'--service-type'"); }
            catch (InvalidOperationException ex) { throw new ArgumentException(ex.Message); }
        }
    }

    if (options.TryGetValue("lifetime", out var rawLifetime) &&
        (rawLifetime.Contains(',', StringComparison.Ordinal) ||
         int.TryParse(rawLifetime.Trim(), out _) ||
         !Enum.TryParse<ServiceLifetimeKind>(rawLifetime.Trim(), ignoreCase: true, out var parsedLt) ||
         !Enum.IsDefined(typeof(ServiceLifetimeKind), parsedLt)))
    {
        throw new ArgumentException($"Invalid --lifetime '{rawLifetime}'. Supported values: singleton, scoped, transient.");
    }

    if (options.TryGetValue("filter", out var rawFilter))
    {
        try
        {
            GlobMatcher.ValidatePattern(rawFilter, "'--filter'");
        }
        catch (InvalidOperationException ex)
        {
            throw new ArgumentException(ex.Message);
        }
    }
}

static List<string> SplitBatchCommands(string input)
{
    var commands = new List<string>();
    var current = new System.Text.StringBuilder();
    char? quote = null;
    for (var i = 0; i < input.Length; i++)
    {
        var ch = input[i];
        if (quote.HasValue)
        {
            if (ch == quote.Value)
            {
                quote = null;
            }
            current.Append(ch);
        }
        else if (ch is '"' or '\'')
        {
            quote = ch;
            current.Append(ch);
        }
        else if (ch is ';' or '\r' or '\n')
        {
            var item = current.ToString().Trim();
            if (item.Length > 0)
            {
                commands.Add(item);
            }
            current.Clear();
        }
        else
        {
            current.Append(ch);
        }
    }
    if (quote.HasValue)
    {
        throw new ArgumentException($"Unterminated quote ({quote.Value}) in batch command input.");
    }
    var last = current.ToString().Trim();
    if (last.Length > 0)
    {
        commands.Add(last);
    }
    return commands;
}

static string[] SplitBatchArgs(string input)
{
    var tokens = new List<string>();
    var current = new System.Text.StringBuilder();
    char? quote = null;
    for (var i = 0; i < input.Length; i++)
    {
        var ch = input[i];
        if (quote.HasValue)
        {
            if (ch == quote.Value)
            {
                quote = null;
            }
            else
            {
                current.Append(ch);
            }
        }
        else if (ch is '"' or '\'')
        {
            quote = ch;
        }
        else if (char.IsWhiteSpace(ch))
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
        else
        {
            current.Append(ch);
        }
    }
    if (quote.HasValue)
    {
        throw new ArgumentException($"Unterminated quote ({quote.Value}) in batch subcommand '{input}'.");
    }
    if (current.Length > 0)
    {
        tokens.Add(current.ToString());
    }
    return [.. tokens];
}

static Dictionary<string, string> ParseBatchSubOptions(string subCmd, string[] parts)
{
    var boolSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "summary-only", "fail-on-warning", "fail-on-captive", "include-degraded", "include-methods", "include-analyzers"
    };
    var valSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "by", "top", "scope", "filter", "preset", "rules", "root-namespace", "profile", "max-nodes"
    };
    var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 1; i < parts.Length; i++)
    {
        var tok = parts[i];
        if (!tok.StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Unexpected argument '{tok}' in batch subcommand '{subCmd}'.");
        }
        var key = tok[2..];
        if (boolSet.Contains(key))
        {
            parsed[key] = "true";
        }
        else if (valSet.Contains(key))
        {
            if (i + 1 >= parts.Length || (parts[i + 1].StartsWith("--", StringComparison.Ordinal) &&
                (boolSet.Contains(parts[i + 1][2..]) || valSet.Contains(parts[i + 1][2..]))))
            {
                throw new ArgumentException($"Option '--{key}' in batch subcommand '{subCmd}' requires a value.");
            }
            parsed[key] = parts[++i];
        }
        else
        {
            throw new ArgumentException($"Unknown option '{tok}' in batch subcommand '{subCmd}'.");
        }
    }
    ValidateCliOptions(subCmd, parsed);
    return parsed;
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
        Violations: ordered,
        SkippedDegradedProjects: baseReport.SkippedDegradedProjects,
        SuppressedCycles: baseReport.SuppressedCycles,
        EvaluatedRules: baseReport.EvaluatedRules,
        Note: baseReport.Note,
        VacuousConfiguration: baseReport.VacuousConfiguration,
        AcknowledgedDeadCode: baseReport.AcknowledgedDeadCode,
        ConfigurationError: baseReport.ConfigurationError);
}

static void WriteLoadStatusTextNotice(TextWriter w, GraphLoadStatus? ls)
{
    if (ls is null || (!ls.Degraded && !ls.Approximate))
    {
        return;
    }

    if (ls.Degraded)
    {
        w.WriteLine($"Load Status: backend={ls.Backend}, projectsLoaded={ls.ProjectsLoaded}/{ls.ProjectsExpected}, degraded=True, approximate={ls.Approximate}");
    }
    if (!string.IsNullOrWhiteSpace(ls.Note))
    {
        w.WriteLine($"Note: {ls.Note}");
    }
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
            backend.Equals("metadata", StringComparison.OrdinalIgnoreCase))
        {
            return metadata;
        }
        if (backend.Equals("roslyn", StringComparison.OrdinalIgnoreCase))
        {
            return roslyn;
        }
        throw new ArgumentException($"Unknown backend '{backend}'. Supported values: auto, roslyn, il.");
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

static RuleSeverity? ParseCliSeverity(string? raw)
{
    if (string.IsNullOrWhiteSpace(raw)) return null;
    return raw.Trim().ToLowerInvariant() switch
    {
        "error" or "err" => RuleSeverity.Error,
        "warning" or "warn" => RuleSeverity.Warning,
        "info" or "information" or "note" => RuleSeverity.Info,
        _ => throw new ArgumentException($"Unknown --min-severity '{raw}'. Supported values: info, warning, error.")
    };
}

static string ResolveOutputFilePath(string outputPath, string defaultFileName)
{
    var trimmed = outputPath.Trim();
    var endsWithSep = trimmed.EndsWith(Path.DirectorySeparatorChar) || trimmed.EndsWith(Path.AltDirectorySeparatorChar);
    var full = Path.GetFullPath(trimmed);
    if (endsWithSep || Directory.Exists(full))
    {
        return Path.Combine(full, defaultFileName);
    }
    return full;
}

static void WriteFriendlyCliError(Exception ex)
{
    var hint = ex switch
    {
        FileNotFoundException or DirectoryNotFoundException =>
            "Verify the target path exists and points to a .slnx, .sln, .csproj, .dll, or directory containing one.",
        UnauthorizedAccessException =>
            "If --output was pointed at a directory, pass a file path (e.g. --output report.html) or ensure write permissions.",
        NotSupportedException =>
            "Supported inputs: .slnx, .sln, .csproj, .dll, .exe (or directory). Supported backends: auto, roslyn, il.",
        ArgumentException =>
            "Run 'archlens --help' to inspect valid options and syntax.",
        _ => null
    };

    Console.Error.WriteLine($"Error: {ex.Message}");
    if (!string.IsNullOrWhiteSpace(hint))
    {
        Console.Error.WriteLine($"Hint: {hint}");
    }
}

static void WriteTextOutput(Action<TextWriter> render, string? outputFile)
{
    if (!string.IsNullOrWhiteSpace(outputFile))
    {
        using var sw = new StringWriter();
        render(sw);
        WriteOutput(sw.ToString(), outputFile);
    }
    else
    {
        render(Console.Out);
    }
}

static void WriteOutput(string content, string? outputFile)
{
    if (!string.IsNullOrWhiteSpace(outputFile))
    {
        var full = Path.GetFullPath(outputFile);
        if (Directory.Exists(full))
        {
            throw new ArgumentException($"Output path '{full}' is a directory. Specify a file path (e.g. '{Path.Combine(full, "output.txt")}').");
        }
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
      --version, -v              Print ArchLens version and exit
      --backend auto|roslyn|il   Choose extraction backend ('il' reads compiled .dll+.pdb 5-10x faster)
      --tfm <tfm>                Target framework for multi-targeted projects
      --include-tests            Include test and test-support projects
      --include-generated        Include generated code in metrics/diagnostics
      --no-cache                 Disable persistent SQLite graph cache
      --refresh                  Force re-extraction and refresh the persistent SQLite cache
      --allow-plugins            Opt-in to load repository plugins from .archlens/plugins/ (or set ARCHLENS_ALLOW_PLUGINS=1)
      --plugins <dir>            Load plugins from an explicit directory (implies --allow-plugins)

    Usage:
      archlens overview  <path> [--summary-only] [--include-tests] [--include-methods] [--tfm <tfm>] [--json]
      archlens graph     <path> [--scope project|namespace|type|method] [--filter <glob>] [--max-nodes 60] [--include-tests] [--tfm <tfm>] [--json]
      archlens hotspots  <path> [--by complexity|total_complexity|god_class|cognitive|total_cognitive|lcom4|coupling|afferent|instability|distance(namespace)|lines|ctor|<custom>] [--scope type|namespace|method] [--filter <glob>] [--top 15] [--include-tests] [--tfm <tfm>] [--json]
      archlens check     <path> [--rules <yaml>] [--preset <name>] [--root-namespace <ns>] [--fail-on-warning] [--include-analyzers] [--analyzer-categories Design,Maintainability,Performance] [--include-degraded (allow degraded/partial workspaces)] [--format text|json|sarif] [--output <file>] [--include-tests] [--include-methods] [--tfm <tfm>]
      archlens query     <path> "<sql>" [--max-rows 100] [--include-methods] [--include-analyzers] [--json]
      archlens query     --schema [--json]
      archlens export    <path> [--format mermaid|dot|structurizr] [--scope project|namespace|type|method] [--filter <glob>] [--max-nodes 60] [--output <file|dir>]
      archlens report    <path> [--output <file|dir>] [--rules <yaml>] [--preset <name>] [--root-namespace <ns>] [--baseline .archlens-baseline.json] [--scope project|namespace|type]  (Roslyn only)
      archlens batch     <path> --commands "overview;hotspots --top 5;check" [--preset <name>] [--output <file>]
      archlens presets   [--json]
      archlens init      [dir] [--preset clean-architecture|vertical-slice|modular-monolith|ddd] [--root-namespace <ns>] [--output <file|dir>] [--force]
      archlens dead-code <path> [--scope type|method] [--profile auto|app|lib|strict] [--filter <glob>] [--public-api-entry-points true|false] [--top 100] [--include-tests] [--tfm <tfm>] [--json]
      archlens di        <path> [--lifetime singleton|scoped|transient] [--project <glob>] [--service-type <substr|glob>] [--only-captive] [--only-unregistered (single-impl interfaces not registered in DI)] [--include-registrations] [--fail-on-captive] [--include-tests] [--tfm <tfm>] [--json]  (Roslyn only)
      archlens analyzers <path> [--categories Design,Maintainability,Performance] [--min-severity warning|info|error] [--elevate-warnings (elevate Info and suppressed rules to Warning)] [--filter <glob: rule ID, project, file path, or type/namespace>] [--include-degraded] [--format text|json|sarif] [--output <file>] [--top 100] [--include-tests] [--tfm <tfm>]  (Roslyn only)
      archlens baseline  <path> [--output <file|dir>] [--include-degraded] [--include-tests] [--include-methods] [--tfm <tfm>] [--json]
      archlens diff      <path> [--baseline .archlens-baseline.json] [--rules <yaml>] [--preset <name>] [--root-namespace <ns>] [--format text|json|sarif] [--output <file>] [--fail-on-new-errors] [--include-tests] [--include-methods] [--tfm <tfm>]
      archlens inspect   <path> <symbol> [--include-methods] [--tfm <tfm>] [--json]
      archlens impact    <path> <symbol> [--depth 4] [--summary-only] [--exclude-registrations true|false] [--strict (strong edges only: Call/Field/Inherits/Implements/New)] [--include-tests] [--include-methods] [--tfm <tfm>] [--json]
    """);
}
