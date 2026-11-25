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
