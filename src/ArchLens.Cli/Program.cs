using System.Text.Json;
using System.Text.Json.Serialization;
using ArchLens.Analysis;
using ArchLens.Extraction.Roslyn;
using ArchLens.Model;
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

for (var i = 1; i < args.Length; i++)
{
    if (args[i].StartsWith("--", StringComparison.Ordinal))
    {
        var key = args[i][2..];
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
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

var jsonOutput = options.ContainsKey("json");
var includeTests = options.ContainsKey("include-tests");
var targetPath = positional.Count > 0 ? positional[0] : Directory.GetCurrentDirectory();

var extractor = new RoslynCodeGraphExtractor();
var analyzer = new ArchitectureAnalyzerService();
var yamlProvider = new YamlRuleSetProvider();

var jsonOpts = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
};

try
{
    var snapshot = await extractor.ExtractAsync(targetPath, new ExtractionOptions(IncludeTestProjects: includeTests));

    switch (command)
    {
        case "overview":
        {
            var report = analyzer.GetOverview(snapshot);
            if (jsonOutput)
            {
                Console.WriteLine(JsonSerializer.Serialize(report, jsonOpts));
            }
            else
            {
                Console.WriteLine($"ArchLens Overview: {report.TargetPath}");
                Console.WriteLine($"Projects: {report.ProjectCount} | Namespaces: {report.NamespaceCount} | Types: {report.TypeCount} | Edges: {report.DependencyEdgeCount}");
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
                _ => NodeKind.Namespace
            };
            var filter = options.GetValueOrDefault("filter");
            var maxNodes = int.TryParse(options.GetValueOrDefault("max-nodes"), out var m) ? m : 60;
            var report = analyzer.GetDependencyGraph(snapshot.Graph, scope, filter, maxNodes);

            if (jsonOutput)
            {
                Console.WriteLine(JsonSerializer.Serialize(report, jsonOpts));
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
            var scope = options.GetValueOrDefault("scope", "type").Equals("namespace", StringComparison.OrdinalIgnoreCase)
                ? NodeKind.Namespace
                : NodeKind.Type;
            var filter = options.GetValueOrDefault("filter");
            var top = int.TryParse(options.GetValueOrDefault("top"), out var t) ? t : 15;
            var report = analyzer.GetHotspots(snapshot.Graph, sortBy, filter, scope, top);

            if (jsonOutput)
            {
                Console.WriteLine(JsonSerializer.Serialize(report, jsonOpts));
            }
            else
            {
                Console.WriteLine($"Top {report.Items.Count} Hotspots (scope={report.Scope}, sortBy={report.SortBy}):");
                foreach (var item in report.Items)
                {
                    var loc = item.Location is not null ? $" ({item.Location.ToShortString()})" : "";
                    Console.WriteLine($"  - {item.Name}: maxCC={item.MaxMethodComplexity}, totalCC={item.CyclomaticComplexity}, Ce={item.EfferentCoupling}, Ca={item.AfferentCoupling}, I={item.Instability:0.00}, LOC={item.LineCount}, ctor={item.ConstructorParameterCount}{loc}");
                }
            }
            return 0;
        }

        case "check":
        {
            var rulesPath = options.GetValueOrDefault("rules") ?? YamlRuleSetProvider.DiscoverConfigFile(targetPath);
            IReadOnlyList<IArchitectureRule> rules = rulesPath is not null
                ? yamlProvider.LoadRules(rulesPath)
                : [
                    new NoCyclesRule("BUILTIN-001", "No project cycles", "Projects must form a DAG.", RuleSeverity.Error, NodeKind.Project),
                    new NoCyclesRule("BUILTIN-002", "No namespace cycles", "Namespaces must form a DAG.", RuleSeverity.Warning, NodeKind.Namespace)
                  ];

            var report = analyzer.CheckRules(snapshot.Graph, rules);
            if (jsonOutput)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { rulesSource = rulesPath ?? "built-in", report }, jsonOpts));
            }
            else
            {
                Console.WriteLine($"Rules Check ({rulesPath ?? "built-in"}): {report.RulesEvaluated} rules | {report.ErrorCount} errors, {report.WarningCount} warnings, {report.InfoCount} info");
                foreach (var v in report.Violations)
                {
                    var loc = v.Location is not null ? $" at {v.Location.ToShortString()}" : "";
                    Console.WriteLine($"  [{v.Severity}] {v.RuleId}: {v.Message}{loc}");
                }
            }
            return report.ErrorCount > 0 ? 1 : 0;
        }

        case "inspect":
        {
            if (positional.Count < 2)
            {
                Console.Error.WriteLine("Usage: archlens inspect <path> <symbol> [--json]");
                return 2;
            }
            var report = analyzer.InspectSymbol(snapshot.Graph, positional[1]);
            if (report is null)
            {
                Console.Error.WriteLine($"Symbol '{positional[1]}' not found.");
                return 1;
            }

            if (jsonOutput)
            {
                Console.WriteLine(JsonSerializer.Serialize(report, jsonOpts));
            }
            else
            {
                Console.WriteLine($"{report.Node.FullName} ({report.Node.TypeKind}, {report.Node.Location?.ToShortString() ?? "no source"})");
                if (report.Metrics is not null)
                {
                    Console.WriteLine($"Metrics: maxCC={report.Metrics.MaxMethodComplexity}, Ca={report.Metrics.AfferentCoupling}, Ce={report.Metrics.EfferentCoupling}, I={report.Metrics.Instability:0.00}, LOC={report.Metrics.LineCount}");
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
                Console.Error.WriteLine("Usage: archlens impact <path> <symbol> [--depth 4] [--json]");
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
                Console.WriteLine(JsonSerializer.Serialize(result, jsonOpts));
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

static void PrintHelp()
{
    Console.WriteLine("""
    ArchLens CLI - Agent-friendly C# Architecture Analyzer

    Usage:
      archlens overview <path> [--include-tests] [--json]
      archlens graph    <path> [--scope project|namespace|type] [--filter <glob>] [--max-nodes 60] [--json]
      archlens hotspots <path> [--by complexity|coupling|afferent|instability|distance|lines|ctor] [--scope type|namespace] [--top 15] [--json]
      archlens check    <path> [--rules <path-to-yaml>] [--json]
      archlens inspect  <path> <symbol> [--json]
      archlens impact   <path> <symbol> [--depth 4] [--json]
    """);
}
