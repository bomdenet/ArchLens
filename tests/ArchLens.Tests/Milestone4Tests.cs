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
    public void PluginLoader_LoadsCustomRuleMetricAndDetectorFromIsolatedAlc_AndUnloadsCleanly()
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

                public sealed class MemberWeightMetric : ICustomMetricCalculator
                {
                    public string MetricName => "member_weight";
                    public string Description => "Weighted member count";

                    public IReadOnlyDictionary<string, double> Calculate(CodeGraph graph)
                    {
                        var result = new Dictionary<string, double>(StringComparer.Ordinal);
                        foreach (var node in graph.GetNodes(NodeKind.Type))
                        {
                            result[node.Id] = node.MemberCount * 2.5;
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
            Assert.Single(loaded.LoadedPluginFiles);
            Assert.Equal("SampleCustomPlugin.dll", loaded.LoadedPluginFiles[0]);
            Assert.Single(loaded.Rules);
            Assert.Single(loaded.MetricCalculators);
            Assert.Single(loaded.EntryPointDetectors);
            Assert.Contains(loaded.Diagnostics, d => d.Contains("BrokenPlugin.dll", StringComparison.Ordinal));

            var graph = BuildSampleGraphForPlugins(tempRoot);
            var analyzer = new ArchitectureAnalyzerService();

            // 1. Plugin rule + custom metric threshold rule
            IReadOnlyList<IArchitectureRule> combinedRules =
            [
                .. loaded.Rules,
                new MaxMetricRule("PLUGIN-M", "Keep member_weight <= 10", "Custom metric cap.", RuleSeverity.Warning, "member_weight", 10, "MyApp.*")
            ];
            var checkReport = analyzer.CheckRules(graph, combinedRules, extraMetricCalculators: loaded.MetricCalculators);
            Assert.Contains(checkReport.Violations, v => v.RuleId == "PLUGIN-001" && v.NodeId == "MyApp.ForbiddenWidget");
            Assert.Contains(checkReport.Violations, v => v.RuleId == "PLUGIN-M" && v.NodeId == "MyApp.HeavyScheduledJob");

            // 2. Hotspots & InspectSymbol expose custom metric
            var hotspots = analyzer.GetHotspots(graph, sortBy: "member_weight", scope: NodeKind.Type, topN: 5, extraMetricCalculators: loaded.MetricCalculators);
            Assert.Equal("MyApp.HeavyScheduledJob", hotspots.Items[0].NodeId);
            Assert.NotNull(hotspots.Items[0].CustomMetrics);
            Assert.Equal(15.0, hotspots.Items[0].CustomMetrics!["member_weight"]);

            var inspected = analyzer.InspectSymbol(graph, "MyApp.HeavyScheduledJob", loaded.MetricCalculators);
            Assert.NotNull(inspected?.Metrics?.CustomMetrics);
            Assert.Equal(15.0, inspected!.Metrics!.CustomMetrics!["member_weight"]);

            // 3. EntryPointDetector plugin exempts HeavyScheduledJob from dead code
            var deadWithoutPlugin = analyzer.AnalyzeDeadCode(graph, NodeKind.Type, treatPublicApiAsEntryPoint: false);
            Assert.Contains(deadWithoutPlugin.Candidates, c => c.NodeId == "MyApp.HeavyScheduledJob");

            var deadWithPlugin = analyzer.AnalyzeDeadCode(graph, NodeKind.Type, treatPublicApiAsEntryPoint: false, extraDetectors: loaded.EntryPointDetectors);
            Assert.DoesNotContain(deadWithPlugin.Candidates, c => c.NodeId == "MyApp.HeavyScheduledJob");
            Assert.Contains(deadWithPlugin.Candidates, c => c.NodeId == "MyApp.UnusedHelper");
            Assert.Contains("ScheduledJobPlugin", deadWithPlugin.ActiveDetectors);

            // 4. SQL query engine exposes custom_metrics table
            var sqlEngine = new GraphSqlQueryEngine();
            var sqlResult = sqlEngine.Execute(
                graph,
                "SELECT node_id, metric_name, value FROM custom_metrics ORDER BY value DESC",
                extraMetricCalculators: loaded.MetricCalculators);
            Assert.True(sqlResult.RowCount >= 2);
            Assert.Equal("MyApp.HeavyScheduledJob", sqlResult.Rows[0]["node_id"]);
            Assert.Equal("member_weight", sqlResult.Rows[0]["metric_name"]);
            Assert.Equal(15.0, Convert.ToDouble(sqlResult.Rows[0]["value"]));
        }
        finally
        {
            // Force GC after collectible ALC unload so Windows releases file lock before cleanup
            GC.Collect();
            GC.WaitForPendingFinalizers();
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void GraphSqlQueryEngine_ExecutesSelectCteAndJoinsAcrossAllTables_AndTruncatesAtMaxRows()
    {
        var graph = BuildRichGraph();
        var engine = new GraphSqlQueryEngine();

        // 1. JOIN across edges & nodes
        var joinRes = engine.Execute(graph, """
            SELECT a.name AS src, b.name AS dst, e.kind
            FROM edges e
            JOIN nodes a ON e.from_id = a.id
            JOIN nodes b ON e.to_id = b.id
            WHERE a.kind = 'Type' AND b.kind = 'Type'
            ORDER BY src, dst
            """);
        Assert.True(joinRes.RowCount >= 2);
        Assert.Equal(["src", "dst", "kind"], joinRes.Columns);

        // 2. WITH CTE querying metrics
        var cteRes = engine.Execute(graph, """
            WITH high_ce AS (
                SELECT node_id, name, efferent_coupling, lcom4
                FROM metrics
                WHERE kind = 'Type'
            )
            SELECT name, efferent_coupling FROM high_ce ORDER BY efferent_coupling DESC, name ASC
            """);
        Assert.True(cteRes.RowCount >= 3);

        // 3. Projects, DI registrations, analyzer diagnostics tables
        var projRes = engine.Execute(graph, "SELECT name, target_framework FROM projects ORDER BY name");
        Assert.Equal(2, projRes.RowCount);

        var diRes = engine.Execute(graph, "SELECT service_type_id, lifetime FROM di_registrations");
        Assert.Equal(1, diRes.RowCount);
        Assert.Equal("Scoped", diRes.Rows[0]["lifetime"]);

        var diagRes = engine.Execute(graph, "SELECT id, category, severity FROM analyzer_diagnostics");
        Assert.Equal(1, diagRes.RowCount);
        Assert.Equal("CA1001", diagRes.Rows[0]["id"]);

        // 4. String literals containing forbidden SQL keywords must be allowed
        var literalRes = engine.Execute(graph, "SELECT name FROM nodes WHERE name LIKE '%Delete%' OR name = 'DROP'");
        Assert.Equal(1, literalRes.RowCount);
        Assert.Equal("DeleteOrderHandler", literalRes.Rows[0]["name"]);

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
    public void GraphSqlQueryEngine_RejectsMutationsDdlPragmaAndMultiStatements(string unsafeSql)
    {
        var graph = BuildRichGraph();
        var engine = new GraphSqlQueryEngine();

        Assert.ThrowsAny<Exception>(() => engine.Execute(graph, unsafeSql));
    }

    [Fact]
    public void ArchitecturePresets_EnforcesCleanArchitectureVerticalSliceModularMonolithAndDdd()
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

        // 1. Clean Architecture preset
        var cleanGraph = new CodeGraph("/repo",
            [
                MakeType("MyApp.Domain.Order", "MyApp.Domain", "MyApp.Domain"),
                MakeType("MyApp.Application.CreateOrder", "MyApp.Application", "MyApp.Application"),
                MakeType("MyApp.Infrastructure.OrderRepo", "MyApp.Infrastructure", "MyApp.Infrastructure")
            ],
            [
                // Valid: Application -> Domain
                new DependencyEdge("MyApp.Application.CreateOrder", "MyApp.Domain.Order", DependencyKind.MethodCall),
                // Invalid: Domain -> Infrastructure
                new DependencyEdge("MyApp.Domain.Order", "MyApp.Infrastructure.OrderRepo", DependencyKind.FieldOrProperty)
            ],
            []);
        var cleanReport = analyzer.CheckRules(cleanGraph, ArchitecturePresets.CreateRules("clean-architecture", "MyApp"));
        Assert.Contains(cleanReport.Violations, v => v.RuleId == "CLEAN-001" && v.NodeId == "MyApp.Domain.Order");
        Assert.DoesNotContain(cleanReport.Violations, v => v.RuleId == "CLEAN-002");

        // 2. Modular Monolith preset (SliceIsolationRule)
        var modularGraph = new CodeGraph("/repo",
            [
                MakeType("MyApp.Modules.Orders.OrderService", "MyApp.Modules.Orders", "Orders"),
                MakeType("MyApp.Modules.Billing.InternalLedger", "MyApp.Modules.Billing", "Billing"),
                MakeType("MyApp.Modules.Billing.Contracts.IBillingGateway", "MyApp.Modules.Billing.Contracts", "Billing", ModelTypeKind.Interface)
            ],
            [
                // Allowed: cross-module reference to .Contracts
                new DependencyEdge("MyApp.Modules.Orders.OrderService", "MyApp.Modules.Billing.Contracts.IBillingGateway", DependencyKind.FieldOrProperty),
                // Forbidden: cross-module reference to Billing internal class
                new DependencyEdge("MyApp.Modules.Orders.OrderService", "MyApp.Modules.Billing.InternalLedger", DependencyKind.MethodCall)
            ],
            []);
        var modReport = analyzer.CheckRules(modularGraph, ArchitecturePresets.CreateRules("modular-monolith", "MyApp"));
        Assert.Contains(modReport.Violations, v => v.RuleId == "MOD-001" && v.TargetNodeId == "MyApp.Modules.Billing.InternalLedger");
        Assert.DoesNotContain(modReport.Violations, v => v.TargetNodeId == "MyApp.Modules.Billing.Contracts.IBillingGateway");

        // 3. Vertical Slice preset
        var vsaGraph = new CodeGraph("/repo",
            [
                MakeType("MyApp.Features.Checkout.CheckoutHandler", "MyApp.Features.Checkout", "App"),
                MakeType("MyApp.Features.Catalog.CatalogDb", "MyApp.Features.Catalog", "App"),
                MakeType("MyApp.Shared.SystemClock", "MyApp.Shared", "App")
            ],
            [
                new DependencyEdge("MyApp.Features.Checkout.CheckoutHandler", "MyApp.Features.Catalog.CatalogDb", DependencyKind.MethodCall),
                new DependencyEdge("MyApp.Shared.SystemClock", "MyApp.Features.Checkout.CheckoutHandler", DependencyKind.TypeReference)
            ],
            []);
        var vsaReport = analyzer.CheckRules(vsaGraph, ArchitecturePresets.CreateRules("vertical-slice", "MyApp"));
        Assert.Contains(vsaReport.Violations, v => v.RuleId == "VSA-001");
        Assert.Contains(vsaReport.Violations, v => v.RuleId == "VSA-002");

        // 4. DDD preset
        var dddGraph = new CodeGraph("/repo",
            [
                MakeType("MyApp.Domain.BadInterfaceName", "MyApp.Domain", "Domain", ModelTypeKind.Interface)
            ],
            [],
            []);
        var dddReport = analyzer.CheckRules(dddGraph, ArchitecturePresets.CreateRules("ddd", "MyApp"));
        Assert.Contains(dddReport.Violations, v => v.RuleId == "DDD-002" && v.NodeId == "MyApp.Domain.BadInterfaceName");

        // 5. YAML preset loading + GenerateYamlTemplate round-trip
        var yamlFile = Path.Combine(Path.GetTempPath(), $"archlens_preset_{Guid.NewGuid():N}.yaml");
        try
        {
            var template = ArchitecturePresets.GenerateYamlTemplate("modular-monolith", "MyApp");
            File.WriteAllText(yamlFile, template);

            var provider = new YamlRuleSetProvider();
            var yamlRules = provider.LoadRules(yamlFile);
            Assert.NotEmpty(yamlRules);
            Assert.Contains(yamlRules, r => r.Id == "MOD-001");
        }
        finally
        {
            if (File.Exists(yamlFile)) File.Delete(yamlFile);
        }
    }

    [Fact]
    public void GraphVisualizer_ExportsMermaidDotAndStructurizrDsl_WithCycleHighlighting()
    {
        var graph = BuildCyclicGraph();

        var mermaid = GraphVisualizer.Export(graph, "mermaid", NodeKind.Namespace);
        Assert.StartsWith("flowchart LR", mermaid);
        Assert.Contains("==>", mermaid);
        Assert.Contains("linkStyle", mermaid);
        Assert.Contains("stroke:#dc2626", mermaid);

        var dot = GraphVisualizer.Export(graph, "dot", NodeKind.Namespace);
        Assert.StartsWith("digraph ArchLens", dot);
        Assert.Contains("color=\"#dc2626\"", dot);
        Assert.Contains("->", dot);

        var dsl = GraphVisualizer.Export(graph, "structurizr", NodeKind.Namespace);
        Assert.Contains("workspace ", dsl);
        Assert.Contains("model {", dsl);
        Assert.Contains("views {", dsl);
        Assert.Contains("->", dsl);

        Assert.Throws<ArgumentException>(() => GraphVisualizer.Export(graph, "unknown-fmt"));
    }

    [Fact]
    public void HtmlReportGenerator_GeneratesSelfContainedReport_WithDsmTrendAndXssEscaping()
    {
        var baselineGraph = BuildCyclicGraph();
        var baselineSnap = new ArchitectureSnapshot("snap-base", "/repo/App.slnx", DateTimeOffset.UtcNow.AddHours(-1), baselineGraph);

        // Add an adversarial node name to verify XSS HTML encoding
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

        var currentSnap = new ArchitectureSnapshot("snap-curr", "/repo/App.slnx", DateTimeOffset.UtcNow, currentGraph);
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
        Assert.Contains("Dependency Structure Matrix", html);
        Assert.Contains("class=\"dep cycle\"", html);
        Assert.Contains("<svg ", html);
        Assert.Contains("Metrics Trend vs Baseline", html);
        Assert.Contains("Architecture Rule Check", html);
        Assert.Contains("DI Captive Dependencies", html);
        Assert.Contains("Dead Code Candidates", html);
        // Must escape HTML/XSS payload
        Assert.DoesNotContain(xssName, html);
        Assert.Contains("&lt;script&gt;alert", html);
    }

    [Fact]
    public void McpPromptsAndResources_ExposeExpectedAttributesAndContent()
    {
        // Verify Prompt attributes & outputs
        Assert.NotNull(typeof(ArchLensPrompts).GetCustomAttribute<McpServerPromptTypeAttribute>());
        var promptMethods = typeof(ArchLensPrompts)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() is not null)
            .ToList();
        Assert.Equal(4, promptMethods.Count);

        var auditPrompt = ArchLensPrompts.AuditArchitecture("./MyApp.slnx", "clean-architecture");
        Assert.Contains("archlens_overview", auditPrompt);
        Assert.Contains("clean-architecture", auditPrompt);
        Assert.Contains("archlens_query", auditPrompt);

        var refactorPrompt = ArchLensPrompts.PlanLayerRefactoring("./MyApp.slnx", "MyApp.Domain.*");
        Assert.Contains("archlens_diff", refactorPrompt);
        Assert.Contains("archlens_impact", refactorPrompt);

        var prPrompt = ArchLensPrompts.ReviewPrArchitecture("./MyApp.slnx", ".archlens-baseline.json");
        Assert.Contains("archlens_diff", prPrompt);
        Assert.Contains(".archlens-baseline.json", prPrompt);

        var deadPrompt = ArchLensPrompts.InvestigateDeadCode("./MyApp.slnx", "MyApp.*");
        Assert.Contains("archlens_dead_code", deadPrompt);

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
        Assert.Contains("modular-monolith", ArchLensResources.GetPresetsCatalog());
        Assert.Contains("slice_isolation", ArchLensResources.GetRulesReference());
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
                MakeType("MyApp.UnusedHelper", "MyApp", "MyApp", memberCount: 1)
            ],
            [],
            [new ModelProjectInfo("MyApp", Path.Combine(rootPath, "MyApp.csproj"), "net10.0", [], [], 3, 1)]);
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
