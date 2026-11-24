using ArchLens.Analysis;
using ArchLens.Model;
using ArchLens.Rules.Yaml;

namespace ArchLens.Tests;

public sealed class AnalysisEngineTests
{
    private static CodeGraph BuildSampleGraph()
    {
        var nodes = new List<CodeNode>
        {
            new("MyApp.Domain.Order", "Order", "MyApp.Domain.Order", NodeKind.Type, "MyApp.Domain", "MyApp.Domain", TypeKind.Class, LineCount: 120, CyclomaticComplexity: 18, ConstructorParameterCount: 2, Metadata: new Dictionary<string, string> { ["maxMethodComplexity"] = "8" }),
            new("MyApp.Domain.IOrderRepo", "IOrderRepo", "MyApp.Domain.IOrderRepo", NodeKind.Type, "MyApp.Domain", "MyApp.Domain", TypeKind.Interface, IsAbstract: true, LineCount: 15, CyclomaticComplexity: 0),
            new("MyApp.App.OrderService", "OrderService", "MyApp.App.OrderService", NodeKind.Type, "MyApp.App", "MyApp.App", TypeKind.Class, LineCount: 240, CyclomaticComplexity: 35, ConstructorParameterCount: 6, Metadata: new Dictionary<string, string> { ["maxMethodComplexity"] = "18" }),
            new("MyApp.Infra.SqlOrderRepo", "SqlOrderRepo", "MyApp.Infra.SqlOrderRepo", NodeKind.Type, "MyApp.Infra", "MyApp.Infra", TypeKind.Class, LineCount: 180, CyclomaticComplexity: 14, ConstructorParameterCount: 3, Metadata: new Dictionary<string, string> { ["maxMethodComplexity"] = "6" }),
            new("MyApp.Api.OrdersController", "OrdersController", "MyApp.Api.OrdersController", NodeKind.Type, "MyApp.Api", "MyApp.Api", TypeKind.Class, LineCount: 90, CyclomaticComplexity: 9, ConstructorParameterCount: 2, Metadata: new Dictionary<string, string> { ["maxMethodComplexity"] = "4" })
        };

        var edges = new List<DependencyEdge>
        {
            // Clean edges
            new("MyApp.App.OrderService", "MyApp.Domain.Order", DependencyKind.MethodCall),
            new("MyApp.App.OrderService", "MyApp.Domain.IOrderRepo", DependencyKind.FieldOrProperty),
            new("MyApp.Infra.SqlOrderRepo", "MyApp.Domain.IOrderRepo", DependencyKind.Implements),
            new("MyApp.Infra.SqlOrderRepo", "MyApp.Domain.Order", DependencyKind.ReturnType),
            new("MyApp.Api.OrdersController", "MyApp.App.OrderService", DependencyKind.FieldOrProperty),
            // Intentional architectural violation & cycle: Domain -> Infra
            new("MyApp.Domain.Order", "MyApp.Infra.SqlOrderRepo", DependencyKind.TypeReference, new SourceLocation("src/Domain/Order.cs", 42, 5))
        };

        return new CodeGraph("C:/repo", nodes, edges);
    }

    [Fact]
    public void CycleDetector_FindsNamespaceCycle()
    {
        var graph = BuildSampleGraph();

        var cycles = CycleDetector.FindCycles(graph, NodeKind.Namespace);

        Assert.Single(cycles);
        Assert.Equal(["MyApp.Domain", "MyApp.Infra"], cycles[0].Nodes);
    }

    [Fact]
    public void MetricsEngine_CalculatesCouplingInstabilityAndAbstractness()
    {
        var graph = BuildSampleGraph();
        var engine = new MetricsEngine();

        var typeMetrics = engine.CalculateTypeMetrics(graph);
        var orderService = typeMetrics["MyApp.App.OrderService"];

        Assert.Equal(1, orderService.AfferentCoupling); // OrdersController
        Assert.Equal(2, orderService.EfferentCoupling); // Order, IOrderRepo
        Assert.Equal(0.667, orderService.Instability);
        Assert.Equal(18, orderService.MaxMethodComplexity);
        Assert.Equal(6, orderService.ConstructorParameterCount);

        var nsMetrics = engine.CalculateNamespaceMetrics(graph);
        var domain = nsMetrics["MyApp.Domain"];
        Assert.Equal(0.5, domain.Abstractness); // 1 interface out of 2 types
    }

