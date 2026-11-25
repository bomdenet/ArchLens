using System.Diagnostics;
using System.Text.Json;
using ArchLens.Analysis;
using ArchLens.Extraction.Metadata;
using ArchLens.Extraction.Roslyn;
using ArchLens.Mcp;
using ArchLens.Model;
using ArchLens.Query;
using ArchLens.Rules.Yaml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelContextProtocol.Protocol;
using TypeKind = ArchLens.Model.TypeKind;

namespace ArchLens.Tests;

public sealed class EvaluationFixesTests
{
    [Fact]
    public async Task Arch01_RazorComponentsAreEmittedAsNodesWithReferences_AndPreventDeadCodeAndImpactFalsePositives()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "BlazorSample.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            WriteFile(tempDir, "Components/_Imports.razor", """
                @using BlazorSample.Formatting
                @using BlazorSample.Components.Shared
                """);

            WriteFile(tempDir, "Components/Pages/GamePage.razor", """
                @page "/game"
                <TopBar />
                <GameBoard />
                """);

            WriteFile(tempDir, "Components/Pages/GamePage.razor.cs", """
                namespace BlazorSample.Components.Pages;
                public partial class GamePage
                {
                    public string Title => "Game";
                }
                """);

            WriteFile(tempDir, "Components/Shared/TopBar.razor", """
                <header>TopBar</header>
                """);

            WriteFile(tempDir, "Components/Shared/GameBoard.razor", """
                <div>@BoardCellText.Format(1)</div>
                """);

            WriteFile(tempDir, "Components/Shared/OrphanWidget.razor", """
                <div>Never used anywhere</div>
                """);

            WriteFile(tempDir, "Formatting/BoardCellText.cs", """
                namespace BlazorSample.Formatting;
                public static class BoardCellText
                {
                    public static string Format(int cell) => cell.ToString();
                }

                public static class TrulyDeadHelper
                {
                    public static int Unused() => 42;
                }
                """);

            WriteFile(tempDir, "Program.cs", """
                Console.WriteLine("Blazor host");
                """);

            using var extractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snapshot = await extractor.ExtractAsync(Path.Combine(tempDir, "BlazorSample.csproj"));
            var graph = snapshot.Graph;

            // Markup-only .razor components MUST be present as Type nodes
            Assert.NotNull(graph.FindNode("BlazorSample.Components.Pages.GamePage"));
            Assert.NotNull(graph.FindNode("BlazorSample.Components.Shared.TopBar"));
            Assert.NotNull(graph.FindNode("BlazorSample.Components.Shared.GameBoard"));
            Assert.NotNull(graph.FindNode("BlazorSample.Components.Shared.OrphanWidget"));
            Assert.Equal(4, graph.LoadStatus?.RazorComponentCount);

            // Edges from .razor markup -> child components and C# helper types MUST exist
            Assert.Contains(graph.Edges, e => e.FromId == "BlazorSample.Components.Pages.GamePage" && e.ToId == "BlazorSample.Components.Shared.TopBar");
            Assert.Contains(graph.Edges, e => e.FromId == "BlazorSample.Components.Pages.GamePage" && e.ToId == "BlazorSample.Components.Shared.GameBoard");
            Assert.Contains(graph.Edges, e => e.FromId == "BlazorSample.Components.Shared.GameBoard" && e.ToId == "BlazorSample.Formatting.BoardCellText");

            // impact on BoardCellText MUST find GameBoard (and transitively GamePage)
            var analyzer = new ArchitectureAnalyzerService();
            var impact = analyzer.AnalyzeImpact(graph, "BoardCellText");
            Assert.NotNull(impact);
            Assert.True(impact.DirectDependentsCount >= 1);
            Assert.Contains(impact.ImpactedNodes, n => n.NodeId == "BlazorSample.Components.Shared.GameBoard");

            // dead_code MUST NOT flag BoardCellText, TopBar, GameBoard, or GamePage; SHOULD flag TrulyDeadHelper & OrphanWidget
            var dead = analyzer.AnalyzeDeadCode(graph, NodeKind.Type, profile: "app");
            Assert.DoesNotContain(dead.Candidates, c => c.NodeId == "BlazorSample.Formatting.BoardCellText");
            Assert.DoesNotContain(dead.Candidates, c => c.NodeId == "BlazorSample.Components.Shared.TopBar");
            Assert.DoesNotContain(dead.Candidates, c => c.NodeId == "BlazorSample.Components.Shared.GameBoard");
            Assert.DoesNotContain(dead.Candidates, c => c.NodeId == "BlazorSample.Components.Pages.GamePage");
            Assert.Contains(dead.Candidates, c => c.NodeId == "BlazorSample.Formatting.TrulyDeadHelper");
            Assert.Contains(dead.Candidates, c => c.NodeId == "BlazorSample.Components.Shared.OrphanWidget");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Arch02_GeneratedPartialCodeIsExcludedFromLocComplexityAndLcom4()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "GenSample.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            WriteFile(tempDir, "MyContext.cs", """
                namespace GenSample;

