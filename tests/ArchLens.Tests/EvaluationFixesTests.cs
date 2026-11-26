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

    [Fact]
    public void V07_P0_NoCyclesExcludesParentChildByDefaultAndReportsSuppressedCounts()
    {
        var nodes = new List<CodeNode>
        {
            new("Dream.Ai.Service", "Service", "Dream.Ai.Service", NodeKind.Type, "Dream.Ai", "Dream.Ai", TypeKind.Class),
            new("Dream.Ai.Abstractions.IContract", "IContract", "Dream.Ai.Abstractions.IContract", NodeKind.Type, "Dream.Ai", "Dream.Ai.Abstractions", TypeKind.Interface),
            new("Dream.Ai.Internal.Helper", "Helper", "Dream.Ai.Internal.Helper", NodeKind.Type, "Dream.Ai", "Dream.Ai.Internal", TypeKind.Class),
            new("App.Data.Ctx", "Ctx", "App.Data.Ctx", NodeKind.Type, "App.Data", "App.Data", TypeKind.Class),
            new("App.Data.Migrations.Init", "Init", "App.Data.Migrations.Init", NodeKind.Type, "App.Data", "App.Data.Migrations", TypeKind.Class),
            new("App.Orders.OrderSvc", "OrderSvc", "App.Orders.OrderSvc", NodeKind.Type, "App.Orders", "App.Orders", TypeKind.Class),
            new("App.Billing.InvoiceSvc", "InvoiceSvc", "App.Billing.InvoiceSvc", NodeKind.Type, "App.Billing", "App.Billing", TypeKind.Class)
        };
        var edges = new List<DependencyEdge>
        {
            new("Dream.Ai.Service", "Dream.Ai.Abstractions.IContract", DependencyKind.Implements),
            new("Dream.Ai.Abstractions.IContract", "Dream.Ai.Service", DependencyKind.TypeReference),
            new("Dream.Ai.Service", "Dream.Ai.Internal.Helper", DependencyKind.MethodCall),
            new("Dream.Ai.Internal.Helper", "Dream.Ai.Service", DependencyKind.TypeReference),
            new("App.Data.Ctx", "App.Data.Migrations.Init", DependencyKind.TypeReference),
            new("App.Data.Migrations.Init", "App.Data.Ctx", DependencyKind.TypeReference),
            new("App.Orders.OrderSvc", "App.Billing.InvoiceSvc", DependencyKind.MethodCall),
            new("App.Billing.InvoiceSvc", "App.Orders.OrderSvc", DependencyKind.MethodCall)
        };

        var graph = new CodeGraph("C:/repo", nodes, edges);
        var analyzer = new ArchitectureAnalyzerService();
        var provider = new YamlRuleSetProvider();

        var defaultRules = provider.ParseYaml("""
            version: 1
            rules:
              - id: CYC-1
                type: no_cycles
                scope: namespace
                severity: error
            """);
        var defaultReport = analyzer.CheckRules(graph, defaultRules);

        Assert.Single(defaultReport.Violations);
        Assert.Contains("App.Billing", defaultReport.Violations[0].Message);
        Assert.Contains("App.Orders", defaultReport.Violations[0].Message);
        Assert.NotNull(defaultReport.SuppressedCycles);
        Assert.Equal(1, defaultReport.SuppressedCycles["parent_child"]);
        Assert.Equal(1, defaultReport.SuppressedCycles["migrations"]);

        var strictRules = provider.ParseYaml("""
            version: 1
            rules:
              - id: CYC-1
                type: no_cycles
                scope: namespace
                severity: error
                include_parent_child: true
            """);
        var strictReport = analyzer.CheckRules(graph, strictRules);

        Assert.Equal(2, strictReport.Violations.Count);
        Assert.Contains(strictReport.Violations, v => v.Message.Contains("Dream.Ai.Abstractions"));
        Assert.NotNull(strictReport.SuppressedCycles);
        Assert.False(strictReport.SuppressedCycles.ContainsKey("parent_child"));
        Assert.Equal(1, strictReport.SuppressedCycles["migrations"]);
    }

    [Fact]
    public void V07_P1_AnalyzersReportNoteForZeroAnalyzersAndHiddenSeverityCount()
    {
        var analyzer = new ArchitectureAnalyzerService();

        var zeroGraph = new CodeGraph("C:/repo", [], [], analyzersLoadedCount: 0);
        var zeroReport = analyzer.GetAnalyzerDiagnostics(zeroGraph, minSeverity: RuleSeverity.Warning);
        Assert.Equal(0, zeroReport.AnalyzersLoaded);
        Assert.Equal(0, zeroReport.TotalDiagnostics);
        Assert.NotNull(zeroReport.Note);
        Assert.Contains("No Roslyn analyzer references", zeroReport.Note);

        var diags = new List<AnalyzerDiagnostic>
        {
            new("CA1822", "Mark static", "Performance", RuleSeverity.Info, "Make static", new SourceLocation("A.cs", 1, 1), "App"),
            new("CA1825", "Avoid array alloc", "Performance", RuleSeverity.Info, "Use Array.Empty", new SourceLocation("B.cs", 2, 1), "App"),
            new("CA2000", "Dispose objects", "Design", RuleSeverity.Warning, "Dispose", new SourceLocation("C.cs", 3, 1), "App")
        };
        var loadedGraph = new CodeGraph("C:/repo", [], [], analyzerDiagnostics: diags, analyzersLoadedCount: 4);
        var warnReport = analyzer.GetAnalyzerDiagnostics(loadedGraph, categories: ["all"], minSeverity: RuleSeverity.Warning);

        Assert.Equal(4, warnReport.AnalyzersLoaded);
        Assert.Equal(1, warnReport.TotalDiagnostics);
        Assert.Equal(2, warnReport.HiddenBySeverityCount);
        Assert.NotNull(warnReport.Note);
        Assert.Contains("2 lower-severity diagnostic(s) hidden", warnReport.Note);
    }

    [Fact]
    public void V07_P1_GodClassRuleAndHotspotsCatchCohesiveGodClasses()
    {
        var nodes = new List<CodeNode>
        {
            new("App.ShellHost", "ShellHost", "App.ShellHost", NodeKind.Type, "App", "App", TypeKind.Class,
                LineCount: 2152, CyclomaticComplexity: 382, Lcom4: 1),
            new("App.SmallHelper", "SmallHelper", "App.SmallHelper", NodeKind.Type, "App", "App", TypeKind.Class,
                LineCount: 40, CyclomaticComplexity: 5, Lcom4: 1)
        };

        var edges = new List<DependencyEdge>();
        for (var i = 0; i < 25; i++)
        {
            var depId = $"App.Dep{i}";
            nodes.Add(new CodeNode(depId, $"Dep{i}", depId, NodeKind.Type, "App", "App", TypeKind.Class, LineCount: 10));
            edges.Add(new DependencyEdge("App.ShellHost", depId, DependencyKind.TypeReference));
        }

        var graph = new CodeGraph("C:/repo", nodes, edges);
        var analyzer = new ArchitectureAnalyzerService();
        var provider = new YamlRuleSetProvider();

        var rules = provider.ParseYaml("""
            version: 1
            rules:
              - id: GOD-1
                type: god_class
                severity: warning
                max_lines: 500
                max_complexity: 50
                max_coupling: 20
                min_conditions: 3
            """);

        var report = analyzer.CheckRules(graph, rules);
        Assert.Single(report.Violations);
        Assert.Equal("App.ShellHost", report.Violations[0].NodeId);
        Assert.Contains("LOC=2152", report.Violations[0].Message);

        var hotspots = analyzer.GetHotspots(graph, "god_class", topN: 5);
        Assert.Equal("App.ShellHost", hotspots.Items[0].NodeId);
    }

    [Fact]
    public void V07_P2_DeadCodeAcknowledgementsAndCustomLayersAndIncludeEfFlag()
    {
        var provider = new YamlRuleSetProvider();
        var analyzer = new ArchitectureAnalyzerService();

        var nodes = new List<CodeNode>
        {
            new("MyApp.Core.Order", "Order", "MyApp.Core.Order", NodeKind.Type, "MyApp.Core", "MyApp.Core", TypeKind.Class, AccessibilityKind.Internal,
                Metadata: new Dictionary<string, string> { ["outputKind"] = "DynamicallyLinkedLibrary" }),
            new("MyApp.Data.OrderRepo", "OrderRepo", "MyApp.Data.OrderRepo", NodeKind.Type, "MyApp.Data", "MyApp.Data", TypeKind.Class, AccessibilityKind.Internal,
                Metadata: new Dictionary<string, string> { ["outputKind"] = "DynamicallyLinkedLibrary" }),
            new("MyApp.Data.Migrations.Init", "Init", "MyApp.Data.Migrations.Init", NodeKind.Type, "MyApp.Data", "MyApp.Data.Migrations", TypeKind.Class,
                LineCount: 800, CyclomaticComplexity: 10,
                Metadata: new Dictionary<string, string> { ["isMigration"] = "true" })
        };
        var edges = new List<DependencyEdge>
        {
            new("MyApp.Core.Order", "MyApp.Data.OrderRepo", DependencyKind.TypeReference)
        };
        var graph = new CodeGraph("C:/repo", nodes, edges);

        var rules = provider.ParseYaml("""
            version: 1
            preset: clean-architecture
            root_namespace: "MyApp"
            layers:
              domain: ["MyApp.Core.*"]
              infrastructure: ["MyApp.Data.*"]
            acknowledge:
              - symbol: "MyApp.Core.Order"
                reason: "Invoked via reflection"
            rules:
              - id: DEAD-1
                type: no_dead_code
                scope: type
                profile: app
                severity: warning
              - id: METRIC-EF
                type: max_metric
                metric: line_count
                threshold: 500
                include_ef_and_generated: true
                severity: warning
            """);

        var report = analyzer.CheckRules(graph, rules);
        Assert.Contains(report.Violations, v => v.RuleId == "CLEAN-001" && v.NodeId == "MyApp.Core.Order" && v.TargetNodeId == "MyApp.Data.OrderRepo");
        Assert.DoesNotContain(report.Violations, v => v.RuleId == "DEAD-1" && v.NodeId == "MyApp.Core.Order");
        Assert.Contains(report.Violations, v => v.RuleId == "METRIC-EF" && v.NodeId == "MyApp.Data.Migrations.Init");

        var deadReport = analyzer.AnalyzeDeadCode(
            graph,
            profile: "app",
            acknowledged: [new DeadCodeAcknowledgement("MyApp.Core.Order", "Invoked via reflection")]);
        Assert.Equal(1, deadReport.AcknowledgedCount);
        Assert.DoesNotContain(deadReport.Candidates, c => c.NodeId == "MyApp.Core.Order");
    }

    [Fact]
    public async Task V07_P2_ImpactAutoSummarizesHighFanInSymbolsInMcp()
    {
        var nodes = new List<CodeNode>
        {
            new("App.Core.IChatRepository", "IChatRepository", "App.Core.IChatRepository", NodeKind.Type, "App.Core", "App.Core", TypeKind.Interface)
        };
        var edges = new List<DependencyEdge>();
        for (var i = 0; i < 25; i++)
        {
            var consumerId = $"App.Features.Consumer{i}";
            nodes.Add(new CodeNode(consumerId, $"Consumer{i}", consumerId, NodeKind.Type, "App.Features", "App.Features", TypeKind.Class));
            edges.Add(new DependencyEdge(consumerId, "App.Core.IChatRepository", DependencyKind.FieldOrProperty));
        }

        var graph = new CodeGraph("C:/repo", nodes, edges);
        var stubExtractor = new StubGraphExtractor(graph);
        using var sessions = new WorkspaceSessionManager([stubExtractor]);
        var tools = new ArchLensTools(sessions, new ArchitectureAnalyzerService(), new GraphSqlQueryEngine(), [new YamlRuleSetProvider()]);

        var targetPath = Directory.GetCurrentDirectory();
        var autoRes = await tools.AnalyzeImpactAsync(targetPath, "IChatRepository");
        Assert.NotEqual(true, autoRes.IsError);
        var autoJson = ((ModelContextProtocol.Protocol.TextContentBlock)autoRes.Content[0]).Text;
        using (var doc = JsonDocument.Parse(autoJson))
        {
            Assert.True(doc.RootElement.GetProperty("autoSummarized").GetBoolean());
            Assert.Equal(25, doc.RootElement.GetProperty("totalTransitiveDependentsCount").GetInt32());
            Assert.False(doc.RootElement.TryGetProperty("impactedNodes", out _));
            Assert.Contains("summaryOnly: false", doc.RootElement.GetProperty("note").GetString());
        }

        var fullRes = await tools.AnalyzeImpactAsync(targetPath, "IChatRepository", summaryOnly: false);
        Assert.NotEqual(true, fullRes.IsError);
        var fullJson = ((ModelContextProtocol.Protocol.TextContentBlock)fullRes.Content[0]).Text;
        using (var doc = JsonDocument.Parse(fullJson))
        {
            Assert.False(doc.RootElement.TryGetProperty("autoSummarized", out _));
            Assert.Equal(25, doc.RootElement.GetProperty("impactedNodes").GetArrayLength());
        }
    }

    [Fact]
    public void GodClassRuleOnlyMarksExceededThresholds()
    {
        var nodes = new List<CodeNode>
        {
            new("App.PartialGod", "PartialGod", "App.PartialGod", NodeKind.Type, "App", "App", TypeKind.Class,
                LineCount: 427, CyclomaticComplexity: 85, Lcom4: 1)
        };
        var edges = new List<DependencyEdge>();
        for (var i = 0; i < 25; i++)
        {
            var depId = $"App.Dep{i}";
            nodes.Add(new CodeNode(depId, $"Dep{i}", depId, NodeKind.Type, "App", "App", TypeKind.Class, LineCount: 10));
            edges.Add(new DependencyEdge("App.PartialGod", depId, DependencyKind.TypeReference));
        }

        var graph = new CodeGraph("C:/repo", nodes, edges);
        var analyzer = new ArchitectureAnalyzerService();
        var rule = new GodClassRule("GOD-1", "No god classes", "desc", RuleSeverity.Warning,
            maxLines: 500, maxComplexity: 50, maxCoupling: 20, minConditions: 2);

        var report = analyzer.CheckRules(graph, [rule]);
        Assert.Single(report.Violations);
        var msg = report.Violations[0].Message;
        Assert.Contains("LOC=427", msg);
        Assert.DoesNotContain("LOC=427 (>500)", msg);
        Assert.Contains("total CC=85 (>50)", msg);
        Assert.Contains("Ce=25 (>20)", msg);
    }

    [Fact]
    public void OverviewAlignsNamespaceCycleCountWithCheckRulesSuppression()
    {
        var nodes = new List<CodeNode>
        {
            new("Dream.Ai.Orchestrator", "Orchestrator", "Dream.Ai.Orchestrator", NodeKind.Type, "Dream.Ai", "Dream.Ai", TypeKind.Class),
            new("Dream.Ai.Abstractions.IModel", "IModel", "Dream.Ai.Abstractions.IModel", NodeKind.Type, "Dream.Ai", "Dream.Ai.Abstractions", TypeKind.Interface),
            new("App.Data.Ctx", "Ctx", "App.Data.Ctx", NodeKind.Type, "App.Data", "App.Data", TypeKind.Class),
            new("App.Data.Migrations.Init", "Init", "App.Data.Migrations.Init", NodeKind.Type, "App.Data", "App.Data.Migrations", TypeKind.Class),
            new("App.Orders.OrderSvc", "OrderSvc", "App.Orders.OrderSvc", NodeKind.Type, "App", "App.Orders", TypeKind.Class),
            new("App.Billing.InvoiceSvc", "InvoiceSvc", "App.Billing.InvoiceSvc", NodeKind.Type, "App", "App.Billing", TypeKind.Class)
        };
        var edges = new List<DependencyEdge>
        {
            new("Dream.Ai.Orchestrator", "Dream.Ai.Abstractions.IModel", DependencyKind.TypeReference),
            new("Dream.Ai.Abstractions.IModel", "Dream.Ai.Orchestrator", DependencyKind.TypeReference),
            new("App.Data.Ctx", "App.Data.Migrations.Init", DependencyKind.TypeReference),
            new("App.Data.Migrations.Init", "App.Data.Ctx", DependencyKind.TypeReference),
            new("App.Orders.OrderSvc", "App.Billing.InvoiceSvc", DependencyKind.MethodCall),
            new("App.Billing.InvoiceSvc", "App.Orders.OrderSvc", DependencyKind.MethodCall)
        };

        var graph = new CodeGraph("C:/repo", nodes, edges);
        var snapshot = new ArchitectureSnapshot("snap-test", "C:/repo", DateTimeOffset.UtcNow, graph);
        var analyzer = new ArchitectureAnalyzerService();

        var overview = analyzer.GetOverview(snapshot);
        Assert.Equal(1, overview.NamespaceCycleCount);
        Assert.Equal(3, overview.NamespaceCycleCountRaw);
        Assert.NotNull(overview.SuppressedNamespaceCycles);
        Assert.Equal(1, overview.SuppressedNamespaceCycles["parent_child"]);
        Assert.Equal(1, overview.SuppressedNamespaceCycles["migrations"]);

        var formatted = McpResponseFormatter.FormatOverview(overview, summaryOnly: true, includePackages: false, includeFullDiagnostics: false);
        var json = JsonSerializer.Serialize(formatted, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(1, doc.RootElement.GetProperty("namespaceCycleCount").GetInt32());
        Assert.Equal(3, doc.RootElement.GetProperty("namespaceCycleCountRaw").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("suppressedNamespaceCycles").GetProperty("parent_child").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("suppressedNamespaceCycles").GetProperty("migrations").GetInt32());

        // Without suppressed cycles, raw and suppressed fields stay null/omitted
        var cleanGraph = new CodeGraph("C:/repo", [nodes[4], nodes[5]], [edges[4], edges[5]]);
        var cleanOverview = analyzer.GetOverview(new ArchitectureSnapshot("snap-clean", "C:/repo", DateTimeOffset.UtcNow, cleanGraph));
        Assert.Equal(1, cleanOverview.NamespaceCycleCount);
        Assert.Null(cleanOverview.NamespaceCycleCountRaw);
        Assert.Null(cleanOverview.SuppressedNamespaceCycles);
    }

    [Fact]
    public async Task Round1Fixes_ValidationAmbiguitySuppressionAndDeadCodeWorkAsExpected()
    {
        var nodes = new List<CodeNode>
        {
            new("App.Cli.Program", "Program", "App.Cli.Program", NodeKind.Type, "App.Cli", "App.Cli", TypeKind.Class,
                Metadata: new Dictionary<string, string> { ["outputKind"] = "ConsoleApplication", ["isEntryPointMain"] = "true" }),
            new("App.Mcp.Program", "Program", "App.Mcp.Program", NodeKind.Type, "App.Mcp", "App.Mcp", TypeKind.Class,
                Metadata: new Dictionary<string, string> { ["outputKind"] = "ConsoleApplication", ["isEntryPointMain"] = "true" }),
            new("App.Data.UserConfig", "UserConfig", "App.Data.UserConfig", NodeKind.Type, "App.Data", "App.Data", TypeKind.Class, AccessibilityKind.Internal,
                Metadata: new Dictionary<string, string> { ["baseTypes"] = "Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<User>" }),
            new("App.Data.Factory", "Factory", "App.Data.Factory", NodeKind.Type, "App.Data", "App.Data", TypeKind.Class, AccessibilityKind.Public,
                Metadata: new Dictionary<string, string> { ["baseTypes"] = "Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<AppDbContext>" }),
            new("App.Web.AvatarViewComponent", "AvatarViewComponent", "App.Web.AvatarViewComponent", NodeKind.Type, "App.Web", "App.Web", TypeKind.Class, AccessibilityKind.Public,
                Metadata: new Dictionary<string, string> { ["baseTypes"] = "Microsoft.AspNetCore.Mvc.ViewComponent" }),
            new("App.Data.Migrations.Init", "Init", "App.Data.Migrations.Init", NodeKind.Type, "App.Data", "App.Data.Migrations", TypeKind.Class, AccessibilityKind.Public,
                Metadata: new Dictionary<string, string> { ["isMigration"] = "true" }),
            new("App.Data.DbCtx", "DbCtx", "App.Data.DbCtx", NodeKind.Type, "App.Data", "App.Data", TypeKind.Class),
            new("App.Orders.OrderSvc", "OrderSvc", "App.Orders.OrderSvc", NodeKind.Type, "App", "App.Orders", TypeKind.Class),
            new("App.Billing.InvoiceSvc", "InvoiceSvc", "App.Billing.InvoiceSvc", NodeKind.Type, "App", "App.Billing", TypeKind.Class)
        };
        var edges = new List<DependencyEdge>
        {
            new("App.Data.DbCtx", "App.Data.Migrations.Init", DependencyKind.TypeReference),
            new("App.Data.Migrations.Init", "App.Data.DbCtx", DependencyKind.TypeReference),
            new("App.Orders.OrderSvc", "App.Billing.InvoiceSvc", DependencyKind.MethodCall),
            new("App.Billing.InvoiceSvc", "App.Orders.OrderSvc", DependencyKind.MethodCall)
        };

        var graph = new CodeGraph("C:/repo", nodes, edges);
        var analyzer = new ArchitectureAnalyzerService();

        // 1. Dead code skips EF Core configurations, design-time factories, migrations, and ViewComponents
        var deadReport = analyzer.AnalyzeDeadCode(graph, NodeKind.Type, profile: "app");
        Assert.DoesNotContain(deadReport.Candidates, c => c.FullName == "App.Data.UserConfig");
        Assert.DoesNotContain(deadReport.Candidates, c => c.FullName == "App.Data.Factory");
        Assert.DoesNotContain(deadReport.Candidates, c => c.FullName == "App.Web.AvatarViewComponent");
        Assert.DoesNotContain(deadReport.Candidates, c => c.FullName == "App.Data.Migrations.Init");

        // 2. Invalid dead_code profile throws ArgumentException
        Assert.Throws<ArgumentException>(() => analyzer.AnalyzeDeadCode(graph, NodeKind.Type, profile: "bogus"));

        // 3. Hotspots rejects invalid sortBy and unsupported distance@type
        Assert.Throws<ArgumentException>(() => analyzer.GetHotspots(graph, sortBy: "distance", scope: NodeKind.Type));
        Assert.Throws<ArgumentException>(() => analyzer.GetHotspots(graph, sortBy: "nonexistent_metric", scope: NodeKind.Type));
        Assert.Throws<ArgumentException>(() => analyzer.GetHotspots(graph, sortBy: "lcom4", scope: NodeKind.Method));

        // 4. Dependency graph scopes cycles to filter and suppresses default noise cycles
        var filteredNone = analyzer.GetDependencyGraph(graph, NodeKind.Namespace, filterPattern: "NoSuch.*");
        Assert.Equal(0, filteredNone.TotalCycleCount);
        Assert.Empty(filteredNone.Cycles);

        var depGraph = analyzer.GetDependencyGraph(graph, NodeKind.Namespace);
        Assert.Equal(1, depGraph.TotalCycleCount);
        Assert.NotNull(depGraph.SuppressedCycles);
        Assert.Equal(1, depGraph.SuppressedCycles["migrations"]);

        // 5. Ambiguous short name detection in MCP inspect_symbol and impact
        var tempDir = CreateTempDir();
        try
        {
            using var sessions = new WorkspaceSessionManager([new StubGraphExtractor(graph)]);
            var tools = new ArchLensTools(sessions, analyzer, new GraphSqlQueryEngine(), [new YamlRuleSetProvider()]);

            var ambigInspect = await tools.InspectSymbolAsync(tempDir, "Program");
            Assert.True(ambigInspect.IsError);
            var ambigText = ((ModelContextProtocol.Protocol.TextContentBlock)ambigInspect.Content[0]).Text;
            using (var doc = JsonDocument.Parse(ambigText))
            {
                Assert.Equal("ambiguous_symbol", doc.RootElement.GetProperty("error").GetString());
                Assert.Equal(2, doc.RootElement.GetProperty("candidates").GetArrayLength());
            }

            // 6. Strict MCP validation for scope, format, lifetime, fields
            var badScope = await tools.GetDependencyGraphAsync(tempDir, scope: "assembly");
            Assert.True(badScope.IsError);
            var badFormat = await tools.GetDependencyGraphAsync(tempDir, format: "xml");
            Assert.True(badFormat.IsError);
            var badLifetime = await tools.AnalyzeDiAsync(tempDir, lifetime: "bogus");
            Assert.True(badLifetime.IsError);
            var badFields = await tools.GetHotspotsAsync(tempDir, fields: "nope");
            Assert.True(badFields.IsError);

            // 7. check_rules warns when rootNamespace matches 0 types and includes evaluatedRules
            var badRootRes = await tools.CheckRulesAsync(tempDir, preset: "clean-architecture", rootNamespace: "Nope", summaryOnly: true);
            Assert.NotEqual(true, badRootRes.IsError);
            var badRootJson = ((ModelContextProtocol.Protocol.TextContentBlock)badRootRes.Content[0]).Text;
            using (var doc = JsonDocument.Parse(badRootJson))
            {
                Assert.Contains("matched 0 types", doc.RootElement.GetProperty("note").GetString());
                Assert.True(doc.RootElement.GetProperty("evaluatedRules").GetArrayLength() > 0);
            }

            // 8. SQL syntax error maps to invalid_argument, not internal_error
            var badSqlRes = await tools.QueryGraphSqlAsync(tempDir, sql: "SELECT FROM WHERE");
            Assert.True(badSqlRes.IsError);
            var badSqlJson = ((ModelContextProtocol.Protocol.TextContentBlock)badSqlRes.Content[0]).Text;
            using (var doc = JsonDocument.Parse(badSqlJson))
            {
                Assert.Equal("invalid_argument", doc.RootElement.GetProperty("error").GetString());
            }
        }
        finally
        {
            TryDeleteDir(tempDir);
        }

        // 9. Malformed YAML throws InvalidDataException with line/column
        var yamlProvider = new YamlRuleSetProvider();
        var ex = Assert.Throws<InvalidDataException>(() => yamlProvider.ParseYaml("version: 1\nrules:\n  - id: X\n   from: A"));
        Assert.Contains("line", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Round2Fixes_DegradedSymbolHintCommaPresetsAndConcurrentExtractionSerialization()
    {
        var degradedStatus = new GraphLoadStatus("roslyn", 1, 0, 1, Degraded: true, Approximate: false, Note: "load failed");
        var degradedGraph = new CodeGraph("C:/repo", [], [], loadStatus: degradedStatus);
        var tempDir = CreateTempDir();
        try
        {
            var analyzer = new ArchitectureAnalyzerService();
            using var sessions = new WorkspaceSessionManager([new StubGraphExtractor(degradedGraph)]);
            var tools = new ArchLensTools(sessions, analyzer, new GraphSqlQueryEngine(), [new YamlRuleSetProvider()]);

            // 1. symbol_not_found includes degraded-load notice
            var missingRes = await tools.InspectSymbolAsync(tempDir, "MainWindow");
            Assert.True(missingRes.IsError);
            var missingJson = ((ModelContextProtocol.Protocol.TextContentBlock)missingRes.Content[0]).Text;
            using (var doc = JsonDocument.Parse(missingJson))
            {
                Assert.Contains("degraded mode", doc.RootElement.GetProperty("hint").GetString(), StringComparison.OrdinalIgnoreCase);
            }

            // 2. Comma-separated presets combine and canonicalize rulesSource
            var multiPresetRes = await tools.CheckRulesAsync(tempDir, preset: "onion,ddd", summaryOnly: true);
            Assert.NotEqual(true, multiPresetRes.IsError);
            var multiJson = ((ModelContextProtocol.Protocol.TextContentBlock)multiPresetRes.Content[0]).Text;
            using (var doc = JsonDocument.Parse(multiJson))
            {
                Assert.Equal("preset:clean-architecture + preset:ddd", doc.RootElement.GetProperty("rulesSource").GetString());
            }

            // 3. Concurrent extractions on same target with different ExtractionOptions are serialized (max 1 concurrent)
            var tracker = new ConcurrencyTrackingExtractor(degradedGraph);
            using var concSessions = new WorkspaceSessionManager([tracker]);
            var t1 = concSessions.GetOrLoadAsync(tempDir, forceRefresh: true, new ExtractionOptions(IncludeMethods: false));
            var t2 = concSessions.GetOrLoadAsync(tempDir, forceRefresh: true, new ExtractionOptions(IncludeMethods: true));
            await Task.WhenAll(t1, t2);
            Assert.Equal(1, tracker.MaxConcurrent);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task FeedbackV011_DeadCodeMethodScopeSkipsDefaultAndDtoRecordConstructors()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "DtoApp.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            WriteFile(tempDir, "Models.cs", """
                using System.Text.Json;
                using System.Text.Json.Serialization;

                namespace DtoApp;

                public sealed record DeviceResponse(string Id, string Name);

                public abstract record StreamEvent
                {
                    private StreamEvent() { }
                    public sealed record Started(string Id) : StreamEvent;
                }

                public sealed class CommandDto
                {
                    public string Text { get; init; } = "";
                    public CommandDto() { }
                    public CommandDto(string text) { Text = text; }
                }

                public sealed class ServiceWithDeadHelper
                {
                    public void Run()
                    {
                        var dto = JsonSerializer.Deserialize<DeviceResponse>("{}");
                        var cmd = new CommandDto { Text = dto?.Id ?? "" };
                        StreamEvent ev = new StreamEvent.Started(cmd.Text);
                        Console.WriteLine(ev);
                    }

                    public void TrulyUnusedHelperMethod() => Console.WriteLine("dead");
                }
                """);

            WriteFile(tempDir, "Program.cs", """
                new DtoApp.ServiceWithDeadHelper().Run();
                """);

            using var extractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snapshot = await extractor.ExtractAsync(Path.Combine(tempDir, "DtoApp.csproj"), new ExtractionOptions(IncludeMethods: true));
            var analyzer = new ArchitectureAnalyzerService();

            var report = analyzer.AnalyzeDeadCode(snapshot.Graph, NodeKind.Method, profile: "app");

            Assert.DoesNotContain(report.Candidates, c => c.FullName.Contains("DeviceResponse.DeviceResponse("));
            Assert.DoesNotContain(report.Candidates, c => c.FullName.Contains("StreamEvent.StreamEvent()"));
            Assert.DoesNotContain(report.Candidates, c => c.FullName.Contains("CommandDto.CommandDto()"));
            Assert.Contains(report.Candidates, c => c.FullName.Contains("TrulyUnusedHelperMethod"));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task FeedbackV011_IlBackendFailsLoudlyOnSingleFileOrNonManagedBinaries()
    {
        var tempDir = CreateTempDir();
        try
        {
            // Simulate a single-file publish folder with a native .exe and non-managed native .dlls (no CLR metadata)
            File.WriteAllBytes(Path.Combine(tempDir, "SingleFileApp.exe"), [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00]);
            File.WriteAllBytes(Path.Combine(tempDir, "native1.dll"), [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00]);
            File.WriteAllBytes(Path.Combine(tempDir, "native2.dll"), [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00]);

            var ilExtractor = new MetadataCodeGraphExtractor();
            var ex = await Assert.ThrowsAsync<InvalidDataException>(() => ilExtractor.ExtractAsync(tempDir));
            Assert.Contains("0 managed .NET assemblies", ex.Message);
            Assert.Contains("PublishSingleFile", ex.Message);
            Assert.Contains("SingleFileApp.exe", ex.Message);

            // Also verify directory with only .exe and .pdb (no .dlls at all)
            var exeOnlyDir = Path.Combine(tempDir, "exe-only");
            Directory.CreateDirectory(exeOnlyDir);
            File.WriteAllBytes(Path.Combine(exeOnlyDir, "MyTool.exe"), [0x4D, 0x5A, 0x90, 0x00]);
            File.WriteAllBytes(Path.Combine(exeOnlyDir, "MyTool.pdb"), [0x42, 0x53, 0x4A, 0x42]);
            var ex2 = await Assert.ThrowsAsync<InvalidDataException>(() => ilExtractor.ExtractAsync(exeOnlyDir));
            Assert.Contains("MyTool.exe", ex2.Message);
            Assert.Contains("PublishSingleFile", ex2.Message);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public void FeedbackV011_ForbidDependencyExcludesSelfTargetSubtreeAndSupportsProjectSelectors()
    {
        var nodes = new List<CodeNode>
        {
            new("Contoso.Web.HomeController", "HomeController", "Contoso.Web.HomeController", NodeKind.Type, "Contoso.Web", "Contoso.Web", TypeKind.Class),
            new("Contoso.Ai.AiClient", "AiClient", "Contoso.Ai.AiClient", NodeKind.Type, "Contoso.Ai", "Contoso.Ai", TypeKind.Class),
            new("Contoso.Ai.Model.Prompt", "Prompt", "Contoso.Ai.Model.Prompt", NodeKind.Type, "Contoso.Ai", "Contoso.Ai.Model", TypeKind.Class),
            new("Contoso.Ai.Dto.Response", "Response", "Contoso.Ai.Dto.Response", NodeKind.Type, "Contoso.Ai", "Contoso.Ai.Dto", TypeKind.Class),
            new("Contoso.Data.Repo", "Repo", "Contoso.Data.Repo", NodeKind.Type, "Contoso.Data", "Contoso.Data", TypeKind.Class)
        };
        var edges = new List<DependencyEdge>
        {
            new("Contoso.Web.HomeController", "Contoso.Ai.AiClient", DependencyKind.MethodCall),
            new("Contoso.Ai.AiClient", "Contoso.Ai.Model.Prompt", DependencyKind.Parameter),
            new("Contoso.Ai.Model.Prompt", "Contoso.Ai.Dto.Response", DependencyKind.FieldOrProperty),
            new("Contoso.Ai.AiClient", "Contoso.Data.Repo", DependencyKind.MethodCall)
        };

        var graph = new CodeGraph("C:/repo", nodes, edges);
        var provider = new YamlRuleSetProvider();
        var analyzer = new ArchitectureAnalyzerService();

        var rules = provider.ParseYaml("""
            version: 1
            rules:
              - id: PROBE-001
                type: forbid_dependency
                severity: error
                from: "Contoso.*"
                to: ["Contoso.Ai.*"]
              - id: PROJ-001
                type: forbid_dependency
                severity: error
                from_project: "Contoso.Ai"
                to_project: "Contoso.Data"
            """);

        var report = analyzer.CheckRules(graph, rules);
        var probeViolations = report.Violations.Where(v => v.RuleId == "PROBE-001").ToList();
        Assert.Single(probeViolations);
        Assert.Equal("Contoso.Web.HomeController", probeViolations[0].NodeId);
        Assert.Equal("Contoso.Ai.AiClient", probeViolations[0].TargetNodeId);

        var projViolations = report.Violations.Where(v => v.RuleId == "PROJ-001").ToList();
        Assert.Single(projViolations);
        Assert.Equal("Contoso.Ai.AiClient", projViolations[0].NodeId);
        Assert.Equal("Contoso.Data.Repo", projViolations[0].TargetNodeId);
    }

    [Fact]
    public void FeedbackV011_VacuousRootNamespaceProducesConfigErrorInReportAndSarif()
    {
        var nodes = new List<CodeNode>
        {
            new("RealApp.Domain.Order", "Order", "RealApp.Domain.Order", NodeKind.Type, "RealApp.Domain", "RealApp.Domain", TypeKind.Class),
            new("RealApp.Web.OrdersController", "OrdersController", "RealApp.Web.OrdersController", NodeKind.Type, "RealApp.Web", "RealApp.Web", TypeKind.Class)
        };
        var graph = new CodeGraph("C:/repo", nodes, []);
        var analyzer = new ArchitectureAnalyzerService();
        var rules = ArchitecturePresets.CreateRules("clean-architecture", "WrongRoot");

        var report = analyzer.CheckRules(graph, rules, rootNamespace: "WrongRoot");
        Assert.True(report.VacuousConfiguration);
        Assert.Equal(1, report.ErrorCount);
        Assert.Contains(report.Violations, v => v.RuleId == "ARCHLENS-CONFIG-001" && v.Severity == RuleSeverity.Error);
        Assert.Contains("matched 0 types", report.Note);
        Assert.Contains("RealApp", report.Note);

        var sarif = SarifExporter.Export(report.Violations);
        using var doc = JsonDocument.Parse(sarif);
        var results = doc.RootElement.GetProperty("runs")[0].GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal("ARCHLENS-CONFIG-001", results[0].GetProperty("ruleId").GetString());
        Assert.Equal("error", results[0].GetProperty("level").GetString());
    }

    [Fact]
    public void FeedbackV011_Lcom4TreatsStatelessAdaptersAndPropertyBridgedViewModelsAsCohesive()
    {
        var code = """
            namespace Demo;

            public static class StaticHelper
            {
                public static string A(string s) => s;
                public static bool B(string s) => s.Length > 0;
                public static int C(string s) => s.Length;
            }

            public sealed class StatelessAdapter
            {
                public string First(string s) => StaticHelper.A(s);
                public bool Second(string s) => StaticHelper.B(s);
                public int Third(string s) => StaticHelper.C(s);
            }

            public abstract class BaseVm(string args, string result)
            {
                public string Arguments { get; } = args;
                public string Result { get; } = result;
            }

            public sealed class DerivedToolVm(string args, string result) : BaseVm(args, result)
            {
                public string? LinkedId { get; set; }
                public string? Task => ReadArg("task");
                public string? EffectiveId => LinkedId ?? ReadRes("id") ?? ReadArg("id");
                public string? EffectiveTitle => LinkedId ?? BuildTitle();

                public void Link(string id) => LinkedId = id;
                private string? BuildTitle() => Task?.Trim();
                private string? ReadArg(string k) => Arguments.Contains(k) ? Arguments : null;
                private string? ReadRes(string k) => Result.Contains(k) ? Result : null;
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(code);
        var refs = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var compilation = CSharpCompilation.Create("Lcom4FeedbackTest", [tree], refs);
        var semanticModel = compilation.GetSemanticModel(tree);

        int Calc(string typeName) =>
            Lcom4Calculator.Calculate(compilation.GetTypeByMetadataName($"Demo.{typeName}")!, _ => semanticModel);

        Assert.Equal(1, Calc("StatelessAdapter"));
        Assert.Equal(1, Calc("DerivedToolVm"));
    }

    [Fact]
    public async Task DeepSeekR1_XamlHandlersRazorMethodCallsKeyedDiTryAddAndCycleSiblingDetection()
    {
        // 1. Stateless adapter with expression-bodied constant properties -> LCOM4 = 1
        var lcomCode = """
            namespace Demo;
            public sealed class TicTacToeGame
            {
                public const string GameId = "ttt";
                public string Id => GameId;
                public int ActionCount => 9;
                public string First(int a) => a.ToString();
                public int Second(int a) => a + 1;
                public bool Third(int a) => a > 0;
            }
            """;
        var lcomTree = CSharpSyntaxTree.ParseText(lcomCode);
        var refs = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var lcomComp = CSharpCompilation.Create("StatelessPropTest", [lcomTree], refs);
        Assert.Equal(1, Lcom4Calculator.Calculate(lcomComp.GetTypeByMetadataName("Demo.TicTacToeGame")!, _ => lcomComp.GetSemanticModel(lcomTree)));

        // 2. Parent-child cycle suppression does NOT suppress SCCs containing sibling-to-sibling edges
        var cycleNodes = new List<CodeNode>
        {
            new("App.Shell.Host", "Host", "App.Shell.Host", NodeKind.Type, "App", "App.Shell", TypeKind.Class),
            new("App.Shell.Screens.MainScreen", "MainScreen", "App.Shell.Screens.MainScreen", NodeKind.Type, "App", "App.Shell.Screens", TypeKind.Class),
            new("App.Shell.ViewModels.MainVm", "MainVm", "App.Shell.ViewModels.MainVm", NodeKind.Type, "App", "App.Shell.ViewModels", TypeKind.Class)
        };
        var cycleEdges = new List<DependencyEdge>
        {
            new("App.Shell.Host", "App.Shell.Screens.MainScreen", DependencyKind.MethodCall),
            new("App.Shell.Screens.MainScreen", "App.Shell.ViewModels.MainVm", DependencyKind.FieldOrProperty),
            new("App.Shell.ViewModels.MainVm", "App.Shell.Host", DependencyKind.TypeReference)
        };
        var cycleGraph = new CodeGraph("C:/repo", cycleNodes, cycleEdges);
        var analyzer = new ArchitectureAnalyzerService();
        var cycleReport = analyzer.CheckRules(cycleGraph, [new NoCyclesRule("NC-1", "No cycles", "", RuleSeverity.Error, NodeKind.Namespace)]);
        Assert.Single(cycleReport.Violations);
        Assert.Null(cycleReport.SuppressedCycles);

        // 3. Invalid YAML severity, scope, type_kind, and unknown metric fail loudly instead of silent pass
        var provider = new YamlRuleSetProvider();
        Assert.Throws<InvalidOperationException>(() => provider.ParseYaml("version: 1\nrules:\n  - id: R1\n    type: no_cycles\n    severity: catastrophic"));
        Assert.Throws<InvalidOperationException>(() => provider.ParseYaml("version: 1\nrules:\n  - id: R1\n    type: no_cycles\n    scope: galaxy"));
        Assert.Throws<InvalidOperationException>(() => provider.ParseYaml("version: 1\nrules:\n  - id: R1\n    type: naming_convention\n    type_kind: Foobar\n    pattern: \"I*\""));

        var badMetricRule = new MaxMetricRule("M-BAD", "Bad metric", "", RuleSeverity.Warning, "nonexistent_metric", 10);
        var badMetricReport = analyzer.CheckRules(cycleGraph, [badMetricRule]);
        Assert.Single(badMetricReport.Violations);
        Assert.Equal(RuleSeverity.Error, badMetricReport.Violations[0].Severity);
        Assert.Contains("Unknown metric 'nonexistent_metric'", badMetricReport.Violations[0].Message);

        // 4. XAML event handlers + Razor @code method calls + Keyed DI & TryAdd semantics
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "UiAndDi.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            WriteFile(tempDir, "MainWindow.xaml", """
                <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        x:Class="UiAndDi.MainWindow">
                    <Button Click="OnSaveClick" />
                </Window>
                """);

            WriteFile(tempDir, "MainWindow.xaml.cs", """
                namespace UiAndDi;
                public sealed class MainWindow
                {
                    private void OnSaveClick(object sender, System.EventArgs e) => SaveInternal();
                    private void SaveInternal() { }
                    private void TotallyUnusedMethod() { }
                }
                """);

            WriteFile(tempDir, "Components/LobbyPage.razor", """
                @page "/lobby"
                @inject UiAndDi.SessionState Session
                <button @onclick="HandleClick">Go</button>
                @code {
                    private void HandleClick() => Session.Lobby.CreateRoom();
                }
                """);

            WriteFile(tempDir, "Services.cs", """
                namespace UiAndDi;
                public interface ILobbyService { void CreateRoom(); void NeverCalled(); }
                public sealed class LobbyService : ILobbyService { public void CreateRoom() { } public void NeverCalled() { } }
                public sealed class SessionState { public ILobbyService Lobby { get; } = new LobbyService(); }
                public interface IKeyed { }
                public sealed class KeyedImpl : IKeyed { }
                public interface IHandler { }
                public sealed class HandlerA : IHandler { }
                public sealed class HandlerB : IHandler { }
                public interface IServiceCollection { }
                public static class DiExtensions
                {
                    public static IServiceCollection AddKeyedSingleton<TService, TImpl>(this IServiceCollection s, object key) => s;
                    public static IServiceCollection TryAddScoped<TService, TImpl>(this IServiceCollection s) => s;
                }
                """);

            WriteFile(tempDir, "Program.cs", """
                using UiAndDi;
                IServiceCollection services = null!;
                services.AddKeyedSingleton<IKeyed, KeyedImpl>("a");
                services.AddKeyedSingleton<IKeyed, KeyedImpl>("b");
                services.TryAddScoped<IHandler, HandlerA>();
                services.TryAddScoped<IHandler, HandlerB>();
                var w = new MainWindow();
                System.Console.WriteLine(w);
                """);

            using var extractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snap = await extractor.ExtractAsync(Path.Combine(tempDir, "UiAndDi.csproj"), new ExtractionOptions(IncludeMethods: true));

            // Keyed DI keeps distinct keys (2), TryAdd skips second IHandler (1) -> total 3
            var diReport = analyzer.AnalyzeDi(snap.Graph);
            Assert.Equal(2, diReport.Registrations.Count(r => r.ServiceTypeId == "UiAndDi.IKeyed"));
            var handlerReg = Assert.Single(diReport.Registrations, r => r.ServiceTypeId == "UiAndDi.IHandler");
            Assert.Equal("UiAndDi.HandlerA", handlerReg.ImplementationTypeId);

            // Dead-code method scope: XAML Click="OnSaveClick" & SaveInternal reachable; Razor Session.Lobby.CreateRoom reachable; TotallyUnusedMethod & NeverCalled dead
            var dead = analyzer.AnalyzeDeadCode(snap.Graph, NodeKind.Method, profile: "app");
            Assert.DoesNotContain(dead.Candidates, c => c.FullName.Contains("OnSaveClick"));
            Assert.DoesNotContain(dead.Candidates, c => c.FullName.Contains("SaveInternal"));
            Assert.DoesNotContain(dead.Candidates, c => c.FullName.Contains("ILobbyService.CreateRoom"));
            Assert.Contains(dead.Candidates, c => c.FullName.Contains("TotallyUnusedMethod"));
            Assert.Contains(dead.Candidates, c => c.FullName.Contains("ILobbyService.NeverCalled"));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task KeyedDiResolution_MatchesByServiceKey_WithoutFalseCaptives()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "KeyedDiProbe.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            WriteFile(tempDir, "Program.cs", """
                using KeyedDiProbe;

                IServiceCollection services = null!;
                services.AddKeyedScoped<OnlyKeyed>("blue");
                services.AddSingleton<NeedsUnkeyed>();
                services.AddScoped<OnlyUnkeyed>();
                services.AddSingleton<NeedsKeyedBlueOnUnkeyed>();
                services.AddSingleton<NeedsKeyedRedOnBlue>();
                services.AddSingleton<NeedsKeyedBlueMatch>();
                services.AddKeyedScoped<WildcardDep>(KeyedService.AnyKey);
                services.AddSingleton<NeedsAnyKey>();
                services.AddSingleton<NeedsWildcardUnkeyed>();

                namespace KeyedDiProbe
                {
                    public interface IServiceCollection { }
                    public static class KeyedService { public static readonly object AnyKey = new(); }

                    [System.AttributeUsage(System.AttributeTargets.Parameter)]
                    public sealed class FromKeyedServicesAttribute(object key) : System.Attribute
                    {
                        public object Key { get; } = key;
                    }

                    public static class DiExtensions
                    {
                        public static IServiceCollection AddSingleton<T>(this IServiceCollection s) => s;
                        public static IServiceCollection AddScoped<T>(this IServiceCollection s) => s;
                        public static IServiceCollection AddKeyedScoped<T>(this IServiceCollection s, object key) => s;
                    }

                    public sealed class OnlyKeyed { }
                    public sealed class OnlyUnkeyed { }
                    public sealed class WildcardDep { }

                    public sealed class NeedsUnkeyed(OnlyKeyed dep) { }
                    public sealed class NeedsKeyedBlueOnUnkeyed([FromKeyedServices("blue")] OnlyUnkeyed dep) { }
                    public sealed class NeedsKeyedRedOnBlue([FromKeyedServices("red")] OnlyKeyed dep) { }
                    public sealed class NeedsKeyedBlueMatch([FromKeyedServices("blue")] OnlyKeyed dep) { }
                    public sealed class NeedsAnyKey([FromKeyedServices("anything")] WildcardDep dep) { }
                    public sealed class NeedsWildcardUnkeyed(WildcardDep dep) { }
                }
                """);

            using var extractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snap = await extractor.ExtractAsync(Path.Combine(tempDir, "KeyedDiProbe.csproj"));
            var report = new ArchitectureAnalyzerService().AnalyzeDi(snap.Graph);

            // Keyed/unkeyed mismatches must NOT produce false captives
            Assert.DoesNotContain(report.CaptiveDependencies, c => c.ConsumerTypeId.EndsWith("NeedsUnkeyed"));
            Assert.DoesNotContain(report.CaptiveDependencies, c => c.ConsumerTypeId.EndsWith("NeedsKeyedBlueOnUnkeyed"));
            Assert.DoesNotContain(report.CaptiveDependencies, c => c.ConsumerTypeId.EndsWith("NeedsKeyedRedOnBlue"));
            Assert.DoesNotContain(report.CaptiveDependencies, c => c.ConsumerTypeId.EndsWith("NeedsWildcardUnkeyed"));

            // Matching key ("blue" -> "blue") and KeyedService.AnyKey -> keyed parameter MUST report captive
            Assert.Contains(report.CaptiveDependencies, c =>
                c.ConsumerTypeId.EndsWith("NeedsKeyedBlueMatch") &&
                c.DependencyPath.Contains("[key=blue]", StringComparison.Ordinal));
            Assert.Contains(report.CaptiveDependencies, c =>
                c.ConsumerTypeId.EndsWith("NeedsAnyKey") &&
                c.DependencyPath.Contains("[key=anything]", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public void AnalyzerDiagnostics_RejectsUnknownCategory_AndYamlHidesClrTypes()
    {
        var analyzer = new ArchitectureAnalyzerService();
        var graph = new CodeGraph(
            rootPath: "/repo",
            nodes: [],
            edges: [],
            analyzersLoadedCount: 120,
            availableAnalyzerCategories: ["Design", "Maintainability", "Performance", "Security"]);

        var ex = Assert.Throws<ArgumentException>(() =>
            analyzer.GetAnalyzerDiagnostics(graph, categories: ["badCategory"]));
        Assert.Contains("Unknown analyzer category", ex.Message);
        Assert.Contains("badCategory", ex.Message);
        Assert.Contains("Design", ex.Message);

        // Valid category with 0 diagnostics succeeds
        var validReport = analyzer.GetAnalyzerDiagnostics(graph, categories: ["Security"]);
        Assert.Equal(0, validReport.TotalDiagnostics);

        // 'all' succeeds
        var allReport = analyzer.GetAnalyzerDiagnostics(graph, categories: ["all"]);
        Assert.Equal(0, allReport.TotalDiagnostics);

        // Malformed YAML type error must not leak assembly-qualified CLR names
        var yamlProvider = new YamlRuleSetProvider();
        var yamlEx = Assert.Throws<InvalidDataException>(() =>
            yamlProvider.ParseYaml("version: [1, 2]\nrules: []\n"));
        Assert.DoesNotContain("System.Int32", yamlEx.Message);
        Assert.DoesNotContain("System.Private.CoreLib", yamlEx.Message);
        Assert.Contains("expected an integer", yamlEx.Message, StringComparison.OrdinalIgnoreCase);

        // Required YAML rule fields and unsupported glob brackets must fail fast
        Assert.Throws<InvalidOperationException>(() => yamlProvider.ParseYaml("version: 1\nrules:\n  - id: F1\n    type: forbid_dependency\n"));
        Assert.Throws<InvalidOperationException>(() => yamlProvider.ParseYaml("version: 1\nrules:\n  - id: M1\n    type: max_metric\n    metric: lcom4\n"));
        Assert.Throws<InvalidOperationException>(() => yamlProvider.ParseYaml("version: 1\nrules:\n  - id: G1\n    type: forbid_dependency\n    from: \"App.[*\"\n    to: [\"App.Infra.*\"]\n"));
        var scalarToEx = Assert.Throws<InvalidDataException>(() => yamlProvider.ParseYaml("version: 1\nrules:\n  - id: L1\n    type: forbid_dependency\n    from: \"App.*\"\n    to: \"App.Infra.*\"\n"));
        Assert.Contains("YAML list", scalarToEx.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cycle2Regressions_AncestorConfig_BranchDi_VirtualOverride_SyntaxDegraded()
    {
        var tempDir = CreateTempDir();
        try
        {
            // 1. Ancestor .archlens.yaml discovery stops at .git
            Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
            WriteFile(tempDir, ".archlens.yaml", "version: 1\nrules: []\n");
            var nestedDir = Path.Combine(tempDir, "src", "MyProj");
            Directory.CreateDirectory(nestedDir);
            Assert.Equal(Path.Combine(tempDir, ".archlens.yaml"), YamlRuleSetProvider.DiscoverConfigFile(nestedDir));

            // 2. Branch-aware TryAdd, ServiceDescriptor.Replace, virtual method live override, and syntax-error degraded detection
            WriteFile(nestedDir, "MyProj.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            WriteFile(nestedDir, "Program.cs", """
                using MyProj;
                IServiceCollection services = null!;
                var fakeMode = args.Length > 0;
                if (fakeMode)
                {
                    services.TryAddSingleton<IGame, FakeGame>();
                }
                else
                {
                    services.TryAddSingleton<IGame, RealGame>();
                }
                services.Replace(ServiceDescriptor.Singleton<IRecorder, AuditRecorder>());
                new DerivedRunner().Run();

                namespace MyProj
                {
                    public enum ServiceLifetime { Singleton, Scoped, Transient }
                    public sealed class ServiceDescriptor
                    {
                        public static ServiceDescriptor Singleton<TService, TImpl>() => new();
                    }
                    public interface IServiceCollection { }
                    public static class DiExt
                    {
                        public static IServiceCollection TryAddSingleton<TService, TImpl>(this IServiceCollection s) => s;
                        public static IServiceCollection Replace(this IServiceCollection s, ServiceDescriptor d) => s;
                    }
                    public interface IGame { }
                    public sealed class FakeGame : IGame { public FakeGame(string x) { } }
                    public sealed class RealGame : IGame { public RealGame(string x) { } }
                    public interface IRecorder { }
                    public sealed class AuditRecorder : IRecorder { public AuditRecorder(string x) { } }
                    public class BaseRunner { public virtual void Run() { } public virtual void DeadVirtual() { } }
                    public sealed class DerivedRunner : BaseRunner { public override void Run() { } }
                }
                """);

            using var extractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snap = await extractor.ExtractAsync(Path.Combine(nestedDir, "MyProj.csproj"), new ExtractionOptions(IncludeMethods: true));
            var analyzer = new ArchitectureAnalyzerService();

            // Both if/else TryAdd registrations AND ServiceDescriptor.Replace must be captured
            var di = analyzer.AnalyzeDi(snap.Graph);
            Assert.Contains(di.Registrations, r => r.ImplementationTypeId == "MyProj.FakeGame");
            Assert.Contains(di.Registrations, r => r.ImplementationTypeId == "MyProj.RealGame");
            Assert.Contains(di.Registrations, r => r.ImplementationTypeId == "MyProj.AuditRecorder");

            // Dead code (method scope): RealGame/AuditRecorder ctors and BaseRunner.Run() must NOT be dead; BaseRunner.DeadVirtual() MUST be dead
            var dead = analyzer.AnalyzeDeadCode(snap.Graph, NodeKind.Method, profile: "app");
            Assert.DoesNotContain(dead.Candidates, c => c.FullName.Contains("RealGame.RealGame"));
            Assert.DoesNotContain(dead.Candidates, c => c.FullName.Contains("AuditRecorder.AuditRecorder"));
            Assert.DoesNotContain(dead.Candidates, c => c.FullName.Contains("BaseRunner.Run()"));
            Assert.Contains(dead.Candidates, c => c.FullName.Contains("BaseRunner.DeadVirtual()"));

            // 3. Syntax error file must mark LoadStatus.Degraded = true and record RoslynSyntax diagnostic
            WriteFile(nestedDir, "Broken.cs", "namespace MyProj; public class Broken { public void Bad( { } }");
            var brokenSnap = await extractor.ExtractAsync(Path.Combine(nestedDir, "MyProj.csproj"));
            Assert.NotNull(brokenSnap.Graph.LoadStatus);
            Assert.True(brokenSnap.Graph.LoadStatus!.Degraded);
            Assert.Contains(brokenSnap.Graph.Diagnostics, d => d.Source == "RoslynSyntax");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Cycle3Regressions_RazorPrecision_InlineCode_DbContextFactory_NamedHttpClient_CompileError()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "BlazorAndDi.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            WriteFile(tempDir, "Components/Layout/MainLayout.razor", """
                @namespace BlazorAndDi.Components.Layout
                <div><NavMenu /></div>
                """);

            WriteFile(tempDir, "Components/Layout/NavMenu.razor", """
                @namespace BlazorAndDi.Components.Layout
                <a href="/">Home</a>
                <a href="@AppRoutes.Ghost">Ghost</a>
                <p>@("Home.Title")</p>
                """);

            WriteFile(tempDir, "Components/Pages/Home.razor", """
                @page "/"
                @layout MainLayout
                @namespace BlazorAndDi.Components.Pages
                <h1>Welcome</h1>
                @code {
                    private int _count;
                    protected override void OnInitialized() { if (_count == 0) Increment(); }
                    private void Increment() { _count++; }
                    private void NeverCalledHelper() { if (_count > 10) _count = 0; }
                }
                """);

            WriteFile(tempDir, "Components/Widgets/Ghost.razor", """
                @namespace BlazorAndDi.Components.Widgets
                <span>Never rendered</span>
                """);

            WriteFile(tempDir, "Program.cs", """
                using BlazorAndDi;

                IServiceCollection services = null!;
                services.AddDbContextFactory<AppDb>();
                services.AddSingleton<BadDirectDbConsumer>();
                services.AddHttpClient("Telegram");
                System.Console.WriteLine(AppRoutes.Ghost);

                namespace BlazorAndDi
                {
                    public static class AppRoutes { public const string Ghost = "/ghost"; }
                    public interface IServiceCollection { }
                    public static class DiExtensions
                    {
                        public static IServiceCollection AddDbContextFactory<TContext>(this IServiceCollection s) => s;
                        public static IServiceCollection AddSingleton<T>(this IServiceCollection s) => s;
                        public static IServiceCollection AddHttpClient(this IServiceCollection s, string name) => s;
                    }
                    public sealed class AppDb { }
                    public sealed class BadDirectDbConsumer(AppDb db) { }
                }
                """);

            using var extractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snap = await extractor.ExtractAsync(Path.Combine(tempDir, "BlazorAndDi.csproj"), new ExtractionOptions(IncludeMethods: true));
            var analyzer = new ArchitectureAnalyzerService();

            // 1. Plain HTML text "Home" and "@AppRoutes.Ghost" must NOT fabricate NavMenu -> Home or NavMenu -> Ghost edges (0 namespace cycles!)
            var nsGraph = analyzer.GetDependencyGraph(snap.Graph, NodeKind.Namespace);
            Assert.Empty(nsGraph.Cycles);
            Assert.DoesNotContain(snap.Graph.Edges, e => e.FromId.EndsWith("NavMenu") && e.ToId.EndsWith(".Home"));
            Assert.DoesNotContain(snap.Graph.Edges, e => e.FromId.EndsWith("NavMenu") && e.ToId.EndsWith(".Ghost"));
            // @layout MainLayout MUST create a real Pages -> Layout edge
            Assert.Contains(snap.Graph.Edges, e => e.FromId.EndsWith(".Home") && e.ToId.EndsWith(".MainLayout"));

            // 2. Ghost.razor is unreferenced -> dead in type scope
            var deadTypes = analyzer.AnalyzeDeadCode(snap.Graph, NodeKind.Type, profile: "app");
            Assert.Contains(deadTypes.Candidates, c => c.FullName.EndsWith(".Ghost"));

            // 3. Inline @code metrics & method dead code on Home.razor
            var homeNode = snap.Graph.FindNode("BlazorAndDi.Components.Pages.Home");
            Assert.NotNull(homeNode);
            Assert.True(homeNode!.CyclomaticComplexity > 0);
            var deadMethods = analyzer.AnalyzeDeadCode(snap.Graph, NodeKind.Method, profile: "app");
            Assert.Contains(deadMethods.Candidates, c => c.FullName.Contains("Home.NeverCalledHelper"));
            Assert.DoesNotContain(deadMethods.Candidates, c => c.FullName.Contains("Home.OnInitialized"));
            Assert.DoesNotContain(deadMethods.Candidates, c => c.FullName.Contains("Home.Increment"));

            // 4. AddDbContextFactory registers IDbContextFactory<AppDb> (Singleton) + AppDb (Scoped) -> catches captive; named AddHttpClient does NOT register string
            var di = analyzer.AnalyzeDi(snap.Graph);
            Assert.Contains(di.Registrations, r => r.ServiceTypeId == "Microsoft.EntityFrameworkCore.IDbContextFactory<BlazorAndDi.AppDb>" && r.Lifetime == ServiceLifetimeKind.Singleton);
            Assert.Contains(di.Registrations, r => r.ServiceTypeId == "BlazorAndDi.AppDb" && r.Lifetime == ServiceLifetimeKind.Scoped);
            Assert.DoesNotContain(di.Registrations, r => r.ServiceTypeId == "string" || r.ImplementationTypeId == "System.String");
            Assert.Contains(di.CaptiveDependencies, c => c.ConsumerTypeId.EndsWith("BadDirectDbConsumer"));

            // 5. Top-level YAML exclude applies to no_dead_code
            var yamlProvider = new YamlRuleSetProvider();
            var rules = yamlProvider.ParseYaml("""
                version: 1
                exclude:
                  - "*.Widgets.*"
                rules:
                  - id: DC-1
                    type: no_dead_code
                    scope: type
                    profile: app
                """);
            var check = analyzer.CheckRules(snap.Graph, rules);
            Assert.DoesNotContain(check.Violations, v => v.Message.Contains("Ghost"));

            // 6. Semantic compile error marks project degraded
            WriteFile(tempDir, "Uncompilable.cs", "namespace BlazorAndDi; public sealed class Uncompilable { private readonly TotallyMissingType _x = null!; }");
            var brokenSnap = await extractor.ExtractAsync(Path.Combine(tempDir, "BlazorAndDi.csproj"));
            Assert.True(brokenSnap.Graph.LoadStatus?.Degraded == true);
            Assert.Contains(brokenSnap.Graph.Diagnostics, d => d.Source == "RoslynCompiler");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Cycle4Fixes_CsprojProjectRefsStableCycleDiffBlazorMethodsFactoryDiAndSqlServiceKey()
    {
        var tempDir = CreateTempDir();
        try
        {
            // 1. Single .csproj extraction includes <ProjectReference> closure
            WriteFile(tempDir, "Lib/Lib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            WriteFile(tempDir, "Lib/SecretLib.cs", "namespace Lib; public sealed class SecretLib { public string Ping() => \"ok\"; }");

            WriteFile(tempDir, "App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="..\Lib\Lib.csproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteFile(tempDir, "App/Components/ChatPanel.razor", """
                @namespace App.Components
                <div class="@CssClass">@Label</div>
                @code {
                    public event Action? Changed;
                    private string CssClass => ComputeCss();
                    private string Label => "ok";
                    protected override void OnInitialized() { Changed += OnChanged; }
                    private void OnChanged() { }
                    private string ComputeCss() => "chat";
                    public void ScrollToBottom() { }
                    private void TrulyDeadRazorMethod() { }
                }
                """);
            WriteFile(tempDir, "App/Program.cs", """
                using App;
                using App.Components;
                using Lib;

                var lib = new SecretLib();
                System.Console.WriteLine(lib.Ping());

                IServiceCollection services = null!;
                services.AddScoped<ScopedRepo>();
                services.AddSingleton<ISingletonFacade>(sp => { _ = sp.GetRequiredService<ScopedRepo>(); return new SingletonFacade(); });
                services.AddKeyedSingleton<IKeyedSvc, KeyedSvcImpl>("primary");

                var holder = new ChatHolder();
                holder.Trigger();

                namespace App
                {
                    public interface IServiceProviderEx { T GetRequiredService<T>(); }
                    public interface IServiceCollection { }
                    public static class DiExt
                    {
                        public static IServiceCollection AddScoped<T>(this IServiceCollection s) => s;
                        public static IServiceCollection AddSingleton<TService>(this IServiceCollection s, Func<IServiceProviderEx, TService> factory) => s;
                        public static IServiceCollection AddKeyedSingleton<TService, TImpl>(this IServiceCollection s, object key) => s;
                    }
                    public sealed class ScopedRepo { }
                    public interface ISingletonFacade { }
                    public sealed class SingletonFacade : ISingletonFacade { }
                    public interface IKeyedSvc { }
                    public sealed class KeyedSvcImpl : IKeyedSvc { }

                    public sealed class NoOpAdapter
                    {
                        public void A() { }
                        public Task B() => Task.CompletedTask;
                        public void C() { }
                    }

                    public sealed record BigDtoRecord(string A, string B, string C, string D, string E, string F, string G);

                    public sealed class ChatHolder
                    {
                        private readonly ChatPanel _panel = new();
                        public void Trigger() => _panel.ScrollToBottom();
                    }
                }
                """);
            WriteFile(tempDir, "App/ChatPanel.razor.g.cs", """
                // <auto-generated/>
                namespace App.Components
                {
                    public partial class ChatPanel { public void ScrollToBottom() { } }
                }
                """);

            using var extractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snap = await extractor.ExtractAsync(Path.Combine(tempDir, "App", "App.csproj"), new ExtractionOptions(IncludeMethods: true));
            var analyzer = new ArchitectureAnalyzerService();

            // ProjectReference Lib must be loaded when analyzing App/App.csproj directly
            Assert.Contains(snap.Graph.Nodes, n => n.Id == "Lib.SecretLib");
            var forbidRule = new YamlRuleSetProvider().ParseYaml("""
                version: 1
                rules:
                  - id: NO-LIB
                    type: forbid_dependency
                    from: "App.*"
                    to: ["Lib.*"]
                """);
            var check = analyzer.CheckRules(snap.Graph, forbidRule);
            Assert.Contains(check.Violations, v => v.RuleId == "NO-LIB" && v.TargetNodeId == "Lib.SecretLib");

            // Blazor @code methods: event subscription (OnChanged), property call (ComputeCss), Roslyn call (ScrollToBottom) are live; TrulyDeadRazorMethod is dead
            var deadMethods = analyzer.AnalyzeDeadCode(snap.Graph, NodeKind.Method, profile: "app");
            Assert.DoesNotContain(deadMethods.Candidates, c => c.FullName.Contains("ChatPanel.OnChanged"));
            Assert.DoesNotContain(deadMethods.Candidates, c => c.FullName.Contains("ChatPanel.ComputeCss"));
            Assert.DoesNotContain(deadMethods.Candidates, c => c.FullName.Contains("ChatPanel.ScrollToBottom"));
            Assert.Contains(deadMethods.Candidates, c => c.FullName.Contains("ChatPanel.TrulyDeadRazorMethod"));

            // Factory lambda captive dependency detection (sp.GetRequiredService<ScopedRepo>(), even with parameterless ctor!)
            var di = analyzer.AnalyzeDi(snap.Graph);
            Assert.Contains(di.CaptiveDependencies, c =>
                (c.ConsumerTypeId == "App.SingletonFacade" || c.ConsumerTypeId == "App.ISingletonFacade") &&
                c.DependencyTypeId == "App.ScopedRepo");

            // SQL di_registrations exposes service_key column
            var sqlEngine = new GraphSqlQueryEngine();
            var sqlRes = sqlEngine.Execute(snap.Graph, "SELECT service_type_id, service_key FROM di_registrations WHERE service_key IS NOT NULL");
            Assert.Single(sqlRes.Rows);
            Assert.Equal("primary", sqlRes.Rows[0]["service_key"]?.ToString());

            // No-op adapter gets LCOM4 = 0/1 (not flagged by max_metric lcom4), and BigDtoRecord is excluded from constructor_params
            var noOpNode = snap.Graph.FindNode("App.NoOpAdapter");
            Assert.NotNull(noOpNode);
            Assert.True(noOpNode!.Lcom4 <= 1);
            var metricRules = new YamlRuleSetProvider().ParseYaml("""
                version: 1
                rules:
                  - id: C-LCOM
                    type: max_metric
                    metric: lcom4
                    threshold: 2
                  - id: C-CTOR
                    type: max_metric
                    metric: constructor_params
                    threshold: 5
                """);
            var metricCheck = analyzer.CheckRules(snap.Graph, metricRules);
            Assert.DoesNotContain(metricCheck.Violations, v => v.RuleId == "C-LCOM" && v.NodeId == "App.NoOpAdapter");
            Assert.DoesNotContain(metricCheck.Violations, v => v.RuleId == "C-CTOR" && v.NodeId == "App.BigDtoRecord");

            // 2. DiffSnapshots matches cycle violations stably by cycleKey even when sample edge / edge count changes
            var cycNodes = new List<CodeNode>
            {
                new("N.A.T1", "T1", "N.A.T1", NodeKind.Type, "P", "N.A", TypeKind.Class),
                new("N.A.T2", "T2", "N.A.T2", NodeKind.Type, "P", "N.A", TypeKind.Class),
                new("N.B.U1", "U1", "N.B.U1", NodeKind.Type, "P", "N.B", TypeKind.Class)
            };
            var baseEdges = new List<DependencyEdge>
            {
                new("N.A.T1", "N.B.U1", DependencyKind.MethodCall),
                new("N.B.U1", "N.A.T1", DependencyKind.MethodCall)
            };
            var currEdges = new List<DependencyEdge>
            {
                new("N.A.T1", "N.B.U1", DependencyKind.MethodCall),
                new("N.A.T2", "N.B.U1", DependencyKind.MethodCall),
                new("N.B.U1", "N.A.T1", DependencyKind.MethodCall)
            };
            var cycRule = new NoCyclesRule("NO-CYC", "No cycles", "desc", RuleSeverity.Error, NodeKind.Namespace);
            var baseSnap = new ArchitectureSnapshot("b", "C:/repo", DateTimeOffset.UtcNow, new CodeGraph("C:/repo", cycNodes, baseEdges));
            var currSnap = new ArchitectureSnapshot("c", "C:/repo", DateTimeOffset.UtcNow, new CodeGraph("C:/repo", cycNodes, currEdges));
            var diff = analyzer.DiffSnapshots(baseSnap, currSnap, [cycRule]);
            Assert.Empty(diff.NewViolations);
            Assert.Empty(diff.ResolvedViolations);

            // 3. Unknown max_metric marks VacuousConfiguration = true
            var badMetricRules = new YamlRuleSetProvider().ParseYaml("""
                version: 1
                rules:
                  - id: BAD-M
                    type: max_metric
                    metric: nonexistent_metric
                    threshold: 5
                """);
            var badMetricReport = analyzer.CheckRules(snap.Graph, badMetricRules);
            Assert.True(badMetricReport.VacuousConfiguration);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Cycle5Fixes_RecursiveSrcDiscoveryTfmWarningEmptyRulesQueryLoadStatusAndMcpGuards()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "src/SubProj/SubProj.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);
            WriteFile(tempDir, "src/SubProj/Alpha.cs", "namespace SubProj; public sealed class Alpha { public int Value() => 42; }");
            WriteFile(tempDir, "empty.yaml", "version: 1\nrules: []\n");
            WriteFile(tempDir, "notes.txt", "not yaml");

            var srcDir = Path.Combine(tempDir, "src");
            using var extractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);

            // 1. Recursive src/ discovery works without top-level .sln
            Assert.True(extractor.CanHandle(srcDir));
            var snap = await extractor.ExtractAsync(srcDir);
            Assert.Contains(snap.Graph.Nodes, n => n.Id == "SubProj.Alpha");

            // 2. Completely unmatched TargetFramework throws ArgumentException listing available TFMs
            var tfmEx = await Assert.ThrowsAsync<ArgumentException>(() =>
                extractor.ExtractAsync(srcDir, new ExtractionOptions(TargetFramework: "net2.0")));
            Assert.Contains("net2.0", tfmEx.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("net10.0", tfmEx.Message, StringComparison.OrdinalIgnoreCase);

            // 3. export_diagram emits zero-match filter comment
            var mermaid = GraphVisualizer.Export(snap.Graph, "mermaid", NodeKind.Namespace, "NoMatch.*");
            Assert.Contains("%% No namespace nodes matched filter 'NoMatch.*'", mermaid);

            // 4. MCP tool guards: empty explicit rulesPath, non-YAML rulesPath hint, hotspots fields=name, diff mutual exclusion, query loadStatus
            var degradedStatus = new GraphLoadStatus("roslyn", 2, 1, 1, Degraded: true, Approximate: false, Note: "1 project failed");
            var degradedGraph = new CodeGraph(
                srcDir,
                snap.Graph.Nodes,
                snap.Graph.Edges,
                snap.Graph.Projects,
                loadStatus: degradedStatus,
                analyzersLoadedCount: 10,
                availableAnalyzerCategories: ["Design"]);
            var analyzer = new ArchitectureAnalyzerService();
            using var sessions = new WorkspaceSessionManager([new StubGraphExtractor(degradedGraph)]);
            var tools = new ArchLensTools(sessions, analyzer, new GraphSqlQueryEngine(), [new YamlRuleSetProvider()]);

            // Empty explicit rulesPath errors instead of silently falling back
            var emptyRulesRes = await tools.CheckRulesAsync(srcDir, rulesPath: Path.Combine(tempDir, "empty.yaml"));
            Assert.True(emptyRulesRes.IsError);
            var emptyRulesJson = ((ModelContextProtocol.Protocol.TextContentBlock)emptyRulesRes.Content[0]).Text;
            Assert.Contains("0 rules", emptyRulesJson);

            // Non-YAML rulesPath gets YAML hint (not .slnx/.sln hint)
            var badExtRes = await tools.CheckRulesAsync(srcDir, rulesPath: Path.Combine(tempDir, "notes.txt"));
            Assert.True(badExtRes.IsError);
            var badExtJson = ((ModelContextProtocol.Protocol.TextContentBlock)badExtRes.Content[0]).Text;
            Assert.Contains(".yaml or .yml", badExtJson);

            // hotspots accepts 'name' in fields
            var hsRes = await tools.GetHotspotsAsync(srcDir, fields: "name,cyclomaticComplexity");
            Assert.NotEqual(true, hsRes.IsError);

            // diff rejects saveBaseline + resetBaseline together
            var diffRes = await tools.DiffAgainstBaselineAsync(srcDir, saveBaseline: true, resetBaseline: true);
            Assert.True(diffRes.IsError);

            // query returns loadStatus on degraded graph
            var qRes = await tools.QueryGraphSqlAsync(srcDir, sql: "SELECT COUNT(*) AS c FROM nodes");
            Assert.NotEqual(true, qRes.IsError);
            var qJson = ((ModelContextProtocol.Protocol.TextContentBlock)qRes.Content[0]).Text;
            Assert.Contains("\"loadStatus\"", qJson);
            Assert.Contains("\"degraded\":true", qJson);

            // analyzers 0-result note mentions elevateToWarning
            var azReport = analyzer.GetAnalyzerDiagnostics(degradedGraph, ["Design"], 50, RuleSeverity.Info);
            Assert.NotNull(azReport.Note);
            Assert.Contains("elevateToWarning", azReport.Note);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Cycle6Fixes_MultiProjectSrcDiscoveryBaselineScopeWarningYamlValidationDiConsumersAndDiffCycles()
    {
        var tempDir = CreateTempDir();
        try
        {
            // 1. Recursive src/ with multiple projects referencing each other must NOT throw "'Lib' is already part of the workspace"
            WriteFile(tempDir, "src/Core/Core.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
                </Project>
                """);
            WriteFile(tempDir, "src/Core/IUnused.cs", "namespace Core; public interface IUnused { } public sealed class UnusedImpl : IUnused { }");
            WriteFile(tempDir, "src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
                  <ItemGroup><ProjectReference Include="..\Core\Core.csproj" /></ItemGroup>
                </Project>
                """);
            WriteFile(tempDir, "src/App/Program.cs", """
                using Core;
                App.IServiceCollection s = null!;
                s.AddSingleton<IUnused, UnusedImpl>();
                namespace App
                {
                    public interface IServiceCollection { }
                    public static class DiExt { public static IServiceCollection AddSingleton<T1, T2>(this IServiceCollection s) => s; }
                }
                """);

            var srcDir = Path.Combine(tempDir, "src");
            using var extractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snap = await extractor.ExtractAsync(srcDir);
            Assert.Contains(snap.Graph.Projects, p => p.Name == "Core");
            Assert.Contains(snap.Graph.Projects, p => p.Name == "App");

            // 2. DI ConsumerCount excludes the DI registration site itself -> 0 consumers for registered-but-uninjected interface
            var analyzer = new ArchitectureAnalyzerService();
            var di = analyzer.AnalyzeDi(snap.Graph);
            var unusedSingle = di.SingleImplementationInterfaces.Single(s => s.InterfaceTypeId == "Core.IUnused");
            Assert.True(unusedSingle.IsDiRegistered);
            Assert.Equal(0, unusedSingle.ConsumerCount);

            // 3. BaselineStore.GetScopeMismatchWarning flags includeMethods and backend differences
            var baseGraph = new CodeGraph(srcDir, snap.Graph.Nodes, snap.Graph.Edges, snap.Graph.Projects,
                loadStatus: new GraphLoadStatus("roslyn", 2, 2, 0, false, false), methodsIncluded: false);
            var currGraph = new CodeGraph(srcDir, snap.Graph.Nodes, snap.Graph.Edges, snap.Graph.Projects,
                loadStatus: new GraphLoadStatus("roslyn", 2, 2, 0, false, false), methodsIncluded: true);
            var warn = BaselineStore.GetScopeMismatchWarning(
                new ArchitectureSnapshot("b", srcDir, DateTimeOffset.UtcNow, baseGraph),
                new ArchitectureSnapshot("c", srcDir, DateTimeOffset.UtcNow, currGraph));
            Assert.NotNull(warn);
            Assert.Contains("includeMethods", warn);

            // 4. YAML validation rejects invalid no_dead_code.profile and out-of-range god_class.min_conditions
            var yaml = new YamlRuleSetProvider();
            Assert.Throws<InvalidOperationException>(() => yaml.ParseYaml("version: 1\nrules:\n  - id: D1\n    type: no_dead_code\n    profile: bogus\n"));
            Assert.Throws<InvalidOperationException>(() => yaml.ParseYaml("version: 1\nrules:\n  - id: G1\n    type: god_class\n    min_conditions: 9\n"));

            // 5. YAML rootNamespaceOverride actually scopes rules
            var overriddenRules = yaml.ParseYaml("version: 1\nroot_namespace: WrongRoot\nrules:\n  - id: S1\n    type: slice_isolation\n    segment: Features\n", rootNamespaceOverride: "RealRoot");
            var sliceRule = Assert.IsType<SliceIsolationRule>(Assert.Single(overriddenRules));
            var sliceNodes = new List<CodeNode>
            {
                new("RealRoot.Features.A.Foo", "Foo", "RealRoot.Features.A.Foo", NodeKind.Type, "P", "RealRoot.Features.A", TypeKind.Class),
                new("RealRoot.Features.B.Bar", "Bar", "RealRoot.Features.B.Bar", NodeKind.Type, "P", "RealRoot.Features.B", TypeKind.Class)
            };
            var sliceCheck = analyzer.CheckRules(new CodeGraph("C:/repo", sliceNodes, [new("RealRoot.Features.A.Foo", "RealRoot.Features.B.Bar", DependencyKind.MethodCall)]), [sliceRule]);
            Assert.Single(sliceCheck.Violations);

            // 6. DiffSnapshots suppresses pure parent-child namespace cycles just like check/overview
            var pcBase = new ArchitectureSnapshot("b", "C:/repo", DateTimeOffset.UtcNow, new CodeGraph("C:/repo", [], []));
            var pcNodes = new List<CodeNode>
            {
                new("Acme.Parent.P", "P", "Acme.Parent.P", NodeKind.Type, "P", "Acme.Parent", TypeKind.Class),
                new("Acme.Parent.Child.C", "C", "Acme.Parent.Child.C", NodeKind.Type, "P", "Acme.Parent.Child", TypeKind.Class)
            };
            var pcEdges = new List<DependencyEdge>
            {
                new("Acme.Parent.P", "Acme.Parent.Child.C", DependencyKind.MethodCall),
                new("Acme.Parent.Child.C", "Acme.Parent.P", DependencyKind.MethodCall)
            };
            var pcCurr = new ArchitectureSnapshot("c", "C:/repo", DateTimeOffset.UtcNow, new CodeGraph("C:/repo", pcNodes, pcEdges));
            var pcDiff = analyzer.DiffSnapshots(pcBase, pcCurr);
            Assert.Empty(pcDiff.AddedCycles);
            Assert.Equal(0, pcDiff.CycleCountDelta);

            // 7. MCP di_analysis and analyzers reject direct .dll files
            var fakeDll = Path.Combine(tempDir, "Fake.dll");
            File.WriteAllBytes(fakeDll, [0x4D, 0x5A]);
            using var sessions = new WorkspaceSessionManager([new StubGraphExtractor(snap.Graph)]);
            var tools = new ArchLensTools(sessions, analyzer, new GraphSqlQueryEngine(), [yaml]);
            var diDllRes = await tools.AnalyzeDiAsync(fakeDll);
            Assert.True(diDllRes.IsError);
            var azDllRes = await tools.GetAnalyzerDiagnosticsAsync(fakeDll);
            Assert.True(azDllRes.IsError);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Cycle7Fixes_CycleSuppressionDiFactoryAndOptionsLcom4DeadCodeInterfacesYamlLayersAndDiffVacuous()
    {
        var tempDir = CreateTempDir();
        try
        {
            // 1. Cycle suppression: 3-node nested chain Root -> Root.Sub -> Root.Sub.Deeper -> Root and
            //    non-parent Probe.Feature <-> Probe.Migrations MUST be reported as real cycles!
            var cycNodes = new List<CodeNode>
            {
                new("Probe.Root.R", "R", "Probe.Root.R", NodeKind.Type, "P", "Probe.Root", TypeKind.Class),
                new("Probe.Root.Sub.S", "S", "Probe.Root.Sub.S", NodeKind.Type, "P", "Probe.Root.Sub", TypeKind.Class),
                new("Probe.Root.Sub.Deeper.D", "D", "Probe.Root.Sub.Deeper.D", NodeKind.Type, "P", "Probe.Root.Sub.Deeper", TypeKind.Class),
                new("Probe.Feature.F", "F", "Probe.Feature.F", NodeKind.Type, "P", "Probe.Feature", TypeKind.Class),
                new("Probe.Migrations.M", "M", "Probe.Migrations.M", NodeKind.Type, "P", "Probe.Migrations", TypeKind.Class)
            };
            var cycEdges = new List<DependencyEdge>
            {
                new("Probe.Root.R", "Probe.Root.Sub.S", DependencyKind.FieldOrProperty),
                new("Probe.Root.Sub.S", "Probe.Root.Sub.Deeper.D", DependencyKind.FieldOrProperty),
                new("Probe.Root.Sub.Deeper.D", "Probe.Root.R", DependencyKind.FieldOrProperty),
                new("Probe.Feature.F", "Probe.Migrations.M", DependencyKind.FieldOrProperty),
                new("Probe.Migrations.M", "Probe.Feature.F", DependencyKind.FieldOrProperty)
            };
            var analyzer = new ArchitectureAnalyzerService();
            var cycGraph = new CodeGraph("C:/repo", cycNodes, cycEdges);
            var cycDep = analyzer.GetDependencyGraph(cycGraph, NodeKind.Namespace);
            Assert.Equal(2, cycDep.TotalCycleCount);

            // 2. YAML strictness: unknown layer key, invalid layer glob, numeric type_kind, unsupported max_metric/no_dead_code scope
            var yaml = new YamlRuleSetProvider();
            Assert.Throws<InvalidOperationException>(() => yaml.ParseYaml("version: 1\npreset: clean-architecture\nlayers:\n  domian: ['A.*']\n"));
            Assert.Throws<InvalidOperationException>(() => yaml.ParseYaml("version: 1\npreset: clean-architecture\nlayers:\n  domain: ['A.[Core]*']\n"));
            Assert.Throws<InvalidOperationException>(() => yaml.ParseYaml("version: 1\nrules:\n  - id: N1\n    type: naming_convention\n    target: '*'\n    pattern: 'I*'\n    type_kind: '99'\n"));
            Assert.Throws<InvalidOperationException>(() => yaml.ParseYaml("version: 1\nrules:\n  - id: M1\n    type: max_metric\n    metric: lcom4\n    threshold: 2\n    scope: project\n"));
            Assert.Throws<InvalidOperationException>(() => yaml.ParseYaml("version: 1\nrules:\n  - id: D1\n    type: no_dead_code\n    scope: project\n"));

            // 3. DiffSnapshots sets VacuousConfiguration when rootNamespace matches 0 types
            var baseSnap = new ArchitectureSnapshot("b", "C:/repo", DateTimeOffset.UtcNow, cycGraph);
            var cleanRules = ArchitecturePresets.CreateRules("clean-architecture", "Missing.Root");
            var diffVacuous = analyzer.DiffSnapshots(baseSnap, baseSnap, cleanRules, rootNamespace: "Missing.Root");
            Assert.True(diffVacuous.VacuousConfiguration);

            // 4. End-to-end Roslyn: DI factory/instance registrations must NOT trigger constructor-based captive FPs;
            //    AddDbContext<T> must register DbContextOptions<T> as Scoped; block-bodied no-op & stateless converter LCOM4<=1;
            //    interface method whose implementation is invoked directly must NOT be flagged dead.
            WriteFile(tempDir, "SemProbe.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
                </Project>
                """);
            WriteFile(tempDir, "Program.cs", """
                using SemProbe;
                IServiceCollection services = null!;
                services.AddScoped<IScoped, ScopedImpl>();
                services.AddSingleton<IManualFactory>(sp => new ManualFactory(new ScopedImpl()));
                services.AddSingleton<IManualInstance>(new ManualInstance(new ScopedImpl()));
                services.AddDbContext<ProbeDb>();
                services.AddSingleton<SingletonTakesDbOptions>();
                services.AddClaimsPrincipalFactory<MyClaimsFactory>();
                new UsedContract().Used();

                namespace Microsoft.EntityFrameworkCore
                {
                    public class DbContextOptions<TContext> { }
                }
                namespace SemProbe
                {
                    public interface IServiceProviderEx { T GetRequiredService<T>(); }
                    public interface IServiceCollection { }
                    public static class DiExt
                    {
                        public static IServiceCollection AddScoped<T1, T2>(this IServiceCollection s) => s;
                        public static IServiceCollection AddSingleton<T1>(this IServiceCollection s) => s;
                        public static IServiceCollection AddSingleton<T1>(this IServiceCollection s, T1 instance) => s;
                        public static IServiceCollection AddSingleton<T1>(this IServiceCollection s, System.Func<IServiceProviderEx, T1> factory) => s;
                        public static IServiceCollection AddDbContext<TContext>(this IServiceCollection s) => s;
                        public static IServiceCollection AddClaimsPrincipalFactory<T>(this IServiceCollection s) => s;
                    }
                    public interface IScoped { }
                    public sealed class ScopedImpl : IScoped { }
                    public interface IManualFactory { }
                    public sealed class ManualFactory(IScoped scoped) : IManualFactory { }
                    public interface IManualInstance { }
                    public sealed class ManualInstance(IScoped scoped) : IManualInstance { }
                    public sealed class ProbeDb { }
                    public sealed class SingletonTakesDbOptions(Microsoft.EntityFrameworkCore.DbContextOptions<ProbeDb> opts) { }
                    public sealed class MyClaimsFactory { public MyClaimsFactory(IScoped s) { } }

                    public sealed class BlockNoOpSeeder
                    {
                        private readonly int _x = 1;
                        public int Start() => _x;
                        public System.Threading.Tasks.Task StopAsync() { return System.Threading.Tasks.Task.CompletedTask; }
                    }

                    public abstract class BaseWithProp { public abstract bool Flag { get; } }
                    public sealed class StatelessSub : BaseWithProp
                    {
                        public override bool Flag => true;
                        public int Read() => 1;
                        public int Write() => 2;
                    }

                    public interface IUsedContract { void Used(); void NeverCalled(); }
                    public sealed class UsedContract : IUsedContract
                    {
                        public void Used() { }
                        public void NeverCalled() { }
                    }
                }
                """);

            using var extractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snap = await extractor.ExtractAsync(Path.Combine(tempDir, "SemProbe.csproj"), new ExtractionOptions(IncludeMethods: true));

            // DI checks
            var di = analyzer.AnalyzeDi(snap.Graph);
            Assert.DoesNotContain(di.CaptiveDependencies, c => c.ConsumerTypeId == "SemProbe.ManualFactory");
            Assert.DoesNotContain(di.CaptiveDependencies, c => c.ConsumerTypeId == "SemProbe.ManualInstance");
            Assert.Contains(di.CaptiveDependencies, c => c.ConsumerTypeId == "SemProbe.SingletonTakesDbOptions" && c.DependencyTypeId.Contains("DbContextOptions<SemProbe.ProbeDb>"));

            // LCOM4 checks: block-bodied StopAsync no-op and stateless subclass both <= 1
            Assert.True(snap.Graph.FindNode("SemProbe.BlockNoOpSeeder")!.Lcom4 <= 1);
            Assert.True(snap.Graph.FindNode("SemProbe.StatelessSub")!.Lcom4 <= 1);

            // Dead code method scope: IUsedContract.Used() and MyClaimsFactory ctor are live; IUsedContract.NeverCalled() is dead
            var deadMethods = analyzer.AnalyzeDeadCode(snap.Graph, NodeKind.Method, profile: "app");
            Assert.DoesNotContain(deadMethods.Candidates, c => c.FullName.Contains("IUsedContract.Used"));
            Assert.DoesNotContain(deadMethods.Candidates, c => c.FullName.Contains("MyClaimsFactory"));
            Assert.Contains(deadMethods.Candidates, c => c.FullName.Contains("IUsedContract.NeverCalled"));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Cycle8Fixes_MapHandler_AnyKeyConsumer_DbFactoryOptions_HttpClientFactory_MigrationSiblings_SqlNocase_Acknowledged()
    {
        var tempDir = CreateTempDir();
        try
        {
            var analyzer = new ArchitectureAnalyzerService();

            // 1. Migration sibling cycle (OrdinaryFeature -> Migrations.First -> Migrations.Second -> OrdinaryFeature) must NOT be suppressed
            var migNodes = new List<CodeNode>
            {
                new("OrdinaryFeature.Entry", "Entry", "OrdinaryFeature.Entry", NodeKind.Type, "App", "OrdinaryFeature", TypeKind.Class),
                new("OrdinaryFeature.Migrations.First.A", "A", "OrdinaryFeature.Migrations.First.A", NodeKind.Type, "App", "OrdinaryFeature.Migrations.First", TypeKind.Class),
                new("OrdinaryFeature.Migrations.Second.B", "B", "OrdinaryFeature.Migrations.Second.B", NodeKind.Type, "App", "OrdinaryFeature.Migrations.Second", TypeKind.Class)
            };
            var migEdges = new List<DependencyEdge>
            {
                new("OrdinaryFeature.Entry", "OrdinaryFeature.Migrations.First.A", DependencyKind.MethodCall),
                new("OrdinaryFeature.Migrations.First.A", "OrdinaryFeature.Migrations.Second.B", DependencyKind.MethodCall),
                new("OrdinaryFeature.Migrations.Second.B", "OrdinaryFeature.Entry", DependencyKind.MethodCall)
            };
            var migGraph = new CodeGraph(tempDir, migNodes, migEdges);
            var migCheck = analyzer.CheckRules(migGraph, [new NoCyclesRule("CYC-1", "No cycles", "", RuleSeverity.Error, NodeKind.Namespace)]);
            Assert.Single(migCheck.Violations);

            // 2. RoslynCodeGraphExtractor.ResolveTargetFile rejects compiled .dll with ArgumentException
            var fakeDll = Path.Combine(tempDir, "Fake.dll");
            File.WriteAllBytes(fakeDll, [0x4D, 0x5A]);
            var dllEx = Assert.Throws<ArgumentException>(() => RoslynCodeGraphExtractor.ResolveTargetFile(fakeDll));
            Assert.Contains("backend 'auto' or 'il'", dllEx.Message, StringComparison.OrdinalIgnoreCase);

            // 3. GetAnalyzerDiagnostics keeps categories when all diagnostics are hidden by minSeverity
            var diagGraph = new CodeGraph(
                tempDir, [], [],
                analyzerDiagnostics: [new AnalyzerDiagnostic("CA1822", "Mark static", "Performance", RuleSeverity.Info, "msg", null, "App")],
                analyzersLoadedCount: 10,
                availableAnalyzerCategories: ["Design", "Maintainability", "Performance"]);
            var diagRep = analyzer.GetAnalyzerDiagnostics(diagGraph, categories: ["Performance"], minSeverity: RuleSeverity.Warning);
            Assert.Equal(0, diagRep.TotalDiagnostics);
            Assert.Equal(1, diagRep.HiddenBySeverityCount);
            Assert.Contains("Performance", diagRep.Categories);

            // 4. Roslyn extraction: Map() handler, GetKeyedServices(KeyedService.AnyKey), AddDbContextFactory DbContextOptions<T>,
            //    AddHttpClient IHttpClientFactory, { ; ; } LCOM4, MethodCall ConsumerCount from Program, NoDeadCodeRule acknowledged reporting,
            //    and SQL COLLATE NOCASE + syntax typo classification.
            WriteFile(tempDir, "C8Probe.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
                </Project>
                """);
            WriteFile(tempDir, "Program.cs", """
                using C8Probe;
                IServiceCollection services = null!;
                IEndpointRouteBuilder endpoints = null!;
                services.AddKeyedScoped<IKeyedSvc, KeyedScopedImpl>("alpha");
                services.AddSingleton<IAnyKeyHolder>(sp => new AnyKeyHolder(sp.GetKeyedServices<IKeyedSvc>(KeyedService.AnyKey)));
                services.AddDbContextFactory<FactoryDb>(ServiceLifetime.Scoped);
                services.AddSingleton<SingletonTakesFactoryOptions>();
                services.AddHttpClient("default");
                services.AddSingleton<IDispatch, DispatchImpl>();
                endpoints.Map("/ws", RelayHandlers.HandleWs);
                endpoints.MapGet("/run", (IDispatch d) => d.Go());

                namespace Microsoft.EntityFrameworkCore
                {
                    public class DbContextOptions<TContext> { }
                }
                namespace C8Probe
                {
                    public enum ServiceLifetime { Singleton, Scoped, Transient }
                    public static class KeyedService { public static readonly object AnyKey = new(); }
                    public interface IServiceProviderEx
                    {
                        System.Collections.Generic.IEnumerable<T> GetKeyedServices<T>(object? key);
                    }
                    public interface IServiceCollection { }
                    public interface IEndpointRouteBuilder { }
                    public static class DiExt
                    {
                        public static IServiceCollection AddKeyedScoped<T1, T2>(this IServiceCollection s, object key) => s;
                        public static IServiceCollection AddSingleton<T1>(this IServiceCollection s) => s;
                        public static IServiceCollection AddSingleton<T1, T2>(this IServiceCollection s) => s;
                        public static IServiceCollection AddSingleton<T1>(this IServiceCollection s, System.Func<IServiceProviderEx, T1> factory) => s;
                        public static IServiceCollection AddDbContextFactory<TContext>(this IServiceCollection s, ServiceLifetime lifetime) => s;
                        public static IServiceCollection AddHttpClient(this IServiceCollection s, string name) => s;
                        public static void Map(this IEndpointRouteBuilder b, string pattern, System.Action handler) { }
                        public static void MapGet(this IEndpointRouteBuilder b, string pattern, System.Action<IDispatch> handler) { }
                    }

                    public interface IKeyedSvc { }
                    public sealed class KeyedScopedImpl : IKeyedSvc { }
                    public interface IAnyKeyHolder { }
                    public sealed class AnyKeyHolder(System.Collections.Generic.IEnumerable<IKeyedSvc> items) : IAnyKeyHolder { }
                    public sealed class FactoryDb { }
                    public sealed class SingletonTakesFactoryOptions(Microsoft.EntityFrameworkCore.DbContextOptions<FactoryDb> opts) { }

                    public interface IDispatch { void Go(); }
                    public sealed class DispatchImpl : IDispatch { public void Go() { } }

                    public static class RelayHandlers
                    {
                        public static void HandleWs() { }
                        public static void TrulyDeadHandler() { }
                    }

                    public sealed class EmptySemiClass
                    {
                        private readonly int _x = 1;
                        public int Read() => _x;
                        public void NoOp() { ; ; }
                    }

                    internal sealed class AckDeadType { }
                }
                """);

            using var extractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snap = await extractor.ExtractAsync(Path.Combine(tempDir, "C8Probe.csproj"), new ExtractionOptions(IncludeMethods: true));

            var di = analyzer.AnalyzeDi(snap.Graph);
            Assert.Contains(di.CaptiveDependencies, c => c.ConsumerTypeId == "C8Probe.AnyKeyHolder" && c.ResolvedImplementationTypeId == "C8Probe.KeyedScopedImpl");
            Assert.Contains(di.CaptiveDependencies, c => c.ConsumerTypeId == "C8Probe.SingletonTakesFactoryOptions" && c.DependencyTypeId.Contains("DbContextOptions<C8Probe.FactoryDb>"));
            Assert.Contains(di.Registrations, r => r.ServiceTypeId == "Microsoft.Extensions.Http.IHttpClientFactory" && r.Lifetime == ServiceLifetimeKind.Singleton);
            var dispatchSingle = di.SingleImplementationInterfaces.First(s => s.InterfaceTypeId == "C8Probe.IDispatch");
            Assert.True(dispatchSingle.ConsumerCount >= 1);

            Assert.True(snap.Graph.FindNode("C8Probe.EmptySemiClass")!.Lcom4 <= 1);

            var deadMethods = analyzer.AnalyzeDeadCode(snap.Graph, NodeKind.Method, profile: "app");
            Assert.DoesNotContain(deadMethods.Candidates, c => c.FullName.Contains("RelayHandlers.HandleWs"));
            Assert.Contains(deadMethods.Candidates, c => c.FullName.Contains("RelayHandlers.TrulyDeadHandler"));

            // NoDeadCodeRule surfaces acknowledged symbols separately on RuleCheckReport
            var ackRule = new NoDeadCodeRule("DEAD-1", "No dead code", "", RuleSeverity.Warning,
                acknowledged: [new DeadCodeAcknowledgement("C8Probe.AckDeadType", "Reflection loaded")]);
            var ackCheck = analyzer.CheckRules(snap.Graph, [ackRule]);
            Assert.DoesNotContain(ackCheck.Violations, v => v.NodeId == "C8Probe.AckDeadType");
            Assert.NotNull(ackCheck.AcknowledgedDeadCode);
            Assert.Contains(ackCheck.AcknowledgedDeadCode!, a => a.Symbol == "C8Probe.AckDeadType");

            // SQL COLLATE NOCASE matches camelCase/lowercase enum filters + typo gives ArgumentException
            var sqlEngine = new GraphSqlQueryEngine();
            var lowerNodes = sqlEngine.Execute(snap.Graph, "SELECT COUNT(*) AS c FROM nodes WHERE kind = 'type'");
            var pascalNodes = sqlEngine.Execute(snap.Graph, "SELECT COUNT(*) AS c FROM nodes WHERE kind = 'Type'");
            Assert.Equal(pascalNodes.Rows[0]["c"], lowerNodes.Rows[0]["c"]);
            Assert.True(Convert.ToInt64(lowerNodes.Rows[0]["c"]) > 0);
            var lowerDi = sqlEngine.Execute(snap.Graph, "SELECT COUNT(*) AS c FROM di_registrations WHERE lifetime = 'singleton'");
            Assert.True(Convert.ToInt64(lowerDi.Rows[0]["c"]) > 0);

            var typoEx = Assert.Throws<ArgumentException>(() => GraphSqlQueryEngine.ValidateReadOnlySql("SELEC name FROM metrics"));
            Assert.Contains("Did you mean SELECT", typoEx.Message);
            Assert.Throws<InvalidOperationException>(() => GraphSqlQueryEngine.ValidateReadOnlySql("DELETE FROM nodes"));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Cycle9Fixes_MethodBodyCompileError_OverlappingForbid_ImpactInterfaceBridge_AnalyzersNamespaceFilter_YamlStrictKeys()
    {
        var tempDir = CreateTempDir();
        try
        {
            var analyzer = new ArchitectureAnalyzerService();

            // 1. Overlapping forbid_dependency: narrower from ("App.Features.Cart.*") inside broader to ("App.Features.*")
            //    must STILL flag Cart -> Orders while excluding Cart -> Cart!
            var forbidNodes = new List<CodeNode>
            {
                new("App.Features.Cart.CartService", "CartService", "App.Features.Cart.CartService", NodeKind.Type, "App", "App.Features.Cart", TypeKind.Class),
                new("App.Features.Cart.CartHelper", "CartHelper", "App.Features.Cart.CartHelper", NodeKind.Type, "App", "App.Features.Cart", TypeKind.Class),
                new("App.Features.Orders.OrderInternals", "OrderInternals", "App.Features.Orders.OrderInternals", NodeKind.Type, "App", "App.Features.Orders", TypeKind.Class)
            };
            var forbidEdges = new List<DependencyEdge>
            {
                new("App.Features.Cart.CartService", "App.Features.Cart.CartHelper", DependencyKind.MethodCall),
                new("App.Features.Cart.CartService", "App.Features.Orders.OrderInternals", DependencyKind.FieldOrProperty)
            };
            var forbidGraph = new CodeGraph(tempDir, forbidNodes, forbidEdges);
            var overlapRule = new ForbidDependencyRule("OV-1", "Cart isolated", "", RuleSeverity.Error, "App.Features.Cart.*", ["App.Features.*"]);
            var overlapViolations = analyzer.CheckRules(forbidGraph, [overlapRule]).Violations;
            Assert.Single(overlapViolations);
            Assert.Equal("App.Features.Orders.OrderInternals", overlapViolations[0].TargetNodeId);

            // 2. Method-level impact bridges implementing method -> interface method callers
            var impactNodes = new List<CodeNode>
            {
                new("App.IGameEngine", "IGameEngine", "App.IGameEngine", NodeKind.Type, "App", "App", TypeKind.Interface),
                new("App.GameEngine", "GameEngine", "App.GameEngine", NodeKind.Type, "App", "App", TypeKind.Class),
                new("App.Caller", "Caller", "App.Caller", NodeKind.Type, "App", "App", TypeKind.Class),
                new("App.IGameEngine.ApplyMove(int)", "ApplyMove", "App.IGameEngine.ApplyMove(int)", NodeKind.Method, "App", "App", ContainingTypeId: "App.IGameEngine"),
                new("App.GameEngine.ApplyMove(int)", "ApplyMove", "App.GameEngine.ApplyMove(int)", NodeKind.Method, "App", "App", ContainingTypeId: "App.GameEngine"),
                new("App.Caller.Run()", "Run", "App.Caller.Run()", NodeKind.Method, "App", "App", ContainingTypeId: "App.Caller")
            };
            var impactEdges = new List<DependencyEdge>
            {
                new("App.GameEngine", "App.IGameEngine", DependencyKind.Implements),
                new("App.Caller.Run()", "App.IGameEngine.ApplyMove(int)", DependencyKind.MethodCall)
            };
            var impactGraph = new CodeGraph(tempDir, impactNodes, impactEdges, methodsIncluded: true);
            var impact = analyzer.AnalyzeImpact(impactGraph, "App.GameEngine.ApplyMove(int)")!;
            Assert.Equal(1, impact.DirectDependentsCount);
            Assert.Contains(impact.ImpactedNodes, n => n.NodeId == "App.Caller.Run()");

            // 3. analyzers --filter matches type/namespace via file path AND gives honest non-match note
            var anNodes = new List<CodeNode>
            {
                new("App.Infrastructure.Repo", "Repo", "App.Infrastructure.Repo", NodeKind.Type, "App.Infra", "App.Infrastructure", TypeKind.Class,
                    Location: new SourceLocation("src/App.Infra/Repo.cs", 1, 1))
            };
            var anDiags = new List<AnalyzerDiagnostic>
            {
                new("CA1822", "Mark static", "Performance", RuleSeverity.Warning, "Make static", new SourceLocation("src/App.Infra/Repo.cs", 10, 5), "App.Infra")
            };
            var anGraph = new CodeGraph(tempDir, anNodes, [], analyzerDiagnostics: anDiags, analyzersLoadedCount: 5, availableAnalyzerCategories: ["Performance"]);
            var matchedAn = analyzer.GetAnalyzerDiagnostics(anGraph, categories: ["Performance"], minSeverity: RuleSeverity.Warning, filterPattern: "App.Infrastructure.*");
            Assert.Equal(1, matchedAn.TotalDiagnostics);
            var unmatchedAn = analyzer.GetAnalyzerDiagnostics(anGraph, categories: ["Performance"], minSeverity: RuleSeverity.Warning, filterPattern: "App.DoesNotExist.*");
            Assert.Equal(0, unmatchedAn.TotalDiagnostics);
            Assert.Contains("matched filter 'App.DoesNotExist.*'", unmatchedAn.Note ?? string.Empty);

            // 4. YAML strict keys & version validation
            var yamlProvider = new YamlRuleSetProvider();
            var badVer = Assert.Throws<InvalidDataException>(() => yamlProvider.ParseYaml("version: 999\nrules: []\n"));
            Assert.Contains("Unsupported .archlens.yaml version '999'", badVer.Message);
            var typoKey = Assert.Throws<InvalidDataException>(() => yamlProvider.ParseYaml("version: 1\npresett: ddd\n"));
            Assert.Contains("Unknown YAML key 'presett'", typoKey.Message);
            Assert.DoesNotContain("ArchLensYamlConfig", typoKey.Message);

            // 5. IL extractor throws NotSupportedException (not FileNotFoundException) for existing .cs file
            var csPath = Path.Combine(tempDir, "OnlyOne.cs");
            File.WriteAllText(csPath, "class C {}");
            var ilExtractor = new MetadataCodeGraphExtractor();
            await Assert.ThrowsAsync<NotSupportedException>(() => ilExtractor.ExtractAsync(csPath));

            // 6. Roslyn extractor marks method-body compile errors (CS0103) as Degraded
            var brokenDir = Path.Combine(tempDir, "BrokenMethod");
            WriteFile(brokenDir, "BrokenMethod.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            WriteFile(brokenDir, "Program.cs", "public static class Program { public static void Main() { MissingMethod(); } }");
            using var roslyn = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var brokenSnap = await roslyn.ExtractAsync(Path.Combine(brokenDir, "BrokenMethod.csproj"));
            Assert.True(brokenSnap.Graph.LoadStatus?.Degraded);
            Assert.Contains(brokenSnap.Graph.Diagnostics, d => d.Message.Contains("CS0103", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Cycle10Fixes_EditorConfigErrorPreserved_UseMiddlewareLive_PublicApiOverride_YamlGlobAck_LineCountExact()
    {
        var tempDir = CreateTempDir();
        try
        {
            var analyzer = new ArchitectureAnalyzerService();

            // 1. YAML top-level exclude and acknowledge validate globs
            var yamlProvider = new YamlRuleSetProvider();
            Assert.Throws<InvalidOperationException>(() => yamlProvider.ParseYaml("version: 1\nexclude:\n  - \"[\"\nrules: []\n"));
            Assert.Throws<InvalidOperationException>(() => yamlProvider.ParseYaml("version: 1\nacknowledge:\n  - symbol: \"[\"\n    reason: bad\nrules: []\n"));

            // 2. DeadCodeAnalyzer explicit treatPublicApiOverride overrides explicit profile
            var libProj = new CodeNode("project:Lib", "Lib", "Lib", NodeKind.Project, "Lib",
                Metadata: new Dictionary<string, string> { ["outputKind"] = "DynamicallyLinkedLibrary" });
            var pubType = new CodeNode("Lib.PublicDead", "PublicDead", "Lib.PublicDead", NodeKind.Type, "Lib", "Lib", TypeKind.Class, AccessibilityKind.Public,
                Metadata: new Dictionary<string, string> { ["outputKind"] = "DynamicallyLinkedLibrary" });
            var libGraph = new CodeGraph(tempDir, [libProj, pubType], []);

            var libWithFalse = analyzer.AnalyzeDeadCode(libGraph, NodeKind.Type, profile: "lib", treatPublicApiOverride: false);
            Assert.Contains(libWithFalse.Candidates, c => c.NodeId == "Lib.PublicDead");
            var appWithTrue = analyzer.AnalyzeDeadCode(libGraph, NodeKind.Type, profile: "app", treatPublicApiOverride: true);
            Assert.DoesNotContain(appWithTrue.Candidates, c => c.NodeId == "Lib.PublicDead");

            // 3. Convention-based UseMiddleware<T>() keeps ctor, InvokeAsync, and private helper live + exact top-level Program LOC
            var mwDir = Path.Combine(tempDir, "MiddlewareApp");
            WriteFile(mwDir, "MiddlewareApp.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
                </Project>
                """);
            var programSrc = string.Join("\n", [
                "using MiddlewareApp;",
                "IApplicationBuilder app = null!;",
                "app.UseMiddleware<SessionMiddleware>();",
                "namespace MiddlewareApp",
                "{",
                "    public interface IApplicationBuilder { }",
                "    public static class AppExt { public static IApplicationBuilder UseMiddleware<T>(this IApplicationBuilder b) => b; }",
                "    public sealed class SessionMiddleware(string key)",
                "    {",
                "        public void InvokeAsync() => Helper();",
                "        private void Helper() { _ = key; }",
                "        private void TrulyDead() { }",
                "    }",
                "}"
            ]) + "\n";
            WriteFile(mwDir, "Program.cs", programSrc);

            using var roslyn = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var mwSnap = await roslyn.ExtractAsync(Path.Combine(mwDir, "MiddlewareApp.csproj"), new ExtractionOptions(IncludeMethods: true));
            var mwDead = analyzer.AnalyzeDeadCode(mwSnap.Graph, NodeKind.Method, profile: "app");
            Assert.DoesNotContain(mwDead.Candidates, c => c.FullName.Contains("SessionMiddleware.SessionMiddleware"));
            Assert.DoesNotContain(mwDead.Candidates, c => c.FullName.Contains("SessionMiddleware.InvokeAsync"));
            Assert.DoesNotContain(mwDead.Candidates, c => c.FullName.Contains("SessionMiddleware.Helper"));
            Assert.Contains(mwDead.Candidates, c => c.FullName.Contains("SessionMiddleware.TrulyDead"));

            // Exact physical line count for top-level Program.cs (14 lines, not 15)
            var progNode = mwSnap.Graph.FindNode("MiddlewareApp.Program")!;
            Assert.Equal(14, progNode.LineCount);

            // 4. --elevate-warnings must NEVER downgrade .editorconfig severity=error (CA1822) to Warning
            var anDir = Path.Combine(tempDir, "AnalyzerErrorProj");
            WriteFile(anDir, ".editorconfig", "root = true\n\n[*.cs]\ndotnet_diagnostic.CA1822.severity = error\n");
            WriteFile(anDir, "AnalyzerErrorProj.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Library</OutputType>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <AnalysisLevel>latest-recommended</AnalysisLevel>
                  </PropertyGroup>
                </Project>
                """);
            WriteFile(anDir, "Code.cs", "namespace AnalyzerErrorProj; public sealed class Sample { public int Compute() => 42; }\n");

            var anSnap = await roslyn.ExtractAsync(
                Path.Combine(anDir, "AnalyzerErrorProj.csproj"),
                new ExtractionOptions(IncludeAnalyzerDiagnostics: true, AnalyzerCategories: ["Performance"], ElevateAnalyzerSeverity: true));
            var ca1822 = anSnap.Graph.AnalyzerDiagnostics.FirstOrDefault(d => d.Id == "CA1822");
            Assert.NotNull(ca1822);
            Assert.Equal(RuleSeverity.Error, ca1822!.Severity);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private sealed class ConcurrencyTrackingExtractor(CodeGraph graph) : ICodeGraphExtractor
    {
        private int _active;
        public int MaxConcurrent { get; private set; }
        public string Name => "Stub";
        public bool CanHandle(string targetPath) => true;
        public async Task<ArchitectureSnapshot> ExtractAsync(string targetPath, ExtractionOptions? options = null, CancellationToken cancellationToken = default)
        {
            var current = Interlocked.Increment(ref _active);
            if (current > MaxConcurrent) MaxConcurrent = current;
            try
            {
                await Task.Delay(80, cancellationToken);
                return new ArchitectureSnapshot("conc-snap", targetPath, DateTimeOffset.UtcNow, graph);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class StubGraphExtractor(CodeGraph graph) : ICodeGraphExtractor
    {
        public string Name => "Stub";
        public bool CanHandle(string targetPath) => true;
        public Task<ArchitectureSnapshot> ExtractAsync(string targetPath, ExtractionOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ArchitectureSnapshot("stub-snap", targetPath, DateTimeOffset.UtcNow, graph));
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