    [Fact]
    public void ImpactAnalyzer_ComputesTransitiveBlastRadius()
    {
        var graph = BuildSampleGraph();
        var analyzer = new ArchitectureAnalyzerService();

        var impact = analyzer.AnalyzeImpact(graph, "IOrderRepo", maxDepth: 4);

        Assert.NotNull(impact);
        Assert.Equal(2, impact.DirectDependentsCount); // OrderService, SqlOrderRepo
        Assert.True(impact.TotalTransitiveDependentsCount >= 3); // + OrdersController, Order
        Assert.Contains("MyApp.Api", impact.AffectedProjects);
    }

    [Fact]
    public void YamlRules_EvaluateLayerAndMetricViolations()
    {
        var graph = BuildSampleGraph();
        var yaml = """
            version: 1
            rules:
              - id: R-01
                name: Domain must not depend on Infra
                severity: error
                type: forbid_dependency
                from: "MyApp.Domain.*"
                to:
                  - "MyApp.Infra.*"
              - id: R-02
                name: No namespace cycles
                severity: error
                type: no_cycles
                scope: namespace
              - id: R-03
                name: Max method complexity
                severity: warning
                type: max_metric
                metric: cyclomatic_complexity
                threshold: 15
              - id: R-04
                name: Max constructor parameters
                severity: warning
                type: max_metric
                metric: constructor_params
                threshold: 5
            """;

        var provider = new YamlRuleSetProvider();
        var rules = provider.ParseYaml(yaml);
        var analyzer = new ArchitectureAnalyzerService();

        var report = analyzer.CheckRules(graph, rules);

        Assert.Equal(4, report.RulesEvaluated);
        Assert.Equal(2, report.ErrorCount);
        Assert.Equal(2, report.WarningCount);
        Assert.Contains(report.Violations, v => v.RuleId == "R-01" && v.Location?.Line == 42);
        Assert.Contains(report.Violations, v => v.RuleId == "R-02");
        Assert.Contains(report.Violations, v => v.RuleId == "R-03" && v.NodeId == "MyApp.App.OrderService");
        Assert.Contains(report.Violations, v => v.RuleId == "R-04" && v.NodeId == "MyApp.App.OrderService");
    }

    [Fact]
    public void SnapshotDiff_DetectsResolvedCycleAndViolation()
    {
        var beforeGraph = BuildSampleGraph();
        var cleanEdges = beforeGraph.Edges
            .Where(e => !(e.FromId == "MyApp.Domain.Order" && e.ToId == "MyApp.Infra.SqlOrderRepo"))
            .ToList();
        var afterGraph = new CodeGraph("C:/repo", beforeGraph.Nodes, cleanEdges);

        var baseline = new ArchitectureSnapshot("s1", "C:/repo", DateTimeOffset.UtcNow.AddMinutes(-1), beforeGraph);
        var current = new ArchitectureSnapshot("s2", "C:/repo", DateTimeOffset.UtcNow, afterGraph);

        var rules = new IArchitectureRule[]
        {
            new ForbidDependencyRule("R-01", "No Domain->Infra", "", RuleSeverity.Error, "MyApp.Domain.*", ["MyApp.Infra.*"])
        };

        var analyzer = new ArchitectureAnalyzerService();
        var diff = analyzer.DiffSnapshots(baseline, current, rules);

        Assert.Equal(-1, diff.CycleCountDelta);
        Assert.Single(diff.ResolvedCycles);
        Assert.Empty(diff.AddedCycles);
        Assert.Equal(-1, diff.ViolationCountDelta);
        Assert.Single(diff.ResolvedViolations);
        Assert.Empty(diff.NewViolations);
    }
}