                public partial class MyContext
                {
                    public int SmallHandWritten(int x) => x > 0 ? x : 0;
                }
                """);

            // Simulate a massive source-generated partial file (.g.cs with <auto-generated/>)
            WriteFile(tempDir, "MyContext.g.cs", """
                // <auto-generated/>
                namespace GenSample;

                public partial class MyContext
                {
                    public int HugeGenerated(int a, int b, int c)
                    {
                        if (a > 0)
                        {
                            if (b > 0)
                            {
                                if (c > 0)
                                {
                                    for (var i = 0; i < 10; i++)
                                    {
                                        if (i % 2 == 0 && a > i) return i;
                                    }
                                }
                            }
                        }
                        return -1;
                    }
                    public void GeneratedDisjointA() { }
                    public void GeneratedDisjointB() { }
                    public void GeneratedDisjointC() { }
                }
                """);

            using var extractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snapshot = await extractor.ExtractAsync(Path.Combine(tempDir, "GenSample.csproj"));

            var node = snapshot.Graph.FindNode("GenSample.MyContext");
            Assert.NotNull(node);
            // Hand-written partial is only ~4 lines (lines 3-6), NOT 30+ lines from .g.cs
            Assert.InRange(node.LineCount, 1, 10);
            // Hand-written method has CC=2, Cognitive=1; HugeGenerated would inflate both dramatically
            Assert.True(node.CyclomaticComplexity <= 3, $"Expected CC <= 3 without generated code, got {node.CyclomaticComplexity}");
            Assert.True(node.CognitiveComplexity <= 2, $"Expected Cognitive <= 2 without generated code, got {node.CognitiveComplexity}");
            Assert.Equal(1, node.Lcom4);
            Assert.Equal("true", node.Metadata?["hasGeneratedParts"]);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Arch03_DeadCodeAutoProfileAuditsLayeredAppLibrariesAndExcludesTestSupportProjects()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "Layered.slnx", """
                <Solution>
                  <Project Path="src/Domain/Domain.csproj" />
                  <Project Path="src/App/App.csproj" />
                  <Project Path="tests/Layered.Tests.Support/Layered.Tests.Support.csproj" />
                  <Project Path="tests/Layered.Tests/Layered.Tests.csproj" />
                </Solution>
                """);

            WriteFile(tempDir, "src/Domain/Domain.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            WriteFile(tempDir, "src/Domain/Entities.cs", """
                namespace Layered.Domain;
                public sealed class ActiveOrder { }
                public sealed class DeadPublicEntity { }
                """);

            WriteFile(tempDir, "src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup><ProjectReference Include="../Domain/Domain.csproj" /></ItemGroup>
                </Project>
                """);
            WriteFile(tempDir, "src/App/Program.cs", """
                var o = new Layered.Domain.ActiveOrder();
                System.Console.WriteLine(o);
                """);

            // Test-support library without xUnit package reference, referenced ONLY by Layered.Tests!
            WriteFile(tempDir, "tests/Layered.Tests.Support/Layered.Tests.Support.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../../src/Domain/Domain.csproj" /></ItemGroup>
                </Project>
                """);
            WriteFile(tempDir, "tests/Layered.Tests.Support/FakeClock.cs", """
                namespace Layered.Tests.Support;
                public sealed class FakeClock { }
                """);

            WriteFile(tempDir, "tests/Layered.Tests/Layered.Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Layered.Tests.Support/Layered.Tests.Support.csproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteFile(tempDir, "tests/Layered.Tests/OrderTests.cs", """
                namespace Layered.Tests;
                public sealed class OrderTests { public Layered.Tests.Support.FakeClock Clock { get; } = new(); }
                """);

            using var extractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snapshot = await extractor.ExtractAsync(Path.Combine(tempDir, "Layered.slnx"));

            // Both Layered.Tests AND Layered.Tests.Support must be excluded when IncludeTestProjects=false
            Assert.Equal(2, snapshot.Graph.ExcludedTestProjectCount);
            Assert.DoesNotContain(snapshot.Graph.Projects, p => p.Name == "Layered.Tests.Support");

            // Profile "auto" must select "app" (because App is OutputType=Exe) and catch DeadPublicEntity!
            var analyzer = new ArchitectureAnalyzerService();
            var deadAuto = analyzer.AnalyzeDeadCode(snapshot.Graph, NodeKind.Type, profile: "auto");
            Assert.Equal("app", deadAuto.Profile);
            Assert.Contains(deadAuto.Candidates, c => c.NodeId == "Layered.Domain.DeadPublicEntity");
            Assert.DoesNotContain(deadAuto.Candidates, c => c.NodeId == "Layered.Domain.ActiveOrder");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public void Arch04_ImpactExcludesDiRegistrationsAndTestsByDefaultAndCategorizesWhenIncluded()
    {
        var nodes = new List<CodeNode>
        {
            new("App.ISettings", "ISettings", "App.ISettings", NodeKind.Type, "App", "App", TypeKind.Interface),
            new("App.SettingsImpl", "SettingsImpl", "App.SettingsImpl", NodeKind.Type, "App", "App", TypeKind.Class),
            new("App.RealConsumer", "RealConsumer", "App.RealConsumer", NodeKind.Type, "App", "App", TypeKind.Class),
            new("App.ServiceCollectionExtensions", "ServiceCollectionExtensions", "App.ServiceCollectionExtensions", NodeKind.Type, "App", "App", TypeKind.Class, IsStatic: true),
            new("App.Tests.Support.FakeSettings", "FakeSettings", "App.Tests.Support.FakeSettings", NodeKind.Type, "App.Tests.Support", "App.Tests.Support", TypeKind.Class,
                Metadata: new Dictionary<string, string> { ["isTestSupportProject"] = "true" })
        };

        var edges = new List<DependencyEdge>
        {
            new("App.RealConsumer", "App.ISettings", DependencyKind.FieldOrProperty),
            new("App.ServiceCollectionExtensions", "App.ISettings", DependencyKind.GenericArgument),
            new("App.ServiceCollectionExtensions", "App.SettingsImpl", DependencyKind.GenericArgument),
            new("App.Tests.Support.FakeSettings", "App.ISettings", DependencyKind.Implements)
        };

        var diRegs = new List<DiRegistration>
        {
            new("App.ISettings", "App.SettingsImpl", ServiceLifetimeKind.Singleton, "App.ServiceCollectionExtensions", ProjectName: "App")
        };

        var graph = new CodeGraph("C:/repo", nodes, edges, diRegistrations: diRegs);
        var analyzer = new ArchitectureAnalyzerService();

        // Default: excludes DI registration and test-support project
        var defaultImpact = analyzer.AnalyzeImpact(graph, "App.ISettings");
        Assert.NotNull(defaultImpact);
        Assert.Equal(1, defaultImpact.DirectDependentsCount);
        Assert.Single(defaultImpact.ImpactedNodes);
        Assert.Equal("App.RealConsumer", defaultImpact.ImpactedNodes[0].NodeId);
        Assert.Equal(1, defaultImpact.ExcludedRegistrationCount);
        Assert.Equal(1, defaultImpact.ExcludedTestCount);

        // Include registrations & tests: categorized cleanly
        var fullImpact = analyzer.AnalyzeImpact(graph, "App.ISettings", excludeRegistrations: false, includeTests: true);
        Assert.NotNull(fullImpact);
        Assert.Equal(3, fullImpact.DirectDependentsCount);
        Assert.Contains(fullImpact.ImpactedNodes, n => n.NodeId == "App.ServiceCollectionExtensions" && n.Category == "registration");
        Assert.Contains(fullImpact.ImpactedNodes, n => n.NodeId == "App.Tests.Support.FakeSettings" && n.Category == "test");
    }

    [Fact]
    public async Task Arch05_IlBackendStripsStringSwitchHashTreeAndAsyncBoilerplateAndSkipsGeneratedCode()
    {
        var tempDir = CreateTempDir();
        try
        {
            var code = """
                using System.CodeDom.Compiler;
                using System.Threading.Tasks;

                namespace IlParity;

                [GeneratedCode("SomeGen", "1.0")]
                public class GeneratedArtifact { public void Run() { } }

                public class StringSwitchAndAsync
                {
                    public string MapReason(string code) => code switch
                    {
                        "a" => "A",
                        "b" => "B",
                        "c" => "C",
                        "d" => "D",
                        "e" => "E",
                        "f" => "F",
                        "g" => "G",
                        "h" => "H",
                        "i" => "I",
                        "j" => "J",
                        _ => "Other"
                    };

                    public async Task<int> MultiAwaitAsync(int x)
                    {
                        await Task.Yield();
                        await Task.Yield();
                        await Task.Yield();
                        return x > 0 ? x : 0;
                    }
                }
                """;

            var tree = CSharpSyntaxTree.ParseText(code, path: Path.Combine(tempDir, "IlParity.cs"), encoding: System.Text.Encoding.UTF8);
            var refs = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .ToList();
            var compilation = CSharpCompilation.Create("IlParity", [tree], refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var dllPath = Path.Combine(tempDir, "IlParity.dll");
            var emit = compilation.Emit(dllPath);
            Assert.True(emit.Success, string.Join("\n", emit.Diagnostics));

            var ilExtractor = new MetadataCodeGraphExtractor();
            var snapshot = await ilExtractor.ExtractAsync(dllPath, new ExtractionOptions(IncludeMethods: true));

            // GeneratedCodeAttribute type must be excluded by default
            Assert.Null(snapshot.Graph.FindNode("IlParity.GeneratedArtifact"));

            // MultiAwaitAsync has only 1 C# branch (x > 0 ? ... : ... -> CC=2); without stripping async state machine boilerplate it would be 10+
            var awaitMethod = snapshot.Graph.GetNodes(NodeKind.Method).First(m => m.Name == "MultiAwaitAsync");
            Assert.InRange(awaitMethod.CyclomaticComplexity, 2, 5);

            // MapReason has 10 string arms -> CC should be ~11-15, NOT 25-35 from ComputeStringHash binary search double-counting
            var switchMethod = snapshot.Graph.GetNodes(NodeKind.Method).First(m => m.Name == "MapReason");
            Assert.InRange(switchMethod.CyclomaticComplexity, 10, 16);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public void Arch06_Arch07_Arch14_InfrastructureExclusionCyclePathAndExplicitMetricNames()
    {
        var nodes = new List<CodeNode>
        {
            new("MyApp.Migrations.InitialCreate", "InitialCreate", "MyApp.Migrations.InitialCreate", NodeKind.Type, "Infra", "MyApp.Migrations", TypeKind.Class,
                Lcom4: 5, CyclomaticComplexity: 40, Location: new SourceLocation("Migrations/InitialCreate.cs", 1, 1),
                Metadata: new Dictionary<string, string> { ["isMigration"] = "true", ["maxMethodComplexity"] = "40" }),
            new("MyApp.Infra.AppDbContext", "AppDbContext", "MyApp.Infra.AppDbContext", NodeKind.Type, "Infra", "MyApp.Infra", TypeKind.Class,
                Lcom4: 6, Metadata: new Dictionary<string, string> { ["isDbContext"] = "true" }),
            new("MyApp.Domain.Order", "Order", "MyApp.Domain.Order", NodeKind.Type, "Domain", "MyApp.Domain", TypeKind.Class,
                Lcom4: 4, CyclomaticComplexity: 20, CognitiveComplexity: 18,
                Metadata: new Dictionary<string, string> { ["maxMethodComplexity"] = "20", ["maxMethodCognitiveComplexity"] = "18" }),
            new("MyApp.App.Service", "Service", "MyApp.App.Service", NodeKind.Type, "App", "MyApp.App", TypeKind.Class,
                Location: new SourceLocation("App/Service.cs", 10, 1))
        };

        var edges = new List<DependencyEdge>
        {
            new("MyApp.Domain.Order", "MyApp.App.Service", DependencyKind.MethodCall, new SourceLocation("Domain/Order.cs", 15, 1)),
            new("MyApp.App.Service", "MyApp.Domain.Order", DependencyKind.FieldOrProperty, new SourceLocation("App/Service.cs", 22, 1)),
            // Migration cycle (should be ignored by default)
            new("MyApp.Infra.AppDbContext", "MyApp.Migrations.InitialCreate", DependencyKind.TypeReference),
            new("MyApp.Migrations.InitialCreate", "MyApp.Infra.AppDbContext", DependencyKind.TypeReference)
        };

        var graph = new CodeGraph("C:/repo", nodes, edges);
        var analyzer = new ArchitectureAnalyzerService();

        var rules = ArchitecturePresets.CreateRules("clean-architecture", "MyApp");
        var report = analyzer.CheckRules(graph, rules);

        // Migration and DbContext must NOT trigger LCOM4 or cognitive complexity warnings
        Assert.DoesNotContain(report.Violations, v => v.NodeId == "MyApp.Migrations.InitialCreate");
        Assert.DoesNotContain(report.Violations, v => v.NodeId == "MyApp.Infra.AppDbContext");

        // Order triggers CLEAN-006 with explicit metric name (no awkward "(max method)" suffix)
        var cogViolation = report.Violations.FirstOrDefault(v => v.RuleId == "CLEAN-006" && v.NodeId == "MyApp.Domain.Order");
        Assert.NotNull(cogViolation);
        Assert.Contains("max_method_cognitive_complexity", cogViolation.Message);
        Assert.DoesNotContain("(max method)", cogViolation.Message);

        // CLEAN-004 cycle includes actual directed cycle path + representative edges + location, and skips Migration cycle!
        var cycleViolations = report.Violations.Where(v => v.RuleId == "CLEAN-004").ToList();
        Assert.Single(cycleViolations);
        Assert.Contains("MyApp.App -> MyApp.Domain -> MyApp.App", cycleViolations[0].Message);
        Assert.Contains("Edges:", cycleViolations[0].Message);
        Assert.NotNull(cycleViolations[0].Location);
    }

    [Fact]
    public void Arch08_Arch11_AnalyzersMinSeverityAndDegradedProjectExclusionWithSnapshotMetadata()
    {
        var loadStatus = new GraphLoadStatus(
            Backend: "roslyn",
            ProjectsExpected: 2,
            ProjectsLoaded: 1,
            ProjectsDegraded: 1,
            Degraded: true,
            DegradedProjects: ["BrokenProj"],
            DegradedDetails: [new DegradedProjectInfo("BrokenProj", "RESTORE_REQUIRED", "project.assets.json not found")]);

        var nodes = new List<CodeNode>
        {
            new("CleanProj.Big", "Big", "CleanProj.Big", NodeKind.Type, "CleanProj", "CleanProj", TypeKind.Class, Lcom4: 5),
            new("BrokenProj.Incomplete", "Incomplete", "BrokenProj.Incomplete", NodeKind.Type, "BrokenProj", "BrokenProj", TypeKind.Class, Lcom4: 5)
        };

        var diags = new List<AnalyzerDiagnostic>
        {
            new("CA1822", "Mark static", "Performance", RuleSeverity.Info, "Make static", new SourceLocation("Clean.cs", 5, 1), "CleanProj"),
            new("CA2000", "Dispose objects", "Reliability", RuleSeverity.Warning, "Dispose", new SourceLocation("Clean.cs", 9, 1), "CleanProj"),
            new("CA1001", "Disposable field", "Design", RuleSeverity.Error, "Own disposable", new SourceLocation("Broken.cs", 3, 1), "BrokenProj")
        };

        var graph = new CodeGraph("C:/repo", nodes, [], analyzerDiagnostics: diags, loadStatus: loadStatus);
        var analyzer = new ArchitectureAnalyzerService();

        // CheckRules skips degraded project's violation by default and reports skippedDegradedProjects
        var ruleReport = analyzer.CheckRules(graph, [new MaxMetricRule("L-1", "LCOM4", "", RuleSeverity.Warning, "lcom4", 2)]);
        Assert.Single(ruleReport.Violations);
        Assert.Equal("CleanProj.Big", ruleReport.Violations[0].NodeId);
        Assert.Contains("BrokenProj", ruleReport.SkippedDegradedProjects ?? []);

        // GetAnalyzerDiagnostics respects minSeverity and excludes degraded project by default
        var warnDiags = analyzer.GetAnalyzerDiagnostics(graph, categories: ["all"], minSeverity: RuleSeverity.Warning);
        Assert.Single(warnDiags.Diagnostics);
        Assert.Equal("CA2000", warnDiags.Diagnostics[0].Id);

        // MCP formatter includes snapshotId & capturedAt
        var now = DateTimeOffset.UtcNow;
        var formatted = McpResponseFormatter.FormatCheckRules("test", [], [], ruleReport, loadStatus, 0, 50, false, "snap-123", now);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(formatted, McpResponseFormatter.JsonOptions));
        Assert.Equal("snap-123", doc.RootElement.GetProperty("snapshotId").GetString());
        Assert.True(doc.RootElement.TryGetProperty("capturedAt", out _));
        Assert.Equal("BrokenProj", doc.RootElement.GetProperty("skippedDegradedProjects")[0].GetString());
    }

    [Fact]
    public void Arch09_SqlQuerySupportsSchemaInfoTableAndHelpfulErrorHints()
    {
        var graph = new CodeGraph("C:/repo", [new CodeNode("A.Foo", "Foo", "A.Foo", NodeKind.Type, "A", "A", TypeKind.Class)], []);
        var engine = new GraphSqlQueryEngine();

        // 1. schema_info table replaces PRAGMA table_info
        var schemaRes = engine.Execute(graph, "SELECT column_name, column_type FROM schema_info WHERE table_name = 'nodes'");
        Assert.True(schemaRes.RowCount >= 15);
        Assert.Contains(schemaRes.Rows, r => (string?)r["column_name"] == "file_path");

        // 2. 'no such column' returns helpful column list for the queried table
        var colEx = Assert.Throws<InvalidOperationException>(() =>
            engine.Execute(graph, "SELECT location FROM nodes"));
        Assert.Contains("Available columns for 'nodes'", colEx.Message);
        Assert.Contains("file_path", colEx.Message);

        // 3. PRAGMA rejection points to schema_info
        var pragmaEx = Assert.Throws<InvalidOperationException>(() =>
            engine.Execute(graph, "SELECT * FROM pragma_table_info('nodes')"));
        Assert.Contains("schema_info", pragmaEx.Message);
    }

    [Fact]
    public void Arch13_Arch14_DiAnalysisCompactFiltersAndDeadCodeSummaryOnlyOmitsReturnedZero()
    {
        var regs = new List<DiRegistration>
        {
            new("App.ISvcA", "App.SvcA", ServiceLifetimeKind.Singleton, "App.Startup", ProjectName: "App.Core"),
            new("App.ISvcB", "App.SvcB", ServiceLifetimeKind.Scoped, "App.Startup", ProjectName: "App.Web"),
            new("App.ISvcC", "App.SvcC", ServiceLifetimeKind.Transient, "App.Startup", ProjectName: "App.Web")
        };
        var singles = new List<SingleImplementationInterfaceInfo>
        {
            new("App.ISvcA", "App.SvcA", true, 3),
            new("App.IUnreg", "App.UnregImpl", false, 1)
        };
        var diReport = new DiAnalysisReport(regs.Count, 0, singles.Count, regs, [], singles);

        var filteredObj = McpResponseFormatter.FormatDiAnalysis(
            diReport, null, includeRegistrations: true, summaryOnly: false, offset: 0, limit: 20,
            lifetime: "scoped", project: "App.Web", onlyUnregistered: true);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(filteredObj, McpResponseFormatter.JsonOptions)))
        {
            var root = doc.RootElement;
            Assert.Equal(1, root.GetProperty("lifetimeCounts").GetProperty("scoped").GetInt32());
            Assert.Equal(1, root.GetProperty("registrations").GetArrayLength());
            Assert.Equal("App.ISvcB", root.GetProperty("registrations")[0].GetProperty("serviceTypeId").GetString());
            Assert.Equal(1, root.GetProperty("singleImplementationInterfaces").GetArrayLength());
            Assert.Equal("App.IUnreg", root.GetProperty("singleImplementationInterfaces")[0].GetProperty("interfaceTypeId").GetString());
        }

        // DeadCode summaryOnly: must omit returnedCount (never confuse with 0 found)
        var deadReport = new DeadCodeReport(NodeKind.Type, 10, 5, 5, false, ["ProgramMain"], [], Profile: "app");
        var summaryObj = McpResponseFormatter.FormatDeadCode(deadReport, null, 0, 50, summaryOnly: true);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(summaryObj, McpResponseFormatter.JsonOptions)))
        {
            Assert.Equal(5, doc.RootElement.GetProperty("unreferencedCount").GetInt32());
            Assert.Equal("app", doc.RootElement.GetProperty("profile").GetString());
            Assert.False(doc.RootElement.TryGetProperty("returnedCount", out _));
            Assert.False(doc.RootElement.TryGetProperty("returned", out _));
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "archlens-eval-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteFile(string root, string relativePath, string content)
    {
        var full = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
