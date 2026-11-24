using System.Reflection;
using ArchLens.Analysis;
using ArchLens.Mcp;
using ArchLens.Model;
using ArchLens.Query;
using ArchLens.Rules.Yaml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Server;
using ModelProjectInfo = ArchLens.Model.ProjectInfo;
using ModelTypeKind = ArchLens.Model.TypeKind;

namespace ArchLens.Tests;

public class Milestone4Tests
{
    [Fact]
    public void PluginLoader_LoadsCustomRuleMetricAndDetectorFromIsolatedAlc_WithoutLockingDll_AndHandlesPartialFailure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"archlens_m4_plugin_{Guid.NewGuid():N}");
        var pluginsDir = Path.Combine(tempRoot, ".archlens", "plugins");
        Directory.CreateDirectory(pluginsDir);

        try
        {
            const string pluginSource = """
                using System;
                using System.Collections.Generic;
                using ArchLens.Model;

                namespace SamplePlugin;

                public sealed class NoForbiddenPrefixRule : IArchitectureRule
                {
                    public string Id => "PLUGIN-001";
                    public string Name => "Forbid Forbidden* types";
                    public string Description => "Custom plugin rule.";
                    public RuleSeverity Severity => RuleSeverity.Error;

                    public IEnumerable<RuleViolation> Evaluate(CodeGraph graph, IReadOnlyDictionary<string, NodeMetrics> metrics)
                    {
                        foreach (var node in graph.GetNodes(NodeKind.Type))
                        {
                            if (node.Name.StartsWith("Forbidden", StringComparison.Ordinal))
                            {
                                yield return new RuleViolation(Id, Name, Severity, $"Type '{node.Name}' is forbidden by plugin.", node.Id, null, node.Location);
                            }
                        }
                    }
                }

                public sealed class ThrowingCtorRule : IArchitectureRule
                {
                    public ThrowingCtorRule() => throw new InvalidOperationException("Intentional ctor failure");
                    public string Id => "PLUGIN-BAD";
                    public string Name => "Bad";
                    public string Description => "Bad";
                    public RuleSeverity Severity => RuleSeverity.Error;
                    public IEnumerable<RuleViolation> Evaluate(CodeGraph graph, IReadOnlyDictionary<string, NodeMetrics> metrics) => [];
                }

                public sealed class MemberWeightMetric : ICustomMetricCalculator
                {
                    public string MetricName => "member_weight";
                    public string Description => "Weighted member count";

                    public IReadOnlyDictionary<string, double> Calculate(CodeGraph graph)
                    {
                        var result = new Dictionary<string, double>(StringComparer.Ordinal);
                        foreach (var node in graph.Nodes)
                        {
                            if (node.Kind == NodeKind.Type)
                            {
                                result[node.Id] = node.MemberCount * 2.5;
                            }
                            else if (node.Kind == NodeKind.Method)
                            {
                                result[node.Id] = 42.0;
                            }
                        }
                        return result;
                    }
                }

                public sealed class ScheduledJobEntryPointDetector : IEntryPointDetector
                {
                    public string Name => "ScheduledJobPlugin";

                    public bool IsEntryPoint(CodeNode node, CodeGraph graph) =>
                        node.Kind == NodeKind.Type && node.Name.EndsWith("ScheduledJob", StringComparison.Ordinal);
                }
                """;

            var pluginDllPath = Path.Combine(pluginsDir, "SampleCustomPlugin.dll");
            CompilePluginAssembly(pluginSource, pluginDllPath);

            // Also drop a corrupt .dll to verify graceful error isolation
            File.WriteAllText(Path.Combine(pluginsDir, "BrokenPlugin.dll"), "not-a-valid-pe-assembly");

            var discovered = PluginLoader.DiscoverPluginsDirectory(tempRoot);
            Assert.Equal(pluginsDir, discovered);

            using var loaded = PluginLoader.LoadFromDirectory(discovered);
            // Partial ctor failure in ThrowingCtorRule must NOT corrupt or unload valid sibling extensions
            Assert.Single(loaded.LoadedPluginFiles);
            Assert.Equal("SampleCustomPlugin.dll", loaded.LoadedPluginFiles[0]);
            Assert.Single(loaded.Rules);
            Assert.Single(loaded.MetricCalculators);
            Assert.Single(loaded.EntryPointDetectors);
            Assert.Contains(loaded.Diagnostics, d => d.Contains("BrokenPlugin.dll", StringComparison.Ordinal));
            Assert.Contains(loaded.Diagnostics, d => d.Contains("ThrowingCtorRule", StringComparison.Ordinal));

            // Loading via MemoryStream must NOT lock the source .dll on disk (can overwrite/delete while loaded)
            File.Delete(pluginDllPath);
            Assert.False(File.Exists(pluginDllPath));

            var graph = BuildSampleGraphForPlugins(tempRoot);
            var hostDetector = new DelegateEntryPointDetector("HostDetector", n => n.Name == "HostEntryType");
            var analyzer = new ArchitectureAnalyzerService(customEntryPointDetectors: [hostDetector]);

            // 1. Plugin rule + custom metric threshold rule + NoDeadCodeRule honoring BOTH host and plugin detectors
            IReadOnlyList<IArchitectureRule> combinedRules =
            [
                .. loaded.Rules,
                new MaxMetricRule("PLUGIN-M", "Keep member_weight <= 10", "Custom metric cap.", RuleSeverity.Warning, "member_weight", 10, "MyApp.*"),
                new NoDeadCodeRule("DEAD-001", "No dead types", "No dead types", RuleSeverity.Warning, NodeKind.Type, treatPublicApiAsEntryPoint: false)
            ];
            var checkReport = analyzer.CheckRules(
                graph,
                combinedRules,
                extraMetricCalculators: loaded.MetricCalculators,
                extraEntryPointDetectors: loaded.EntryPointDetectors);
            Assert.Contains(checkReport.Violations, v => v.RuleId == "PLUGIN-001" && v.NodeId == "MyApp.ForbiddenWidget");
            Assert.Contains(checkReport.Violations, v => v.RuleId == "PLUGIN-M" && v.NodeId == "MyApp.HeavyScheduledJob");
            // Both HeavyScheduledJob (plugin detector) and HostEntryType (host DI detector) must be exempted from DEAD-001
            Assert.DoesNotContain(checkReport.Violations, v => v.RuleId == "DEAD-001" && v.NodeId == "MyApp.HeavyScheduledJob");
            Assert.DoesNotContain(checkReport.Violations, v => v.RuleId == "DEAD-001" && v.NodeId == "MyApp.HostEntryType");
            Assert.Contains(checkReport.Violations, v => v.RuleId == "DEAD-001" && v.NodeId == "MyApp.UnusedHelper");

            // 2. Hotspots & InspectSymbol expose custom metric on Type AND Method scopes
            var hotspots = analyzer.GetHotspots(graph, sortBy: "member_weight", scope: NodeKind.Type, topN: 5, extraMetricCalculators: loaded.MetricCalculators);
            Assert.Equal("MyApp.HeavyScheduledJob", hotspots.Items[0].NodeId);
            Assert.Equal(15.0, hotspots.Items[0].CustomMetrics!["member_weight"]);

            var methodHotspots = analyzer.GetHotspots(graph, sortBy: "member_weight", scope: NodeKind.Method, topN: 5, extraMetricCalculators: loaded.MetricCalculators);
            Assert.Single(methodHotspots.Items);
            Assert.Equal(42.0, methodHotspots.Items[0].CustomMetrics!["member_weight"]);

            var inspectedMethod = analyzer.InspectSymbol(graph, "MyApp.HeavyScheduledJob.Run()", loaded.MetricCalculators);
            Assert.NotNull(inspectedMethod?.Metrics?.CustomMetrics);
            Assert.Equal(42.0, inspectedMethod!.Metrics!.CustomMetrics!["member_weight"]);

            // 3. AnalyzeDeadCode merges BOTH host and plugin detectors
            var deadWithPlugin = analyzer.AnalyzeDeadCode(graph, NodeKind.Type, treatPublicApiAsEntryPoint: false, extraDetectors: loaded.EntryPointDetectors);
            Assert.DoesNotContain(deadWithPlugin.Candidates, c => c.NodeId == "MyApp.HeavyScheduledJob");
            Assert.DoesNotContain(deadWithPlugin.Candidates, c => c.NodeId == "MyApp.HostEntryType");
            Assert.Contains(deadWithPlugin.Candidates, c => c.NodeId == "MyApp.UnusedHelper");
            Assert.Contains("ScheduledJobPlugin", deadWithPlugin.ActiveDetectors);
            Assert.Contains("HostDetector", deadWithPlugin.ActiveDetectors);

            // 4. SQL query engine exposes custom_metrics across ALL node scopes (Type + Method)
            var sqlEngine = new GraphSqlQueryEngine();
            var sqlResult = sqlEngine.Execute(
                graph,
                "SELECT node_id, metric_name, value FROM custom_metrics ORDER BY value DESC",
                extraMetricCalculators: loaded.MetricCalculators);
            Assert.Contains(sqlResult.Rows, r => (string?)r["node_id"] == "MyApp.HeavyScheduledJob.Run()" && Convert.ToDouble(r["value"]) == 42.0);
            Assert.Contains(sqlResult.Rows, r => (string?)r["node_id"] == "MyApp.HeavyScheduledJob" && Convert.ToDouble(r["value"]) == 15.0);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CustomMetrics_HandleNaNAndNegativeInfinitySafely_AcrossRulesHotspotsAndSql()
    {
        var graph = BuildRichGraph();
        var edgeCaseCalc = new DelegateMetricCalculator("weird_metric", new Dictionary<string, double>
        {
            ["App.DeleteOrderHandler"] = double.NaN,
            ["Core.OrderService"] = double.NegativeInfinity
            // Core.IOrderRepository intentionally omitted (missing)
        });

        var analyzer = new ArchitectureAnalyzerService();

        // NaN must NOT trigger MaxMetricRule
        var rule = new MaxMetricRule("M-NAN", "Cap weird_metric", "Cap", RuleSeverity.Error, "weird_metric", 10);
        var check = analyzer.CheckRules(graph, [rule], extraMetricCalculators: [edgeCaseCalc]);
        Assert.Empty(check.Violations);

        // In GetHotspots, -Infinity (present) must outrank NaN / missing
        var hotspots = analyzer.GetHotspots(graph, sortBy: "weird_metric", scope: NodeKind.Type, topN: 3, extraMetricCalculators: [edgeCaseCalc]);
        Assert.Equal("Core.OrderService", hotspots.Items[0].NodeId);

        // SQL engine must NOT crash on NaN (filters NaN before NOT NULL insert)
        var sqlEngine = new GraphSqlQueryEngine();
        var sqlRes = sqlEngine.Execute(graph, "SELECT node_id, value FROM custom_metrics", extraMetricCalculators: [edgeCaseCalc]);
        Assert.Single(sqlRes.Rows);
        Assert.Equal("Core.OrderService", sqlRes.Rows[0]["node_id"]);
    }

    [Fact]
    public void GraphSqlQueryEngine_ExecutesSelectCteJoinsReplaceAndDuplicateColumns_AndTruncatesAtMaxRows()
    {
        var graph = BuildRichGraph();
        var engine = new GraphSqlQueryEngine();

        // 1. JOIN across edges & nodes with DUPLICATE column names (Columns & Row keys must match 1:1!)
        var dupRes = engine.Execute(graph, """
            SELECT a.name, b.name, a.name AS name_1
            FROM edges e
            JOIN nodes a ON e.from_id = a.id
            JOIN nodes b ON e.to_id = b.id
            WHERE a.kind = 'Type' AND b.kind = 'Type'
            ORDER BY a.name, b.name
            """);
        Assert.True(dupRes.RowCount >= 2);
        Assert.Equal(3, dupRes.Columns.Distinct(StringComparer.Ordinal).Count());
        foreach (var col in dupRes.Columns)
        {
            Assert.True(dupRes.Rows[0].ContainsKey(col));
        }
        // First two columns must hold distinct source & target names (not overwritten!)
        Assert.NotEqual(dupRes.Rows[0][dupRes.Columns[0]], dupRes.Rows[0][dupRes.Columns[1]]);

        // 2. Built-in SQLite REPLACE() function + trailing semicolon must work
        var replaceRes = engine.Execute(graph, "SELECT REPLACE(name, 'Delete', 'Remove') AS renamed FROM nodes WHERE name LIKE '%Delete%';");
        Assert.Equal(1, replaceRes.RowCount);
        Assert.Equal("RemoveOrderHandler", replaceRes.Rows[0]["renamed"]);

        // 3. WITH CTE querying metrics
        var cteRes = engine.Execute(graph, """
            WITH high_ce AS (
                SELECT node_id, name, efferent_coupling, lcom4
                FROM metrics
                WHERE kind = 'Type'
            )
            SELECT name, efferent_coupling FROM high_ce ORDER BY efferent_coupling DESC, name ASC
            """);
        Assert.True(cteRes.RowCount >= 3);

        // 4. Projects, DI registrations, analyzer diagnostics tables
        Assert.Equal(2, engine.Execute(graph, "SELECT name FROM projects").RowCount);
        Assert.Equal("Scoped", engine.Execute(graph, "SELECT lifetime FROM di_registrations").Rows[0]["lifetime"]);
        Assert.Equal("CA1001", engine.Execute(graph, "SELECT id FROM analyzer_diagnostics").Rows[0]["id"]);

        // 5. Truncation at maxRows
        var truncRes = engine.Execute(graph, "SELECT id FROM nodes ORDER BY id", maxRows: 2);
        Assert.Equal(2, truncRes.RowCount);
        Assert.True(truncRes.Truncated);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DROP TABLE nodes")]
    [InlineData("DELETE FROM nodes")]
    [InlineData("INSERT INTO projects(name) VALUES ('evil')")]
    [InlineData("UPDATE nodes SET name = 'hacked'")]
    [InlineData("CREATE TABLE evil (id INT)")]
    [InlineData("ALTER TABLE nodes ADD COLUMN x INT")]
    [InlineData("ATTACH DATABASE ':memory:' AS aux")]
    [InlineData("PRAGMA table_info(nodes)")]
    [InlineData("VACUUM")]
    [InlineData("SELECT 1; DROP TABLE nodes;")]
    [InlineData("/* hide */ DELETE FROM nodes")]
    [InlineData("-- comment\nDROP TABLE nodes")]
    // Security regressions for backtick/bracket quoting bypasses & pragma_* TVFs
    [InlineData("WITH x AS (SELECT 1 AS `a'`) DELETE FROM nodes WHERE '1'=1")]
    [InlineData("SELECT 1 AS `a'`; PRAGMA query_only = OFF; ATTACH DATABASE 'target.db' AS s; SELECT '1'")]
    [InlineData("WITH x AS (SELECT 1 AS [a']) DELETE FROM nodes WHERE '1'=1")]
    [InlineData("SELECT * FROM pragma_database_list")]
    [InlineData("SELECT * FROM pragma_compile_options")]
    [InlineData("SELECT load_extension('evil.dll')")]
    [InlineData("SELECT 'unterminated")]
    [InlineData("SELECT /* unterminated")]
    public void GraphSqlQueryEngine_RejectsMutationsDdlPragmaBypassesAndUnterminatedLiterals(string unsafeSql)
    {
        var graph = BuildRichGraph();
        var engine = new GraphSqlQueryEngine();

        Assert.ThrowsAny<Exception>(() => engine.Execute(graph, unsafeSql));
    }

    [Fact]
    public void GraphSqlQueryEngine_InterruptsInfiniteRecursiveCteOnTimeout()
    {
        var graph = BuildRichGraph();
        var engine = new GraphSqlQueryEngine();

        var ex = Assert.Throws<TimeoutException>(() =>
            engine.Execute(
                graph,
                "WITH RECURSIVE r(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM r) SELECT COUNT(*) FROM r",
                timeout: TimeSpan.FromMilliseconds(150)));
        Assert.Contains("limit", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArchitecturePresets_EnforcesAllFourPresets_RootlessNamespaces_NestedSlices_AndAliasDedup()
    {
        Assert.Equal(4, ArchitecturePresets.All.Count);
        Assert.True(ArchitecturePresets.TryResolvePresetId("clean", out var cleanId));
        Assert.Equal("clean-architecture", cleanId);
        Assert.True(ArchitecturePresets.TryResolvePresetId("vsa", out var vsaId));
        Assert.Equal("vertical-slice", vsaId);
        Assert.True(ArchitecturePresets.TryResolvePresetId("modular", out var modId));
        Assert.Equal("modular-monolith", modId);
        Assert.True(ArchitecturePresets.TryResolvePresetId("domain-driven-design", out var dddId));
        Assert.Equal("ddd", dddId);
        Assert.Throws<ArgumentException>(() => ArchitecturePresets.CreateRules("unknown-preset"));

        var analyzer = new ArchitectureAnalyzerService();

        // 1. Clean Architecture WITHOUT root_namespace on top-level Domain.* -> Infrastructure.*
        var rootlessClean = new CodeGraph("/repo",
            [
                MakeType("Domain.Order", "Domain", "Domain"),
                MakeType("Infrastructure.OrderRepo", "Infrastructure", "Infrastructure")
            ],
            [
                new DependencyEdge("Domain.Order", "Infrastructure.OrderRepo", DependencyKind.FieldOrProperty)
            ],
            []);
        var rootlessReport = analyzer.CheckRules(rootlessClean, ArchitecturePresets.CreateRules("clean-architecture", rootNamespace: null));
        Assert.Contains(rootlessReport.Violations, v => v.RuleId == "CLEAN-001" && v.NodeId == "Domain.Order");

        // 2. Modular Monolith: cross-module internals forbidden; .Contracts & peer Modules.Contracts allowed; rootNamespace scoped!
        var modularGraph = new CodeGraph("/repo",
            [
                MakeType("MyApp.Modules.Orders.OrderService", "MyApp.Modules.Orders", "Orders"),
                MakeType("MyApp.Modules.Billing.InternalLedger", "MyApp.Modules.Billing", "Billing"),
                MakeType("MyApp.Modules.Billing.Contracts.IBillingGateway", "MyApp.Modules.Billing.Contracts", "Billing", ModelTypeKind.Interface),
                MakeType("MyApp.Modules.Contracts.SharedEvent", "MyApp.Modules.Contracts", "Contracts"),
                MakeType("Legacy.Modules.A.Foo", "Legacy.Modules.A", "Legacy"),
                MakeType("Legacy.Modules.B.Bar", "Legacy.Modules.B", "Legacy")
            ],
            [
                new DependencyEdge("MyApp.Modules.Orders.OrderService", "MyApp.Modules.Billing.Contracts.IBillingGateway", DependencyKind.FieldOrProperty),
                new DependencyEdge("MyApp.Modules.Orders.OrderService", "MyApp.Modules.Contracts.SharedEvent", DependencyKind.TypeReference),
                new DependencyEdge("MyApp.Modules.Orders.OrderService", "MyApp.Modules.Billing.InternalLedger", DependencyKind.MethodCall),
                // Out of rootNamespace scope ("MyApp") -> must be ignored when rootNamespace == "MyApp"
                new DependencyEdge("Legacy.Modules.A.Foo", "Legacy.Modules.B.Bar", DependencyKind.MethodCall)
            ],
            []);
        var modReport = analyzer.CheckRules(modularGraph, ArchitecturePresets.CreateRules("modular-monolith", "MyApp"));
        Assert.Contains(modReport.Violations, v => v.RuleId == "MOD-001" && v.TargetNodeId == "MyApp.Modules.Billing.InternalLedger");
        Assert.DoesNotContain(modReport.Violations, v => v.TargetNodeId == "MyApp.Modules.Billing.Contracts.IBillingGateway");
        Assert.DoesNotContain(modReport.Violations, v => v.TargetNodeId == "MyApp.Modules.Contracts.SharedEvent");
        Assert.DoesNotContain(modReport.Violations, v => v.NodeId == "Legacy.Modules.A.Foo");

        // 3. Vertical Slice: nested slices with same leaf name ("Create") under different parents must NOT collapse!
        var vsaGraph = new CodeGraph("/repo",
            [
                MakeType("MyApp.Modules.Orders.Features.Create.Handler", "MyApp.Modules.Orders.Features.Create", "App"),
                MakeType("MyApp.Modules.Billing.Features.Create.InternalDto", "MyApp.Modules.Billing.Features.Create", "App"),
                MakeType("MyApp.Shared.SystemClock", "MyApp.Shared", "App")
            ],
            [
                new DependencyEdge("MyApp.Modules.Orders.Features.Create.Handler", "MyApp.Modules.Billing.Features.Create.InternalDto", DependencyKind.MethodCall),
                new DependencyEdge("MyApp.Shared.SystemClock", "MyApp.Modules.Orders.Features.Create.Handler", DependencyKind.TypeReference)
            ],
            []);
        var vsaReport = analyzer.CheckRules(vsaGraph, ArchitecturePresets.CreateRules("vertical-slice", "MyApp"));
        Assert.Contains(vsaReport.Violations, v => v.RuleId == "VSA-001");

        // 4. YAML preset alias deduplication ([clean, clean-architecture] must not emit duplicate rules)
        var provider = new YamlRuleSetProvider();
        var dedupRules = provider.ParseYaml("""
            version: 1
            preset: clean
            presets:
              - clean-architecture
            root_namespace: MyApp
            """);
        Assert.Equal(dedupRules.Select(r => r.Id).Distinct().Count(), dedupRules.Count);
    }

    [Fact]
    public void GraphVisualizer_ExportsMermaidDotAndStructurizrDsl_WithCycleHighlightingAndGenericEscaping()
    {
        var graph = BuildCyclicGraph();

        var mermaid = GraphVisualizer.Export(graph, "mermaid", NodeKind.Namespace);
        Assert.StartsWith("flowchart LR", mermaid);
        Assert.Contains("==>", mermaid);
        Assert.Contains("linkStyle", mermaid);
        Assert.Contains("stroke:#dc2626", mermaid);

        // Generic angle brackets in Mermaid type labels must be HTML-escaped (not stripped as tags)
        var genericGraph = new CodeGraph("/repo",
            [MakeType("Ns.IRepository<Order>", "Ns", "Proj", ModelTypeKind.Interface)],
            [], []);
        var genericMermaid = GraphVisualizer.Export(genericGraph, "mermaid", NodeKind.Type);
        Assert.Contains("&lt;Order&gt;", genericMermaid);

        var dot = GraphVisualizer.Export(graph, "dot", NodeKind.Namespace);
        Assert.StartsWith("digraph ArchLens", dot);
        Assert.Contains("color=\"#dc2626\"", dot);
        Assert.Contains("->", dot);

        var dsl = GraphVisualizer.Export(graph, "structurizr", NodeKind.Namespace);
        Assert.Contains("workspace ", dsl);
        Assert.Contains("model {", dsl);
        Assert.Contains("views {", dsl);
        Assert.Contains("\"Cycle\"", dsl);
        Assert.Contains("color #dc2626", dsl);

        Assert.Throws<ArgumentException>(() => GraphVisualizer.Export(graph, "unknown-fmt"));
    }

    [Fact]
    public void HtmlReportGenerator_GeneratesSelfContainedReport_WithCspDsmTrendAndXssEscaping()
    {
        var baselineGraph = BuildCyclicGraph();
        var baselineSnap = new ArchitectureSnapshot("snap-base", "/repo/App.slnx", DateTimeOffset.UtcNow.AddHours(-1), baselineGraph);

        var xssName = "<script>alert('xss')</script>";
        var nodes = baselineGraph.Nodes.ToList();
        nodes.Add(MakeType($"Ns.C.{xssName}", "Ns.C", "ProjC", lineCount: 120, cognitive: 22, lcom4: 3));
        var currentGraph = new CodeGraph(
            "/repo",
            nodes,
            baselineGraph.Edges,
            baselineGraph.Projects,
            diRegistrations:
            [
                new DiRegistration("Ns.A.IScoped", "Ns.A.ScopedImpl", ServiceLifetimeKind.Scoped, "Ns.A.Startup", ProjectName: "ProjA"),
                new DiRegistration("Ns.A.ISingleton", "Ns.A.SingletonImpl", ServiceLifetimeKind.Singleton, "Ns.A.Startup", ProjectName: "ProjA")
            ]);

        var currentSnap = new ArchitectureSnapshot("snap-curr", "/repo/App.slnx/", DateTimeOffset.UtcNow, currentGraph);
        var rules = new IArchitectureRule[]
        {
            new NoCyclesRule("R-1", "No namespace cycles", "Must be DAG", RuleSeverity.Error, NodeKind.Namespace)
        };
        var analyzer = new ArchitectureAnalyzerService();
        var rulesReport = analyzer.CheckRules(currentGraph, rules);
        var diReport = new DiAnalysisReport(2, 1, 0, [],
            [new CaptiveDependencyIssue("Ns.A.SingletonImpl", ServiceLifetimeKind.Singleton, "Ns.A.IScoped", ServiceLifetimeKind.Scoped, "Ns.A.ScopedImpl", "Singleton -> Scoped", null)],
            []);
        var deadReport = analyzer.AnalyzeDeadCode(currentGraph, NodeKind.Type, treatPublicApiAsEntryPoint: false);

        var html = HtmlReportGenerator.Generate(currentSnap, rulesReport, diReport, deadReport, baselineSnap, rules, NodeKind.Namespace);

        Assert.Contains("<!DOCTYPE html>", html);
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("App.slnx", html);
        Assert.Contains("Dependency Structure Matrix", html);
        Assert.Contains("class=\"dep cycle\"", html);
        Assert.Contains("<svg ", html);
        Assert.Contains("Metrics Trend vs Baseline", html);
        Assert.Contains("Architecture Rule Check", html);
        Assert.Contains("DI Captive Dependencies", html);
        Assert.Contains("Dead Code Candidates", html);
        Assert.DoesNotContain(xssName, html);
        Assert.Contains("&lt;script&gt;alert", html);
    }

    [Fact]
    public void McpToolsPromptsAndResources_OmitArchlensPrefix_AndExposeExpectedMetadata()
    {
        // All MCP tool names must omit the redundant 'archlens_' prefix
        var toolNames = typeof(ArchLensTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.Name)
            .ToList();

        Assert.Equal(12, toolNames.Count);
        Assert.All(toolNames, name => Assert.DoesNotContain("archlens_", name ?? "", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("overview", toolNames);
        Assert.Contains("query", toolNames);
        Assert.Contains("check_rules", toolNames);
        Assert.Contains("export_diagram", toolNames);
        Assert.Contains("diff", toolNames);

        // Verify Prompt attributes & outputs (no archlens_ tool prefix + preset forwarding)
        Assert.NotNull(typeof(ArchLensPrompts).GetCustomAttribute<McpServerPromptTypeAttribute>());
        var promptMethods = typeof(ArchLensPrompts)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() is not null)
            .ToList();
        Assert.Equal(4, promptMethods.Count);

        var auditPrompt = ArchLensPrompts.AuditArchitecture("./MyApp.slnx", "clean-architecture");
        Assert.Contains("overview(", auditPrompt);
        Assert.Contains("preset: \"clean-architecture\"", auditPrompt);
        Assert.Contains("`query`", auditPrompt);
        Assert.DoesNotContain("archlens_", auditPrompt, StringComparison.OrdinalIgnoreCase);

        var refactorPrompt = ArchLensPrompts.PlanLayerRefactoring("./MyApp.slnx", "MyApp.Domain.*");
        Assert.Contains("diff(", refactorPrompt);
        Assert.Contains("impact(", refactorPrompt);
        Assert.DoesNotContain("archlens_", refactorPrompt, StringComparison.OrdinalIgnoreCase);

        var prPrompt = ArchLensPrompts.ReviewPrArchitecture("./MyApp.slnx", ".archlens-baseline.json");
        Assert.Contains("diff(", prPrompt);
        Assert.Contains(".archlens-baseline.json", prPrompt);
        Assert.DoesNotContain("archlens_", prPrompt, StringComparison.OrdinalIgnoreCase);

        var deadPrompt = ArchLensPrompts.InvestigateDeadCode("./MyApp.slnx", "MyApp.*");
        Assert.Contains("dead_code(", deadPrompt);
        Assert.DoesNotContain("archlens_", deadPrompt, StringComparison.OrdinalIgnoreCase);

        // Verify Resource attributes & outputs
        Assert.NotNull(typeof(ArchLensResources).GetCustomAttribute<McpServerResourceTypeAttribute>());
        var resourceAttrs = typeof(ArchLensResources)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.GetCustomAttribute<McpServerResourceAttribute>())
            .Where(a => a is not null)
            .Select(a => a!.UriTemplate)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("archlens://schema/sql", resourceAttrs);
        Assert.Contains("archlens://presets", resourceAttrs);
        Assert.Contains("archlens://rules/reference", resourceAttrs);

        Assert.Contains("custom_metrics", ArchLensResources.GetSqlSchema());
        Assert.DoesNotContain("archlens_query", ArchLensResources.GetSqlSchema());
        Assert.Contains("modular-monolith", ArchLensResources.GetPresetsCatalog());
        Assert.Contains("slice_isolation", ArchLensResources.GetRulesReference());
    }

    private sealed class DelegateEntryPointDetector(string name, Func<CodeNode, bool> predicate) : IEntryPointDetector
    {
        public string Name { get; } = name;
        public bool IsEntryPoint(CodeNode node, CodeGraph graph) => predicate(node);
    }

    private sealed class DelegateMetricCalculator(string metricName, IReadOnlyDictionary<string, double> values) : ICustomMetricCalculator
    {
        public string MetricName { get; } = metricName;
        public string Description => MetricName;
        public IReadOnlyDictionary<string, double> Calculate(CodeGraph graph) => values;
    }

    private static void CompilePluginAssembly(string sourceCode, string outputDllPath)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var refs = new List<MetadataReference>();
        var trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty;
        foreach (var path in trusted.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(path))
            {
                refs.Add(MetadataReference.CreateFromFile(path));
            }
        }
        refs.Add(MetadataReference.CreateFromFile(typeof(IArchitectureRule).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            Path.GetFileNameWithoutExtension(outputDllPath),
            [tree],
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var emitResult = compilation.Emit(outputDllPath);
        Assert.True(emitResult.Success, string.Join(Environment.NewLine, emitResult.Diagnostics.Select(d => d.ToString())));
    }

    private static CodeGraph BuildSampleGraphForPlugins(string rootPath)
    {
        return new CodeGraph(
            rootPath,
            [
                new CodeNode("proj:MyApp", "MyApp", "MyApp", NodeKind.Project, "MyApp"),
                new CodeNode("ns:MyApp", "MyApp", "MyApp", NodeKind.Namespace, "MyApp", Namespace: "MyApp"),
                MakeType("MyApp.ForbiddenWidget", "MyApp", "MyApp", memberCount: 2),
                MakeType("MyApp.HeavyScheduledJob", "MyApp", "MyApp", memberCount: 6),
                MakeType("MyApp.HostEntryType", "MyApp", "MyApp", memberCount: 1),
                MakeType("MyApp.UnusedHelper", "MyApp", "MyApp", memberCount: 1),
                new CodeNode(
                    Id: "MyApp.HeavyScheduledJob.Run()",
                    Name: "Run",
                    FullName: "MyApp.HeavyScheduledJob.Run()",
                    Kind: NodeKind.Method,
                    ProjectName: "MyApp",
                    Namespace: "MyApp",
                    ContainingTypeId: "MyApp.HeavyScheduledJob")
            ],
            [],
            [new ModelProjectInfo("MyApp", Path.Combine(rootPath, "MyApp.csproj"), "net10.0", [], [], 4, 1)]);
    }

    private static CodeGraph BuildRichGraph()
    {
        return new CodeGraph(
            "/repo",
            [
                new CodeNode("proj:App", "App", "App", NodeKind.Project, "App"),
                new CodeNode("proj:Core", "Core", "Core", NodeKind.Project, "Core"),
                new CodeNode("ns:App", "App", "App", NodeKind.Namespace, "App", Namespace: "App"),
                new CodeNode("ns:Core", "Core", "Core", NodeKind.Namespace, "Core", Namespace: "Core"),
                MakeType("App.DeleteOrderHandler", "App", "App", lineCount: 40, cognitive: 8, lcom4: 1),
                MakeType("Core.OrderService", "Core", "Core", lineCount: 90, cognitive: 14, lcom4: 2),
                MakeType("Core.IOrderRepository", "Core", "Core", ModelTypeKind.Interface, lineCount: 10)
            ],
            [
                new DependencyEdge("proj:App", "proj:Core", DependencyKind.ProjectReference),
                new DependencyEdge("App.DeleteOrderHandler", "Core.OrderService", DependencyKind.MethodCall),
                new DependencyEdge("Core.OrderService", "Core.IOrderRepository", DependencyKind.FieldOrProperty)
            ],
            projects:
            [
                new ModelProjectInfo("App", "/repo/App.csproj", "net10.0", ["Core"], [], 1, 1),
                new ModelProjectInfo("Core", "/repo/Core.csproj", "net10.0", [], [], 2, 2)
            ],
            diRegistrations:
            [
                new DiRegistration("Core.IOrderRepository", "Core.OrderService", ServiceLifetimeKind.Scoped, "App.DeleteOrderHandler", ProjectName: "App")
            ],
            analyzerDiagnostics:
            [
                new AnalyzerDiagnostic("CA1001", "Types that own disposable fields should be disposable", "Design", RuleSeverity.Warning, "Disposable field", ProjectName: "Core")
            ]);
    }

    private static CodeGraph BuildCyclicGraph()
    {
        return new CodeGraph(
            "/repo",
            [
                new CodeNode("proj:ProjA", "ProjA", "ProjA", NodeKind.Project, "ProjA"),
                new CodeNode("proj:ProjB", "ProjB", "ProjB", NodeKind.Project, "ProjB"),
                new CodeNode("proj:ProjC", "ProjC", "ProjC", NodeKind.Project, "ProjC"),
                new CodeNode("ns:Ns.A", "Ns.A", "Ns.A", NodeKind.Namespace, "ProjA", Namespace: "Ns.A"),
                new CodeNode("ns:Ns.B", "Ns.B", "Ns.B", NodeKind.Namespace, "ProjB", Namespace: "Ns.B"),
                new CodeNode("ns:Ns.C", "Ns.C", "Ns.C", NodeKind.Namespace, "ProjC", Namespace: "Ns.C"),
                MakeType("Ns.A.TypeA", "Ns.A", "ProjA"),
                MakeType("Ns.B.TypeB", "Ns.B", "ProjB"),
                MakeType("Ns.C.TypeC", "Ns.C", "ProjC")
            ],
            [
                new DependencyEdge("Ns.A.TypeA", "Ns.B.TypeB", DependencyKind.MethodCall),
                new DependencyEdge("Ns.B.TypeB", "Ns.A.TypeA", DependencyKind.MethodCall),
                new DependencyEdge("Ns.C.TypeC", "Ns.A.TypeA", DependencyKind.FieldOrProperty)
            ],
            projects:
            [
                new ModelProjectInfo("ProjA", "/repo/ProjA.csproj", "net10.0", ["ProjB"], [], 1, 1),
                new ModelProjectInfo("ProjB", "/repo/ProjB.csproj", "net10.0", ["ProjA"], [], 1, 1),
                new ModelProjectInfo("ProjC", "/repo/ProjC.csproj", "net10.0", ["ProjA"], [], 1, 1)
            ]);
    }

    private static CodeNode MakeType(
        string fullName,
        string ns,
        string project,
        ModelTypeKind typeKind = ModelTypeKind.Class,
        int lineCount = 20,
        int cognitive = 2,
        int lcom4 = 1,
        int memberCount = 3)
    {
        var shortName = fullName.Split('.').Last();
        return new CodeNode(
            Id: fullName,
            Name: shortName,
            FullName: fullName,
            Kind: NodeKind.Type,
            ProjectName: project,
            Namespace: ns,
            TypeKind: typeKind,
            Accessibility: AccessibilityKind.Public,
            IsAbstract: typeKind == ModelTypeKind.Interface,
            LineCount: lineCount,
            CyclomaticComplexity: cognitive,
            CognitiveComplexity: cognitive,
            Lcom4: lcom4,
            MemberCount: memberCount);
    }
}
