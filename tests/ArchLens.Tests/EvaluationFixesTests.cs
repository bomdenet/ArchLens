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

            // 3. Unknown max_metric marks ConfigurationError = true (not VacuousConfiguration)
            var badMetricRules = new YamlRuleSetProvider().ParseYaml("""
                version: 1
                rules:
                  - id: BAD-M
                    type: max_metric
                    metric: nonexistent_metric
                    threshold: 5
                """);
            var badMetricReport = analyzer.CheckRules(snap.Graph, badMetricRules);
            Assert.True(badMetricReport.ConfigurationError);
            Assert.False(badMetricReport.VacuousConfiguration);
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

            // 2. RoslynCodeGraphExtractor.ResolveTargetFile rejects compiled .dll with NotSupportedException (maps to unsupported_target in MCP)
            var fakeDll = Path.Combine(tempDir, "Fake.dll");
            File.WriteAllBytes(fakeDll, [0x4D, 0x5A]);
            var dllEx = Assert.Throws<NotSupportedException>(() => RoslynCodeGraphExtractor.ResolveTargetFile(fakeDll));
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
            Assert.Contains(di.Registrations, r => r.ServiceTypeId == "System.Net.Http.IHttpClientFactory" && r.Lifetime == ServiceLifetimeKind.Singleton);
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

    [Fact]
    public async Task Cycle11Fixes_TypedKeyedDiContainerScopeMultiCtorScopedFactoryCaseSensitiveCyclesHybridRazorAndMcpGuards()
    {
        var tempDir = CreateTempDir();
        try
        {
            var analyzer = new ArchitectureAnalyzerService();

            // 1. Typed keyed DI (int 1 vs string "1"), isolated local ServiceCollections,
            //    multi-constructor satisfiable selection, scoped factory CreateScope(), and hybrid .razor + .razor.cs
            var diDir = Path.Combine(tempDir, "DiAndRazor");
            WriteFile(diDir, "DiAndRazor.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);
            WriteFile(diDir, "DiStubs.cs", """
                namespace Microsoft.Extensions.DependencyInjection
                {
                    public interface IServiceCollection { }
                    public sealed class ServiceCollection : IServiceCollection { }
                    public interface IServiceScope : System.IDisposable { System.IServiceProvider ServiceProvider { get; } }
                    [System.AttributeUsage(System.AttributeTargets.Parameter)]
                    public sealed class FromKeyedServicesAttribute(object key) : System.Attribute { public object Key { get; } = key; }
                    public static class DiExtensions
                    {
                        public static IServiceCollection AddSingleton<T>(this IServiceCollection s) => s;
                        public static IServiceCollection AddSingleton<T>(this IServiceCollection s, System.Func<System.IServiceProvider, T> f) => s;
                        public static IServiceCollection AddScoped<T>(this IServiceCollection s) => s;
                        public static IServiceCollection AddKeyedScoped<TService, TImpl>(this IServiceCollection s, object key) => s;
                        public static IServiceCollection AddKeyedSingleton<TService, TImpl>(this IServiceCollection s, object key) => s;
                        public static IServiceScope CreateScope(this System.IServiceProvider sp) => null!;
                        public static T GetRequiredService<T>(this System.IServiceProvider sp) => default!;
                    }
                }
                """);
            WriteFile(diDir, "Program.cs", """
                using Microsoft.Extensions.DependencyInjection;
                namespace DiAndRazor;

                public interface IKey { }
                public sealed class IntScopedKey : IKey { }
                public sealed class StrSingletonKey : IKey { }
                public sealed class NumericConsumer([FromKeyedServices(1)] IKey k) { }
                public sealed class StringConsumer([FromKeyedServices("1")] IKey k) { }

                public sealed class ScopedDep { }
                public sealed class UnregisteredArg { }
                public sealed class MultiCtorSafe
                {
                    public MultiCtorSafe() { }
                    public MultiCtorSafe(ScopedDep dep, UnregisteredArg missing) { }
                }
                public sealed class MultiCtorCaptive
                {
                    public MultiCtorCaptive(ScopedDep dep) { }
                    public MultiCtorCaptive(ScopedDep dep, UnregisteredArg missing) { }
                }
                public sealed class ScopedFactorySafe { }
                public sealed class ContainerBConsumer(ScopedDep dep) { }

                public static class Program
                {
                    public static void Main()
                    {
                        var main = new ServiceCollection();
                        main.AddKeyedScoped<IKey, IntScopedKey>(1);
                        main.AddKeyedSingleton<IKey, StrSingletonKey>("1");
                        main.AddSingleton<NumericConsumer>();
                        main.AddSingleton<StringConsumer>();
                        main.AddScoped<ScopedDep>();
                        main.AddSingleton<MultiCtorSafe>();
                        main.AddSingleton<MultiCtorCaptive>();
                        main.AddSingleton<ScopedFactorySafe>(sp =>
                        {
                            using var scope = sp.CreateScope();
                            _ = scope.ServiceProvider.GetRequiredService<ScopedDep>();
                            return new ScopedFactorySafe();
                        });

                        // Separate local ServiceCollection must NOT cross-resolve ScopedDep from 'main'
                        var isolated = new ServiceCollection();
                        isolated.AddSingleton<ContainerBConsumer>();
                    }
                }
                """);
            WriteFile(diDir, "Hybrid.razor", """
                @page "/hybrid"
                <button @onclick="InlineHandler">Click</button>
                @code {
                    private int _clicks;
                    private void InlineHandler()
                    {
                        if (_clicks > 0 && _clicks < 10)
                        {
                            _clicks++;
                        }
                    }
                    private void UnusedInlineHelper()
                    {
                        if (_clicks == 99) { _clicks = 0; }
                    }
                }
                """);
            WriteFile(diDir, "Hybrid.razor.cs", """
                namespace DiAndRazor;
                public partial class Hybrid
                {
                    public void CodeBehindMethod() { }
                }
                """);

            using var roslyn = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snap = await roslyn.ExtractAsync(Path.Combine(diDir, "DiAndRazor.csproj"), new ExtractionOptions(IncludeMethods: true));

            var di = analyzer.AnalyzeDi(snap.Graph);
            // Keyed int 1 vs string "1" must NOT collide
            Assert.Contains(di.CaptiveDependencies, c => c.ConsumerTypeId == "DiAndRazor.NumericConsumer" && c.ResolvedImplementationTypeId == "DiAndRazor.IntScopedKey");
            Assert.DoesNotContain(di.CaptiveDependencies, c => c.ConsumerTypeId == "DiAndRazor.StringConsumer");
            // Multi-constructor: picks longest satisfiable ctor
            Assert.DoesNotContain(di.CaptiveDependencies, c => c.ConsumerTypeId == "DiAndRazor.MultiCtorSafe");
            Assert.Contains(di.CaptiveDependencies, c => c.ConsumerTypeId == "DiAndRazor.MultiCtorCaptive");
            // Scoped factory using CreateScope() is scope-safe
            Assert.DoesNotContain(di.CaptiveDependencies, c => c.ConsumerTypeId == "DiAndRazor.ScopedFactorySafe");
            // Separate local ServiceCollection does not cross-resolve
            Assert.DoesNotContain(di.CaptiveDependencies, c => c.ConsumerTypeId == "DiAndRazor.ContainerBConsumer");

            // Hybrid .razor + .razor.cs merges @code metrics & methods
            var hybridNode = snap.Graph.FindNode("DiAndRazor.Hybrid")!;
            Assert.True(hybridNode.CyclomaticComplexity >= 5, $"Expected merged @code cyclomatic complexity, got {hybridNode.CyclomaticComplexity}");
            Assert.True(hybridNode.LineCount >= 18, $"Expected merged .razor + .razor.cs LOC, got {hybridNode.LineCount}");
            var deadMethods = analyzer.AnalyzeDeadCode(snap.Graph, NodeKind.Method, profile: "app");
            Assert.DoesNotContain(deadMethods.Candidates, c => c.FullName.Contains("Hybrid.InlineHandler"));
            Assert.Contains(deadMethods.Candidates, c => c.FullName.Contains("Hybrid.UnusedInlineHelper"));

            // 2. Case-sensitive namespace cycle suppression (Audit.Upper vs Audit.upper.Child are distinct C# namespaces)
            var caseNodes = new List<CodeNode>
            {
                new("Audit.Upper.A", "A", "Audit.Upper.A", NodeKind.Type, "P", "Audit.Upper", TypeKind.Class),
                new("Audit.upper.Child.B", "B", "Audit.upper.Child.B", NodeKind.Type, "P", "Audit.upper.Child", TypeKind.Class)
            };
            var caseEdges = new List<DependencyEdge>
            {
                new("Audit.Upper.A", "Audit.upper.Child.B", DependencyKind.TypeReference),
                new("Audit.upper.Child.B", "Audit.Upper.A", DependencyKind.TypeReference)
            };
            var caseGraph = new CodeGraph("C:/repo", caseNodes, caseEdges);
            var caseCheck = analyzer.CheckRules(caseGraph, [new NoCyclesRule("CYC", "No cycles", "", RuleSeverity.Error, NodeKind.Namespace)]);
            Assert.Single(caseCheck.Violations);

            // 3. BaselineStore warns when comparing against a degraded baseline
            var degradedStatus = new GraphLoadStatus("roslyn", 2, 1, 1, true, DegradedProjects: ["Broken"]);
            var degSnap = new ArchitectureSnapshot("deg-1", "C:/repo", DateTimeOffset.UtcNow, new CodeGraph("C:/repo", [], [], loadStatus: degradedStatus));
            var cleanSnap = new ArchitectureSnapshot("clean-2", "C:/repo", DateTimeOffset.UtcNow, new CodeGraph("C:/repo", [], [], loadStatus: new GraphLoadStatus("roslyn", 2, 2, 0, false)));
            var scopeWarn = BaselineStore.GetScopeMismatchWarning(degSnap, cleanSnap);
            Assert.NotNull(scopeWarn);
            Assert.Contains("degraded workspace", scopeWarn);

            // 4. GraphVisualizer emits loadStatus comment for approximate/degraded graphs
            var ilGraph = new CodeGraph("C:/repo", caseNodes, caseEdges, loadStatus: new GraphLoadStatus("il", 1, 1, 0, false, Approximate: true, Note: "IL approximate"));
            Assert.Contains("%% LoadStatus: backend=il", GraphVisualizer.Export(ilGraph, "mermaid"));
            Assert.Contains("// LoadStatus: backend=il", GraphVisualizer.Export(ilGraph, "dot"));
            Assert.Contains("// LoadStatus: backend=il", GraphVisualizer.Export(ilGraph, "structurizr"));

            // 5. MCP query allows schema_info without path; dependency_graph omits emptyWindowNote when maxEdges=0
            using var sessions = new WorkspaceSessionManager([new StubGraphExtractor(caseGraph)]);
            var tools = new ArchLensTools(sessions, analyzer, new GraphSqlQueryEngine(), [new YamlRuleSetProvider()]);
            var schemaSqlRes = await tools.QueryGraphSqlAsync(path: null, sql: "SELECT table_name FROM schema_info WHERE table_name = 'nodes'");
            Assert.NotEqual(true, schemaSqlRes.IsError);
            Assert.Contains("nodes", ((ModelContextProtocol.Protocol.TextContentBlock)schemaSqlRes.Content[0]).Text);

            var ilAnRes = await tools.CheckRulesAsync(diDir, includeAnalyzers: true, backend: "il");
            Assert.True(ilAnRes.IsError);
            var ilAnText = ((ModelContextProtocol.Protocol.TextContentBlock)ilAnRes.Content[0]).Text;
            Assert.Contains("Roslyn source extraction", ilAnText);
            Assert.DoesNotContain(".dll", ilAnText);

            var depZeroEdges = analyzer.GetDependencyGraph(caseGraph, NodeKind.Namespace, maxEdges: 0);
            var depZeroJson = JsonSerializer.Serialize(McpResponseFormatter.FormatDependencyGraph(depZeroEdges, "compact", false, null, requestedMaxEdges: 0), McpResponseFormatter.JsonOptions);
            using var depDoc = JsonDocument.Parse(depZeroJson);
            Assert.False(depDoc.RootElement.TryGetProperty("note", out _));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Cycle12Fixes_ImpactMixedRegistrarDegradedCyclesYamlMethodScopeDiBranchesXamlHybridRazorAndMcpPrompts()
    {
        var tempDir = CreateTempDir();
        try
        {
            var analyzer = new ArchitectureAnalyzerService();
            var yaml = new YamlRuleSetProvider();

            // 1. Impact keeps real consumer that ALSO registers target in DI (ObjectCreation + MethodCall)
            var impNodes = new List<CodeNode>
            {
                new("App.Entry", "Entry", "App.Entry", NodeKind.Type, "App", "App", TypeKind.Class),
                new("App.Stable", "Stable", "App.Stable", NodeKind.Type, "App", "App", TypeKind.Class),
                new("App.RegOnly", "RegOnly", "App.RegOnly", NodeKind.Type, "App", "App", TypeKind.Class)
            };
            var impEdges = new List<DependencyEdge>
            {
                new("App.Entry", "App.Stable", DependencyKind.GenericArgument),
                new("App.Entry", "App.Stable", DependencyKind.ObjectCreation),
                new("App.Entry", "App.Stable", DependencyKind.MethodCall),
                new("App.Entry", "App.RegOnly", DependencyKind.GenericArgument)
            };
            var impRegs = new List<DiRegistration>
            {
                new("App.Stable", "App.Stable", ServiceLifetimeKind.Singleton, "App.Entry"),
                new("App.RegOnly", "App.RegOnly", ServiceLifetimeKind.Transient, "App.Entry")
            };
            var impGraph = new CodeGraph("C:/repo", impNodes, impEdges, diRegistrations: impRegs);
            var stableImpact = analyzer.AnalyzeImpact(impGraph, "App.Stable", excludeRegistrations: true)!;
            Assert.Equal(1, stableImpact.DirectDependentsCount);
            Assert.Contains(stableImpact.ImpactedNodes, n => n.NodeId == "App.Entry" && n.Category == "consumer");
            var regOnlyImpact = analyzer.AnalyzeImpact(impGraph, "App.RegOnly", excludeRegistrations: true)!;
            Assert.Equal(0, regOnlyImpact.DirectDependentsCount);
            Assert.Equal(1, regOnlyImpact.ExcludedRegistrationCount);

            // 2. check_rules(excludeDegradedProjects: true) excludes namespace cycles wholly inside a degraded project
            var degNodes = new List<CodeNode>
            {
                new("ns:Bad.A", "A", "Bad.A", NodeKind.Namespace, "Bad", "Bad.A"),
                new("ns:Bad.B", "B", "Bad.B", NodeKind.Namespace, "Bad", "Bad.B"),
                new("Bad.A.T1", "T1", "Bad.A.T1", NodeKind.Type, "Bad", "Bad.A", TypeKind.Class),
                new("Bad.B.T2", "T2", "Bad.B.T2", NodeKind.Type, "Bad", "Bad.B", TypeKind.Class),
                new("Good.A.G1", "G1", "Good.A.G1", NodeKind.Type, "Good", "Good.A", TypeKind.Class)
            };
            var degEdges = new List<DependencyEdge>
            {
                new("Bad.A.T1", "Bad.B.T2", DependencyKind.FieldOrProperty),
                new("Bad.B.T2", "Bad.A.T1", DependencyKind.FieldOrProperty)
            };
            var degStatus = new GraphLoadStatus("roslyn", 2, 1, 1, true, DegradedProjects: ["Bad"]);
            var degGraph = new CodeGraph("C:/repo", degNodes, degEdges, loadStatus: degStatus);
            var degCheck = analyzer.CheckRules(degGraph, [new NoCyclesRule("CYC", "No cycles", "", RuleSeverity.Error, NodeKind.Namespace)], excludeDegradedProjects: true);
            Assert.Empty(degCheck.Violations);
            Assert.Contains("Bad", degCheck.SkippedDegradedProjects ?? []);

            // 3. analyzers type filter respects line ranges within multi-type files
            var spanNodes = new List<CodeNode>
            {
                new("App.Stable", "Stable", "App.Stable", NodeKind.Type, "App", "App", TypeKind.Class, Location: new SourceLocation("App/Stable.cs", 2, 1), LineCount: 10),
                new("App.Other", "Other", "App.Other", NodeKind.Type, "App", "App", TypeKind.Class, Location: new SourceLocation("App/Stable.cs", 15, 1), LineCount: 3)
            };
            var spanDiags = new List<AnalyzerDiagnostic>
            {
                new("CA1051", "Fields", "Design", RuleSeverity.Error, "Field", new SourceLocation("App/Stable.cs", 5, 1), "App")
            };
            var spanGraph = new CodeGraph("C:/repo", spanNodes, [], projects: [new ArchLens.Model.ProjectInfo("App", "App.csproj", "net10.0", [], [], 2, 1)], analyzerDiagnostics: spanDiags, analyzersLoadedCount: 10);
            Assert.Empty(analyzer.GetAnalyzerDiagnostics(spanGraph, categories: ["all"], filterPattern: "App.Other").Diagnostics);
            Assert.Single(analyzer.GetAnalyzerDiagnostics(spanGraph, categories: ["all"], filterPattern: "App.Stable").Diagnostics);

            // 4. Case-exact EF Migrations suppression (lowercase .migrations is NOT suppressed)
            var migNodes = new List<CodeNode>
            {
                new("CaseProbe.Root", "Root", "CaseProbe.Root", NodeKind.Type, "P", "CaseProbe", TypeKind.Class),
                new("CaseProbe.migrations.Node", "Node", "CaseProbe.migrations.Node", NodeKind.Type, "P", "CaseProbe.migrations", TypeKind.Class)
            };
            var migEdges = new List<DependencyEdge>
            {
                new("CaseProbe.Root", "CaseProbe.migrations.Node", DependencyKind.FieldOrProperty),
                new("CaseProbe.migrations.Node", "CaseProbe.Root", DependencyKind.FieldOrProperty)
            };
            var migCheck = analyzer.CheckRules(new CodeGraph("C:/repo", migNodes, migEdges), [new NoCyclesRule("CYC", "No cycles", "", RuleSeverity.Error, NodeKind.Namespace, excludeMigrations: true, excludeParentChild: false)]);
            Assert.Single(migCheck.Violations);

            // 5. YAML max_metric rejects lcom4 at method scope, and no_dead_code honors treat_public_api_as_entry_point override with profile
            Assert.Throws<InvalidOperationException>(() => yaml.ParseYaml("""
                version: 1
                rules:
                  - id: BAD-LCOM
                    type: max_metric
                    metric: lcom4
                    threshold: 1
                    scope: method
                """));

            var deadRuleOverride = yaml.ParseYaml("""
                version: 1
                rules:
                  - id: DEAD-1
                    type: no_dead_code
                    profile: app
                    treat_public_api_as_entry_point: true
                """);
            var pubLibGraph = new CodeGraph("C:/repo",
                [
                    new CodeNode("App.Program", "Program", "App.Program", NodeKind.Type, "App", "App", TypeKind.Class, AccessibilityKind.Public, Metadata: new Dictionary<string, string> { ["outputKind"] = "ConsoleApplication", ["isEntryPointMain"] = "true" }),
                    new CodeNode("Lib.PublicApi", "PublicApi", "Lib.PublicApi", NodeKind.Type, "Lib", "Lib", TypeKind.Class, AccessibilityKind.Public, Metadata: new Dictionary<string, string> { ["outputKind"] = "DynamicallyLinkedLibrary" })
                ], []);
            Assert.Empty(analyzer.CheckRules(pubLibGraph, deadRuleOverride).Violations);

            // 6. Quoted "schema_info" works without path; emptyWindowNote shows on maxNodes=1 but not maxEdges=0; prompts reject empty path and adapt for DLL
            Assert.True(GraphSqlQueryEngine.IsSchemaInfoOnlyQuery("SELECT * FROM \"schema_info\" LIMIT 1"));
            var oneNodeGraph = analyzer.GetDependencyGraph(impGraph, NodeKind.Type, maxNodes: 1);
            var oneNodeJson = JsonSerializer.Serialize(McpResponseFormatter.FormatDependencyGraph(oneNodeGraph, "compact", false, null, requestedMaxEdges: 20), McpResponseFormatter.JsonOptions);
            Assert.Contains("0 edges connect nodes strictly within", oneNodeJson);
            Assert.Throws<ArgumentException>(() => ArchLensPrompts.AuditArchitecture("   "));
            var dllPrompt = ArchLensPrompts.AuditArchitecture("C:\\app\\MyApp.dll", "custom-style");
            Assert.DoesNotContain("includeAnalyzers: true", dllPrompt);
            Assert.DoesNotContain("di_analysis(", dllPrompt);
            Assert.DoesNotContain("preset: \"custom-style\"", dllPrompt);

            // 7. DI conditional branches (if/else) + XAML ResourceDictionary + broken <Import> detection + in-solution dead interface impl
            var projDir = Path.Combine(tempDir, "CondDiAndXaml");
            WriteFile(projDir, "CondDiAndXaml.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);
            WriteFile(projDir, "Templates.xaml", """
                <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                    xmlns:views="clr-namespace:CondDiAndXaml">
                  <DataTemplate><views:MainPageView /></DataTemplate>
                  <Style TargetType="Button"><Setter Property="views:AttachedProps.Header" Value="Hi" /></Style>
                </ResourceDictionary>
                """);
            WriteFile(projDir, "Code.cs", """
                namespace Microsoft.Extensions.DependencyInjection
                {
                    public interface IServiceCollection { }
                    public sealed class ServiceCollection : IServiceCollection { }
                    public static class Ext
                    {
                        public static IServiceCollection AddSingleton<TService, TImpl>(this IServiceCollection s) => s;
                        public static IServiceCollection AddScoped<TService, TImpl>(this IServiceCollection s) => s;
                        public static IServiceCollection AddScoped<T>(this IServiceCollection s) => s;
                        public static IServiceCollection AddSingleton<T>(this IServiceCollection s) => s;
                    }
                }
                namespace CondDiAndXaml
                {
                    using Microsoft.Extensions.DependencyInjection;
                    public sealed class MainPageView { }
                    public static class AttachedProps { }
                    public interface IUnusedContract { void NeverCalled(); }
                    public sealed class UnusedImpl : IUnusedContract { public void NeverCalled() { } }
                    public interface IScoped { }
                    public sealed class ScopedImpl : IScoped { }
                    public interface ISender { }
                    public sealed class DevSender(IScoped s) : ISender { }
                    public sealed class ProdSender : ISender { }
                    public sealed class Notifier(ISender s) { }
                    public sealed class BadFuncConsumer(System.Func<IScoped> f) { }

                    public static class Program
                    {
                        public static void Main(string[] args)
                        {
                            var services = new ServiceCollection();
                            services.AddScoped<IScoped, ScopedImpl>();
                            if (args.Length > 0)
                                services.AddScoped<ISender, DevSender>();
                            else
                                services.AddSingleton<ISender, ProdSender>();
                            services.AddSingleton<Notifier>();
                            services.AddSingleton<BadFuncConsumer>();
                            _ = new UnusedImpl();
                        }
                    }
                }
                """);

            using var roslyn = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snap = await roslyn.ExtractAsync(Path.Combine(projDir, "CondDiAndXaml.csproj"), new ExtractionOptions(IncludeMethods: true));
            var di = analyzer.AnalyzeDi(snap.Graph);
            // Conditional if/else branch captive MUST be detected even when else branch is Singleton
            Assert.Contains(di.CaptiveDependencies, c => c.ConsumerTypeId == "CondDiAndXaml.Notifier" && c.ResolvedImplementationTypeId == "CondDiAndXaml.DevSender");
            // Unresolvable Func<IScoped> must NOT be flagged as captive
            Assert.DoesNotContain(di.CaptiveDependencies, c => c.ConsumerTypeId == "CondDiAndXaml.BadFuncConsumer");

            // XAML ResourceDictionary types (MainPageView, AttachedProps) are kept live
            var deadTypes = analyzer.AnalyzeDeadCode(snap.Graph, NodeKind.Type, profile: "app");
            Assert.DoesNotContain(deadTypes.Candidates, c => c.FullName == "CondDiAndXaml.MainPageView");
            Assert.DoesNotContain(deadTypes.Candidates, c => c.FullName == "CondDiAndXaml.AttachedProps");

            // Method-scope dead code flags BOTH IUnusedContract.NeverCalled and UnusedImpl.NeverCalled consistently
            var deadMethods = analyzer.AnalyzeDeadCode(snap.Graph, NodeKind.Method, profile: "app");
            Assert.Contains(deadMethods.Candidates, c => c.FullName.Contains("IUnusedContract.NeverCalled"));
            Assert.Contains(deadMethods.Candidates, c => c.FullName.Contains("UnusedImpl.NeverCalled"));

            // Broken unconditional <Import> marks workspace degraded (MSB4019)
            var brokenDir = Path.Combine(tempDir, "BrokenImport");
            WriteFile(brokenDir, "BrokenImport.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <Import Project="MissingTargetsFile.targets" />
                </Project>
                """);
            WriteFile(brokenDir, "A.cs", "namespace BrokenImport; public class A {}");
            var brokenSnap = await roslyn.ExtractAsync(Path.Combine(brokenDir, "BrokenImport.csproj"));
            Assert.True(brokenSnap.Graph.LoadStatus?.Degraded);
            Assert.Contains(brokenSnap.Graph.Diagnostics, d => d.Message.Contains("MSB4019"));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Cycle13Fixes_SemanticsCliRulesAndMcpRegression()
    {
        var tempDir = CreateTempDir();
        try
        {
            var analyzer = new ArchitectureAnalyzerService();

            // 1. BaselineStore rejects schema-incomplete JSON missing nodes/edges/projects
            var incompleteBaseline = Path.Combine(tempDir, "incomplete-baseline.json");
            File.WriteAllText(incompleteBaseline, """{"version":1,"snapshotId":"s1","targetPath":"App.csproj"}""");
            Assert.Throws<InvalidDataException>(() => BaselineStore.Load(incompleteBaseline));

            // 2. HtmlReportGenerator renders degraded banner & withholds "All architecture rules passed" on degraded workspace
            var degradedGraph = new CodeGraph(
                tempDir,
                nodes: [],
                edges: [],
                projects: [],
                diagnostics: [],
                loadStatus: new GraphLoadStatus("roslyn", 1, 0, 1, true, false, "Compile errors", ["BadProj"]));
            var degradedSnap = new ArchitectureSnapshot("deg-1", Path.Combine(tempDir, "Bad.csproj"), DateTimeOffset.UtcNow, degradedGraph);
            var degradedRulesReport = new RuleCheckReport(1, 0, 0, 0, 0, false, []);
            var html = HtmlReportGenerator.Generate(degradedSnap, degradedRulesReport);
            Assert.Contains("Degraded Workspace", html);
            Assert.DoesNotContain("All architecture rules passed with 0 violations.", html);

            // 3. Top-level exclude applies to preset rules AND custom rule overrides preset rule with same ID (no duplicate IDs)
            var yamlProvider = new YamlRuleSetProvider();
            var yamlRules = yamlProvider.ParseYaml("""
                version: 1
                presets:
                  - clean-architecture
                exclude:
                  - "App.Domain.ExcludedType"
                rules:
                  - id: CLEAN-001
                    name: Custom override
                    severity: info
                    type: forbid_dependency
                    from: "App.Domain.*"
                    to: ["App.Infrastructure.*"]
                """);
            Assert.Single(yamlRules, r => r.Id.Equals("CLEAN-001", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(RuleSeverity.Info, yamlRules.Single(r => r.Id == "CLEAN-001").Severity);

            var cleanGraph = new CodeGraph(
                tempDir,
                nodes:
                [
                    new CodeNode("App.Domain.ExcludedType", "ExcludedType", "App.Domain.ExcludedType", NodeKind.Type, "App", "App.Domain", TypeKind.Class),
                    new CodeNode("App.Infrastructure.Repo", "Repo", "App.Infrastructure.Repo", NodeKind.Type, "App", "App.Infrastructure", TypeKind.Class)
                ],
                edges:
                [
                    new DependencyEdge("App.Domain.ExcludedType", "App.Infrastructure.Repo", DependencyKind.FieldOrProperty)
                ],
                projects: [new ArchLens.Model.ProjectInfo("App", "App.csproj", "net10.0", [], [], 2, 1)],
                diagnostics: []);
            var excludePresetRules = yamlProvider.ParseYaml("""
                version: 1
                preset: clean-architecture
                exclude:
                  - "App.Domain.ExcludedType"
                """);
            var cleanCheck = analyzer.CheckRules(cleanGraph, excludePresetRules);
            Assert.DoesNotContain(cleanCheck.Violations, v => v.RuleId == "CLEAN-001");

            // 4. ArchLensPrompts: standalone preset wording on source targets + honest binary-only directory detection
            var srcPrompt = ArchLensPrompts.AuditArchitecture(Path.Combine(tempDir, "Sample.slnx"), "clean");
            Assert.Contains("standalone", srcPrompt, StringComparison.OrdinalIgnoreCase);
            var binDir = Path.Combine(tempDir, "binonly");
            Directory.CreateDirectory(binDir);
            File.WriteAllBytes(Path.Combine(binDir, "Sample.dll"), [0, 1, 2]);
            var binPrompt = ArchLensPrompts.AuditArchitecture(binDir, "clean");
            Assert.Contains("structural dependency cycles only", binPrompt, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("includeAnalyzers: true", binPrompt);
            Assert.DoesNotContain("di_analysis(", binPrompt);
            Assert.DoesNotContain("sortBy: \"cognitive\"", binPrompt);

            // 5. McpResponseFormatter: fields="all" includes godClassScore & nodeId; missing-sql error does not say "Check SQL syntax"
            var missingSqlResult = McpResponseFormatter.FromException(new ArgumentException("Parameter 'sql' is required (or pass includeSchema: true)."));
            var missingSqlJson = ((ModelContextProtocol.Protocol.TextContentBlock)missingSqlResult.Content[0]).Text;
            Assert.DoesNotContain("Check SQL syntax", missingSqlJson);

            var hsReport = new HotspotsReport(NodeKind.Type, "god_class", 1,
                [new NodeMetrics("T1", "T1", NodeKind.Type, 1, 2, 0.5, null, null, 10, 10, 100, 0, null, 5, 5, 1)],
                Note: "1 record/data type(s) excluded by default; pass a filter (e.g. '*') to include all types.");
            var hsJson = JsonSerializer.Serialize(McpResponseFormatter.FormatHotspots(cleanGraph, hsReport, 0, 5, "all", false));
            Assert.Contains("godClassScore", hsJson);
            Assert.Contains("excluded by default", hsJson);

            var impReport = new ImpactAnalysisResult("T1", "T1", 1, 1, ["App"],
                [new ImpactNode("T2", "T2", "App", 1, null)], false);
            var impJson = JsonSerializer.Serialize(McpResponseFormatter.FormatImpact(impReport, null, 0, 10, false, "all"));
            Assert.Contains("nodeId", impJson);

            // 6. Analyzers: bare filename filter + partial-type declarationSpans scoping
            var partialGraph = new CodeGraph(
                tempDir,
                nodes:
                [
                    new CodeNode("Ns.PartialBig", "PartialBig", "Ns.PartialBig", NodeKind.Type, "P", "Ns", TypeKind.Class,
                        Location: new SourceLocation("src/Multi.cs", 3, 1), LineCount: 69,
                        Metadata: new Dictionary<string, string> { ["declarationSpans"] = "src/Multi.cs|3|6;src/PartialBig.Extra.cs|1|66" }),
                    new CodeNode("Ns.LaterType", "LaterType", "Ns.LaterType", NodeKind.Type, "P", "Ns", TypeKind.Class,
                        Location: new SourceLocation("src/Multi.cs", 8, 1), LineCount: 4)
                ],
                edges: [],
                projects: [new ArchLens.Model.ProjectInfo("P", "P.csproj", "net10.0", [], [], 2, 2)],
                diagnostics: [],
                analyzerDiagnostics:
                [
                    new AnalyzerDiagnostic("CA1822", "Mark members as static", "Performance", RuleSeverity.Warning, "OnlyInFirst", new SourceLocation("src/Multi.cs", 5, 1), "P"),
                    new AnalyzerDiagnostic("CA1822", "Mark members as static", "Performance", RuleSeverity.Warning, "LaterMethod", new SourceLocation("src/Multi.cs", 10, 1), "P"),
                    new AnalyzerDiagnostic("CA1822", "Mark members as static", "Performance", RuleSeverity.Warning, "OnlyInSecond", new SourceLocation("src/PartialBig.Extra.cs", 65, 1), "P")
                ],
                analyzersLoadedCount: 10);
            var filteredPartial = analyzer.GetAnalyzerDiagnostics(partialGraph, minSeverity: RuleSeverity.Info, filterPattern: "*PartialBig");
            Assert.Equal(2, filteredPartial.Diagnostics.Count);
            Assert.Contains(filteredPartial.Diagnostics, d => d.Message == "OnlyInFirst");
            Assert.Contains(filteredPartial.Diagnostics, d => d.Message == "OnlyInSecond");
            Assert.DoesNotContain(filteredPartial.Diagnostics, d => d.Message == "LaterMethod");

            var bareFileFiltered = analyzer.GetAnalyzerDiagnostics(partialGraph, minSeverity: RuleSeverity.Info, filterPattern: "PartialBig.Extra.cs");
            Assert.Single(bareFileFiltered.Diagnostics);
            Assert.Equal("OnlyInSecond", bareFileFiltered.Diagnostics[0].Message);

            // 7. DependencyGraph filtered counts & cross-boundary cycle note
            var cycleGraph = new CodeGraph(
                tempDir,
                nodes:
                [
                    new CodeNode("Ns.A.T1", "T1", "Ns.A.T1", NodeKind.Type, "P", "Ns.A", TypeKind.Class),
                    new CodeNode("Ns.B.T2", "T2", "Ns.B.T2", NodeKind.Type, "P", "Ns.B", TypeKind.Class),
                    new CodeNode("Ns.C.T3", "T3", "Ns.C.T3", NodeKind.Type, "P", "Ns.C", TypeKind.Class)
                ],
                edges:
                [
                    new DependencyEdge("Ns.A.T1", "Ns.B.T2", DependencyKind.FieldOrProperty),
                    new DependencyEdge("Ns.B.T2", "Ns.A.T1", DependencyKind.FieldOrProperty)
                ],
                projects: [new ArchLens.Model.ProjectInfo("P", "P.csproj", "net10.0", [], [], 3, 1)],
                diagnostics: []);
            var depRep = analyzer.GetDependencyGraph(cycleGraph, NodeKind.Namespace, filterPattern: "Ns.A");
            Assert.Equal(1, depRep.TotalNodesInScope);
            Assert.Equal(3, depRep.TotalNodesBeforeFilter);
            Assert.NotNull(depRep.Note);

            // 8. Roslyn end-to-end: DbContext optionsLifetime + AddDbContextPool, indirect factory captives, Replace first-only,
            //    extension method DI order, per-method external contract dead code, Razor markup CC & hybrid LCOM4, Directory.Build.props broken import
            var labDir = Path.Combine(tempDir, "SemLab");
            WriteFile(labDir, "SemLab.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);
            WriteFile(labDir, "A_Extensions.cs", """
                namespace Microsoft.Extensions.DependencyInjection
                {
                    public enum ServiceLifetime { Singleton = 0, Scoped = 1, Transient = 2 }
                    public interface IServiceCollection { }
                    public sealed class ServiceCollection : IServiceCollection { }
                    public sealed class ServiceDescriptor
                    {
                        public static ServiceDescriptor Singleton<TService, TImpl>() => new();
                    }
                    public static class ActivatorUtilities
                    {
                        public static T CreateInstance<T>(System.IServiceProvider sp, params object[] args) => default!;
                    }
                    public static class ServiceProviderServiceExtensions
                    {
                        public static T GetRequiredService<T>(this System.IServiceProvider sp) => default!;
                    }
                    public static class Ext
                    {
                        public static IServiceCollection AddSingleton<TService, TImpl>(this IServiceCollection s) => s;
                        public static IServiceCollection AddSingleton<T>(this IServiceCollection s) => s;
                        public static IServiceCollection AddSingleton<T>(this IServiceCollection s, System.Func<System.IServiceProvider, T> f) => s;
                        public static IServiceCollection AddScoped<TService, TImpl>(this IServiceCollection s) => s;
                        public static IServiceCollection Replace(this IServiceCollection s, ServiceDescriptor d) => s;
                        public static IServiceCollection AddDbContext<T>(this IServiceCollection s, System.Action<object>? o = null, ServiceLifetime contextLifetime = ServiceLifetime.Scoped, ServiceLifetime optionsLifetime = ServiceLifetime.Scoped) => s;
                        public static IServiceCollection AddDbContextPool<T>(this IServiceCollection s, System.Action<object>? o = null) => s;
                    }
                }
                namespace Microsoft.EntityFrameworkCore
                {
                    public sealed class DbContextOptions<T> { }
                }
                namespace SemLab
                {
                    using Microsoft.Extensions.DependencyInjection;
                    public static class DiExtensions
                    {
                        public static IServiceCollection AddInfra(this IServiceCollection s)
                            => s.AddSingleton<IOrderedSvc, OrderedImpl>();
                    }
                }
                """);
            WriteFile(labDir, "Program.cs", """
                namespace SemLab
                {
                    using System;
                    using System.Collections.Generic;
                    using Microsoft.EntityFrameworkCore;
                    using Microsoft.Extensions.DependencyInjection;

                    public interface IScoped { }
                    public sealed class ScopedImpl : IScoped { }

                    public sealed class CtxB { }
                    public sealed class CtxC { }
                    public sealed class CtxBOptionsConsumer(DbContextOptions<CtxB> o) { }
                    public sealed class CtxCOptionsConsumer(DbContextOptions<CtxC> o) { }

                    public sealed class ViaHelper(IScoped s) { }
                    public sealed class ViaAlias(IScoped s) { }
                    public sealed class ViaActivator(IScoped s) { }

                    public interface IOrderedSvc { }
                    public sealed class OrderedImpl : IOrderedSvc { }
                    public sealed class OrderedConsumer(IOrderedSvc s) { }

                    public interface ISecond { }
                    public sealed class SecondA : ISecond { }
                    public sealed class SecondB : ISecond { }
                    public sealed class SecondC : ISecond { }
                    public sealed class SecondCollConsumer(IEnumerable<ISecond> items) { }

                    public interface IUnusedLocal { void Hidden(); }
                    public sealed class LocalPlusDisposable : IUnusedLocal, IDisposable
                    {
                        public void Hidden() { }
                        public void Dispose() { }
                    }

                    public partial class HybridComp
                    {
                        private int _codeBehind;
                        public void UseCodeBehind() { _codeBehind++; }
                    }

                    public static class Program
                    {
                        private static ViaHelper CreateViaHelper(IServiceProvider provider)
                            => new ViaHelper(provider.GetRequiredService<IScoped>());

                        public static void Main()
                        {
                            var services = new ServiceCollection();
                            services.AddScoped<IScoped, ScopedImpl>();

                            services.AddDbContext<CtxB>(_ => { }, ServiceLifetime.Scoped, ServiceLifetime.Singleton);
                            services.AddDbContextPool<CtxC>(_ => { });
                            services.AddSingleton<CtxBOptionsConsumer>();
                            services.AddSingleton<CtxCOptionsConsumer>();

                            services.AddSingleton(sp => CreateViaHelper(sp));
                            services.AddSingleton(sp => { var p = sp; return new ViaAlias(p.GetRequiredService<IScoped>()); });
                            services.AddSingleton(sp => ActivatorUtilities.CreateInstance<ViaActivator>(sp));

                            services.AddScoped<IOrderedSvc, OrderedImpl>();
                            services.AddInfra();
                            services.AddSingleton<OrderedConsumer>();

                            services.AddSingleton<ISecond, SecondA>();
                            services.AddScoped<ISecond, SecondB>();
                            services.Replace(ServiceDescriptor.Singleton<ISecond, SecondC>());
                            services.AddSingleton<SecondCollConsumer>();

                            services.AddSingleton<LocalPlusDisposable>();
                        }
                    }
                }
                """);
            WriteFile(labDir, "MarkupComplex.razor", """
                @page "/markup"
                @if (Flag) { <p>A</p> } else if (Other) { <p>B</p> }
                @foreach (var item in Items) { <span>@item</span> }
                @code {
                    private int _count;
                    public bool Flag { get; set; }
                    public bool Other { get; set; }
                    public System.Collections.Generic.List<int> Items { get; } = new();
                    private void Increment() { _count++; }
                    private void NeverUsed() { }
                }
                """);
            WriteFile(labDir, "HybridComp.razor", """
                @namespace SemLab
                <p>Hybrid</p>
                @code {
                    private int _inline;
                    public void UseInline() { _inline++; }
                }
                """);

            using var roslyn = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snap = await roslyn.ExtractAsync(Path.Combine(labDir, "SemLab.csproj"), new ExtractionOptions(IncludeMethods: true));
            var di = analyzer.AnalyzeDi(snap.Graph);

            // DbContext optionsLifetime=Singleton & AddDbContextPool options=Singleton -> no false captive
            Assert.DoesNotContain(di.CaptiveDependencies, c => c.ConsumerTypeId == "SemLab.CtxBOptionsConsumer");
            Assert.DoesNotContain(di.CaptiveDependencies, c => c.ConsumerTypeId == "SemLab.CtxCOptionsConsumer");

            // Indirect factory resolutions -> all 3 captive
            Assert.Contains(di.CaptiveDependencies, c => c.ConsumerTypeId == "SemLab.ViaHelper");
            Assert.Contains(di.CaptiveDependencies, c => c.ConsumerTypeId == "SemLab.ViaAlias");
            Assert.Contains(di.CaptiveDependencies, c => c.ConsumerTypeId == "SemLab.ViaActivator");

            // Interprocedural DI ordering: AddScoped then AddInfra (AddSingleton) -> Singleton wins -> no false captive
            Assert.DoesNotContain(di.CaptiveDependencies, c => c.ConsumerTypeId == "SemLab.OrderedConsumer");

            // Replace removes only first matching descriptor -> SecondB (Scoped) remains captive for IEnumerable<ISecond>
            Assert.Contains(di.CaptiveDependencies, c => c.ConsumerTypeId == "SemLab.SecondCollConsumer" && c.ResolvedImplementationTypeId == "SemLab.SecondB");

            // Method-scope dead code: Hidden() is dead even though class also implements IDisposable; Dispose() stays alive
            var deadMethods = analyzer.AnalyzeDeadCode(snap.Graph, NodeKind.Method, profile: "app");
            Assert.Contains(deadMethods.Candidates, c => c.FullName == "SemLab.LocalPlusDisposable.Hidden()");
            Assert.DoesNotContain(deadMethods.Candidates, c => c.FullName == "SemLab.LocalPlusDisposable.Dispose()");

            // Razor markup control-flow CC >= 4, cognitive >= 3, and trivial no-op method ignored in LCOM4 (= 1)
            var markupNode = snap.Graph.FindNode("SemLab.MarkupComplex");
            Assert.NotNull(markupNode);
            Assert.True(markupNode!.CyclomaticComplexity >= 4, $"Expected CC >= 4, got {markupNode.CyclomaticComplexity}");
            Assert.True(markupNode.CognitiveComplexity >= 3, $"Expected Cognitive >= 3, got {markupNode.CognitiveComplexity}");
            Assert.Equal(1, markupNode.Lcom4);

            // Hybrid Razor LCOM4 merges both halves -> 2 disjoint state clusters
            var hybridNode = snap.Graph.FindNode("SemLab.HybridComp");
            Assert.NotNull(hybridNode);
            Assert.Equal(2, hybridNode!.Lcom4);

            // Directory.Build.props broken unconditional <Import> marks workspace degraded (MSB4019)
            var propsDir = Path.Combine(tempDir, "PropsBroken");
            WriteFile(propsDir, "Directory.Build.props", """
                <Project>
                  <Import Project="does-not-exist.props" />
                </Project>
                """);
            WriteFile(propsDir, "PropsBroken.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            WriteFile(propsDir, "Sample.cs", "namespace PropsBroken; public class Sample {}");
            var propsSnap = await roslyn.ExtractAsync(Path.Combine(propsDir, "PropsBroken.csproj"));
            Assert.True(propsSnap.Graph.LoadStatus?.Degraded);
            Assert.Contains(propsSnap.Graph.Diagnostics, d => d.Message.Contains("MSB4019"));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Cycle14Fixes_SemanticsIlCliRulesAndMcpRegression()
    {
        var tempDir = CreateTempDir();
        try
        {
            var analyzer = new ArchitectureAnalyzerService();

            // 1. BaselineStore rejects null elements in arrays and warns on partial project scope & includeTests mismatch
            var nullNodeBaseline = Path.Combine(tempDir, "null-node.json");
            File.WriteAllText(nullNodeBaseline, """{"version":1,"snapshotId":"x","targetPath":"y","capturedAt":"2026-01-01T00:00:00Z","nodes":[null],"edges":[],"projects":[]}""");
            Assert.Throws<InvalidDataException>(() => BaselineStore.Load(nullNodeBaseline));

            var fullSolGraph = new CodeGraph(
                tempDir,
                nodes: [new CodeNode("project:App", "App", "App", NodeKind.Project, "App"), new CodeNode("project:Core", "Core", "Core", NodeKind.Project, "Core")],
                edges: [],
                projects: [new ArchLens.Model.ProjectInfo("App", "App.csproj", "net10.0", [], [], 1, 1), new ArchLens.Model.ProjectInfo("Core", "Core.csproj", "net10.0", [], [], 1, 1)],
                diagnostics: [],
                excludedTestProjectCount: 1);
            var singleProjGraph = new CodeGraph(
                tempDir,
                nodes: [new CodeNode("project:Core", "Core", "Core", NodeKind.Project, "Core")],
                edges: [],
                projects: [new ArchLens.Model.ProjectInfo("Core", "Core.csproj", "net10.0", [], [], 1, 1)],
                diagnostics: []);
            var withTestsGraph = new CodeGraph(
                tempDir,
                nodes:
                [
                    new CodeNode("project:App", "App", "App", NodeKind.Project, "App"),
                    new CodeNode("project:Core", "Core", "Core", NodeKind.Project, "Core"),
                    new CodeNode("project:App.Tests", "App.Tests", "App.Tests", NodeKind.Project, "App.Tests", Metadata: new Dictionary<string, string> { ["isTestProject"] = "true" })
                ],
                edges: [],
                projects:
                [
                    new ArchLens.Model.ProjectInfo("App", "App.csproj", "net10.0", [], [], 1, 1),
                    new ArchLens.Model.ProjectInfo("Core", "Core.csproj", "net10.0", [], [], 1, 1),
                    new ArchLens.Model.ProjectInfo("App.Tests", "App.Tests.csproj", "net10.0", [], [], 1, 1)
                ],
                diagnostics: [],
                excludedTestProjectCount: 0);
            var baseSnap = new ArchitectureSnapshot("b1", "Sol.slnx", DateTimeOffset.UtcNow, fullSolGraph);
            var subSnap = new ArchitectureSnapshot("c1", "Core.csproj", DateTimeOffset.UtcNow, singleProjGraph);
            var testsSnap = new ArchitectureSnapshot("c2", "Sol.slnx", DateTimeOffset.UtcNow, withTestsGraph);
            Assert.Contains("target scope differs", BaselineStore.GetScopeMismatchWarning(baseSnap, subSnap) ?? "");
            Assert.Contains("test-project inclusion differs", BaselineStore.GetScopeMismatchWarning(baseSnap, testsSnap) ?? "");

            // 2. Unknown metric rejected even on 0-type graph; rootNamespace matching only a type name is vacuous; type-only hotspot metrics rejected at namespace scope
            var emptyTypeGraph = new CodeGraph(tempDir, [], [], [new ArchLens.Model.ProjectInfo("Empty", "Empty.csproj", "net10.0", [], [], 0, 0)], []);
            var badMetricCheck = analyzer.CheckRules(emptyTypeGraph, [new MaxMetricRule("M1", "Bad", "", RuleSeverity.Error, "bogus_metric", 5)]);
            Assert.True(badMetricCheck.ConfigurationError);

            var singleTypeGraph = new CodeGraph(
                tempDir,
                nodes: [new CodeNode("App.Domain.Entity", "Entity", "App.Domain.Entity", NodeKind.Type, "App", "App.Domain", TypeKind.Class)],
                edges: [],
                projects: [new ArchLens.Model.ProjectInfo("App", "App.csproj", "net10.0", [], [], 1, 1)],
                diagnostics: []);
            var typeAsRootCheck = analyzer.CheckRules(singleTypeGraph, ArchitecturePresets.CreateRules("clean-architecture", "App.Domain.Entity"), rootNamespace: "App.Domain.Entity");
            Assert.True(typeAsRootCheck.VacuousConfiguration);

            Assert.Throws<ArgumentException>(() => analyzer.GetHotspots(singleTypeGraph, sortBy: "lcom4", scope: NodeKind.Namespace));
            Assert.Throws<ArgumentException>(() => analyzer.GetHotspots(singleTypeGraph, sortBy: "ctor", scope: NodeKind.Namespace));
            Assert.Throws<ArgumentException>(() => analyzer.GetHotspots(singleTypeGraph, sortBy: "god_class", scope: NodeKind.Namespace));

            // 3. Absolute path in analyzers --filter matches relative diagnostic path
            var absFile = Path.Combine(tempDir, "src", "Multi.cs");
            var absDiagGraph = new CodeGraph(
                tempDir,
                nodes: [],
                edges: [],
                projects: [new ArchLens.Model.ProjectInfo("P", "P.csproj", "net10.0", [], [], 0, 1)],
                diagnostics: [],
                analyzerDiagnostics: [new AnalyzerDiagnostic("CA1822", "Title", "Performance", RuleSeverity.Warning, "Msg", new SourceLocation("src/Multi.cs", 5, 1), "P")],
                analyzersLoadedCount: 1);
            var absRes = analyzer.GetAnalyzerDiagnostics(absDiagGraph, minSeverity: RuleSeverity.Info, filterPattern: absFile);
            Assert.Single(absRes.Diagnostics);

            // 4. MCP prompt binary/path fixes, XmlException classification, inspect_symbol incomingNextOffset validation
            var binPrompt = ArchLensPrompts.AuditArchitecture("C:\\out\\App.dll", "clean");
            Assert.DoesNotContain("preset: \"clean-architecture\"", binPrompt);
            Assert.Contains("hotspots(path: \"C:\\\\out\\\\App.dll\", sortBy: \"complexity\")", binPrompt);
            var srcPrompt = ArchLensPrompts.AuditArchitecture("C:\\src\\App.slnx", "clean");
            Assert.Contains("hotspots(path: \"C:\\\\src\\\\App.slnx\", sortBy: \"cognitive\")", srcPrompt);
            var xmlErr = McpResponseFormatter.FromException(new System.Xml.XmlException("Unexpected EOF"));
            var xmlJson = ((ModelContextProtocol.Protocol.TextContentBlock)xmlErr.Content[0]).Text;
            Assert.Contains("\"invalid_operation\"", xmlJson);
            McpResponseFormatter.ValidateInspectSymbolFields("offset,incomingOffset,incomingNextOffset");

            // 5. Roslyn end-to-end: malformed Directory.Build.props (MSB4024), EF Singleton options promotion, repeated DI registration,
            //    [ActivatorUtilitiesConstructor] NOT applied to standard DI, 2-arg ServiceDescriptor instance,
            //    catch-when CC, instance `this.Empty` LCOM4, MVC [NonAction], transitive polymorphic method impact, IsTestProject, colliding Twin assemblies
            var semDir = Path.Combine(tempDir, "Sem14");
            WriteFile(semDir, "Sem14.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);
            WriteFile(semDir, "Code.cs", """
                namespace Microsoft.Extensions.DependencyInjection
                {
                    public enum ServiceLifetime { Singleton = 0, Scoped = 1, Transient = 2 }
                    public interface IServiceCollection { }
                    public sealed class ServiceCollection : IServiceCollection { }
                    public sealed class ActivatorUtilitiesConstructorAttribute : System.Attribute { }
                    public sealed class ServiceDescriptor
                    {
                        public ServiceDescriptor(System.Type serviceType, object instance) { }
                    }
                    public static class Ext
                    {
                        public static IServiceCollection Add(this IServiceCollection s, ServiceDescriptor d) => s;
                        public static IServiceCollection AddSingleton<TService, TImpl>(this IServiceCollection s) => s;
                        public static IServiceCollection AddSingleton<T>(this IServiceCollection s) => s;
                        public static IServiceCollection AddScoped<TService, TImpl>(this IServiceCollection s) => s;
                        public static IServiceCollection AddDbContext<T>(this IServiceCollection s, System.Action<object>? o = null, ServiceLifetime contextLifetime = ServiceLifetime.Scoped, ServiceLifetime optionsLifetime = ServiceLifetime.Scoped) => s;
                    }
                }
                namespace Microsoft.EntityFrameworkCore
                {
                    public sealed class DbContextOptions<T> { }
                }
                namespace Microsoft.AspNetCore.Mvc
                {
                    public sealed class NonActionAttribute : System.Attribute { }
                    public abstract class ControllerBase { }
                }
                namespace Sem14
                {
                    using System;
                    using Microsoft.AspNetCore.Mvc;
                    using Microsoft.EntityFrameworkCore;
                    using Microsoft.Extensions.DependencyInjection;

                    public sealed class EfA(DbContextOptions<EfA> o) { }
                    public interface IRepeat { }
                    public sealed class RepScoped : IRepeat { }
                    public sealed class RepSingleton : IRepeat { }
                    public sealed class RepeatRoot(IRepeat r) { }

                    public interface IDep { }
                    public sealed class ScopedDep : IDep { }
                    public sealed class Pinned
                    {
                        [ActivatorUtilitiesConstructor]
                        public Pinned() { }
                        public Pinned(IDep d) { }
                    }

                    public sealed class DescriptorInst { }

                    public sealed class AuditController : ControllerBase
                    {
                        public string Get() => "ok";
                        [NonAction]
                        public string HelperNeverCalled() => "dead";
                    }

                    public sealed class ReadsEmpty
                    {
                        private int Empty = 4;
                        private int _other = 1;
                        public int ReadA() => this.Empty;
                        public int ReadB() => _other;
                    }

                    public static class FilteredCatch
                    {
                        public static int Run(bool handle)
                        {
                            try { return 1; }
                            catch (Exception) when (handle) { return 2; }
                        }
                    }

                    public interface IGo { void Go(); }
                    public sealed class Child : IGo { public void Go() => Target.Work(); }
                    public static class Target { public static void Work() { } }
                    public static class Callers
                    {
                        public static void ViaInterface(IGo g) => g.Go();
                    }

                    public static class Program
                    {
                        public static void Main()
                        {
                            var s = new ServiceCollection();
                            s.AddDbContext<EfA>(_ => { }, ServiceLifetime.Singleton, ServiceLifetime.Scoped);
                            s.AddScoped<IRepeat, RepScoped>();
                            s.AddSingleton<IRepeat, RepSingleton>();
                            s.AddScoped<IRepeat, RepScoped>();
                            s.AddSingleton<RepeatRoot>();
                            s.AddScoped<IDep, ScopedDep>();
                            s.AddSingleton<Pinned>();
                            s.Add(new ServiceDescriptor(typeof(DescriptorInst), new DescriptorInst()));
                        }
                    }
                }
                """);

            using var roslyn = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snap = await roslyn.ExtractAsync(Path.Combine(semDir, "Sem14.csproj"), new ExtractionOptions(IncludeMethods: true));
            var di = analyzer.AnalyzeDi(snap.Graph);

            // EF Singleton context promotes options to Singleton -> NO false captive
            Assert.DoesNotContain(di.CaptiveDependencies, c => c.ConsumerTypeId == "Sem14.EfA");
            // Repeated Scoped -> Singleton -> Scoped retains final Scoped -> captive!
            Assert.Contains(di.CaptiveDependencies, c => c.ConsumerTypeId == "Sem14.RepeatRoot");
            // [ActivatorUtilitiesConstructor] ignored for standard ServiceProvider -> Pinned(IDep) selected -> captive!
            Assert.Contains(di.CaptiveDependencies, c => c.ConsumerTypeId == "Sem14.Pinned");
            // 2-arg ServiceDescriptor instance -> Singleton, Impl = DescriptorInst
            var descReg = Assert.Single(di.Registrations, r => r.ServiceTypeId == "Sem14.DescriptorInst");
            Assert.Equal(ServiceLifetimeKind.Singleton, descReg.Lifetime);
            Assert.Equal("Sem14.DescriptorInst", descReg.ImplementationTypeId);

            // MVC [NonAction] is dead, Get() is alive
            var deadMethods = analyzer.AnalyzeDeadCode(snap.Graph, NodeKind.Method, profile: "app");
            Assert.Contains(deadMethods.Candidates, c => c.FullName == "Sem14.AuditController.HelperNeverCalled()");
            Assert.DoesNotContain(deadMethods.Candidates, c => c.FullName == "Sem14.AuditController.Get()");

            // `this.Empty` is not discarded -> LCOM4 = 2
            Assert.Equal(2, snap.Graph.FindNode("Sem14.ReadsEmpty")?.Lcom4);

            // catch-when filter adds +1 -> CC = 3
            var catchMethod = snap.Graph.GetNodes(NodeKind.Method).Single(m => m.FullName.Contains("FilteredCatch.Run"));
            Assert.Equal(3, catchMethod.CyclomaticComplexity);

            // Transitive polymorphic method impact: Target.Work -> Child.Go -> IGo.Go -> Callers.ViaInterface
            var impact = analyzer.AnalyzeImpact(snap.Graph, "Sem14.Target.Work()", maxDepth: 4);
            Assert.NotNull(impact);
            Assert.Contains(impact!.ImpactedNodes, n => n.FullName.Contains("Callers.ViaInterface"));

            // Malformed Directory.Build.props marks workspace degraded (MSB4024)
            var badPropsDir = Path.Combine(tempDir, "BadProps");
            WriteFile(badPropsDir, "Directory.Build.props", "<Project><Broken");
            WriteFile(badPropsDir, "BadProps.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            WriteFile(badPropsDir, "A.cs", "namespace BadProps; public class A {}");
            var badPropsSnap = await roslyn.ExtractAsync(Path.Combine(badPropsDir, "BadProps.csproj"));
            Assert.True(badPropsSnap.Graph.LoadStatus?.Degraded);
            Assert.Contains(badPropsSnap.Graph.Diagnostics, d => d.Message.Contains("MSB4024"));

            // Colliding same-named types across two independent projects produce 4 type nodes & 0 type cycles
            var twinDir = Path.Combine(tempDir, "Twins");
            WriteFile(twinDir, "Twin.slnx", """
                <Solution>
                  <Project Path="TwinA/TwinA.csproj" />
                  <Project Path="TwinB/TwinB.csproj" />
                </Solution>
                """);
            WriteFile(twinDir, "TwinA/TwinA.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
            WriteFile(twinDir, "TwinA/Types.cs", "namespace Twins; public class X { public Y? Y { get; set; } } public class Y { }");
            WriteFile(twinDir, "TwinB/TwinB.csproj", """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""");
            WriteFile(twinDir, "TwinB/Types.cs", "namespace Twins; public class Y { public X? X { get; set; } } public class X { }");
            var twinSnap = await roslyn.ExtractAsync(Path.Combine(twinDir, "Twin.slnx"));
            Assert.Equal(4, twinSnap.Graph.GetNodes(NodeKind.Type).Count());
            Assert.Empty(CycleDetector.FindCycles(twinSnap.Graph, NodeKind.Type));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Cycle15Fixes_MultiTargetNamesServiceDescriptorDiComplexityBaselineAndYamlPresetMerge()
    {
        var tempDir = CreateTempDir();
        try
        {
            var analyzer = new ArchitectureAnalyzerService();

            // 1. Multi-target project name normalization without space: "Multi(net10.0)" -> "Multi", "Foo.Tests(net8.0)" -> "Foo.Tests"
            var mtDir = Path.Combine(tempDir, "MultiTfm");
            WriteFile(mtDir, "Multi.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
                    <OutputType>Library</OutputType>
                  </PropertyGroup>
                </Project>
                """);
            WriteFile(mtDir, "MultiType.cs", "namespace Multi; public sealed class OnlyNet10 { }");
            WriteFile(mtDir, "Foo.Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
                    <OutputType>Library</OutputType>
                  </PropertyGroup>
                </Project>
                """);
            WriteFile(mtDir, "FooTests.cs", "namespace Foo.Tests; public sealed class T { }");
            WriteFile(mtDir, "Multi.slnx", """
                <Solution>
                  <Project Path="Multi.csproj" />
                  <Project Path="Foo.Tests.csproj" />
                </Solution>
                """);
            using var roslynMt = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var mtSnap = await roslynMt.ExtractAsync(Path.Combine(mtDir, "Multi.slnx"), new ExtractionOptions(TargetFramework: "net10.0"));
            Assert.Single(mtSnap.Graph.Projects);
            Assert.Equal("Multi", mtSnap.Graph.Projects[0].Name);
            Assert.Equal(1, mtSnap.Graph.ExcludedTestProjectCount);

            // 2. Preset + YAML rules merge preserves YAML layers:, exclude:, and root_namespace:
            var yamlDir = Path.Combine(tempDir, "YamlMerge");
            var layerOnlyYaml = Path.Combine(yamlDir, "layer-only.yaml");
            WriteFile(yamlDir, "layer-only.yaml", """
                version: 1
                layers:
                  domain: ["L.Api.*"]
                  application: ["L.Application.*"]
                exclude:
                  - "L.Api.Ignored*"
                """);
            var yamlProvider = new YamlRuleSetProvider();
            var mergedRules = yamlProvider.LoadRules(layerOnlyYaml, rootNamespaceOverride: null, extraPresets: ["clean-architecture"]);
            var synthGraph = new CodeGraph(
                rootPath: yamlDir,
                nodes:
                [
                    new CodeNode("L.Api.Controller", "Controller", "L.Api.Controller", NodeKind.Type, "L.Api", "L.Api", TypeKind.Class),
                    new CodeNode("L.Api.IgnoredController", "IgnoredController", "L.Api.IgnoredController", NodeKind.Type, "L.Api", "L.Api", TypeKind.Class),
                    new CodeNode("L.Application.AppService", "AppService", "L.Application.AppService", NodeKind.Type, "L.Application", "L.Application", TypeKind.Class)
                ],
                edges:
                [
                    new DependencyEdge("L.Api.Controller", "L.Application.AppService", DependencyKind.FieldOrProperty),
                    new DependencyEdge("L.Api.IgnoredController", "L.Application.AppService", DependencyKind.FieldOrProperty)
                ],
                projects:
                [
                    new ArchLens.Model.ProjectInfo("L.Api", "L.Api.csproj", "net10.0", [], [], 2, 0),
                    new ArchLens.Model.ProjectInfo("L.Application", "L.Application.csproj", "net10.0", [], [], 1, 0)
                ]);
            var mergeReport = analyzer.CheckRules(synthGraph, mergedRules);
            Assert.Single(mergeReport.Violations);
            Assert.Equal("CLEAN-001", mergeReport.Violations[0].RuleId);
            Assert.Equal("L.Api.Controller", mergeReport.Violations[0].NodeId);

            // 3. BaselineStore refuses to overwrite existing unrelated non-baseline file and rejects incomplete nested objects
            var snap = new ArchitectureSnapshot("snap-1", tempDir, DateTimeOffset.UtcNow, synthGraph);
            var unrelatedPath = Path.Combine(tempDir, "sentinel.txt");
            File.WriteAllText(unrelatedPath, "DO NOT OVERWRITE");
            Assert.Throws<InvalidOperationException>(() => BaselineStore.Save(unrelatedPath, snap));
            Assert.Equal("DO NOT OVERWRITE", File.ReadAllText(unrelatedPath));

            var baselinePath = Path.Combine(tempDir, ".archlens-baseline.json");
            BaselineStore.Save(baselinePath, snap);
            BaselineStore.Save(baselinePath, snap); // overwriting an existing valid baseline succeeds

            var incompleteProjectsPath = Path.Combine(tempDir, "incomplete-projects.json");
            File.WriteAllText(incompleteProjectsPath, """{"version":1,"snapshotId":"s","targetPath":"t","capturedAt":"2026-01-01T00:00:00Z","nodes":[],"edges":[],"projects":[{}]}""");
            Assert.Throws<InvalidDataException>(() => BaselineStore.Load(incompleteProjectsPath));

            var missingCapturedAtPath = Path.Combine(tempDir, "missing-captured.json");
            File.WriteAllText(missingCapturedAtPath, """{"version":1,"snapshotId":"s","targetPath":"t","nodes":[],"edges":[],"projects":[]}""");
            Assert.Throws<InvalidDataException>(() => BaselineStore.Load(missingCapturedAtPath));

            // 4. Persistent SQLite cache detects equal-size edit with preserved mtime via content hashing
            var fpDir = Path.Combine(tempDir, "FpTest");
            var fpCsproj = Path.Combine(fpDir, "FpTest.csproj");
            WriteFile(fpDir, "FpTest.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Library</OutputType>
                  </PropertyGroup>
                </Project>
                """);
            var fpFile = Path.Combine(fpDir, "Sample.cs");
            WriteFile(fpDir, "Sample.cs", "namespace Fp; public class OldType {}");
            var originalMtime = File.GetLastWriteTimeUtc(fpFile);
            var cacheDb = Path.Combine(tempDir, "fp-cache.db");
            using (var cache1 = new SqliteGraphCache(cacheDb))
            using (var r1 = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false))
            using (var mgr1 = new WorkspaceSessionManager([r1], cache1))
            {
                var s1 = await mgr1.GetOrLoadAsync(fpCsproj);
                Assert.Contains(s1.Graph.GetNodes(NodeKind.Type), n => n.FullName == "Fp.OldType");
            }
            File.WriteAllText(fpFile, "namespace Fp; public class NewType {}");
            File.SetLastWriteTimeUtc(fpFile, originalMtime);
            using (var cache2 = new SqliteGraphCache(cacheDb))
            using (var r2 = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false))
            using (var mgr2 = new WorkspaceSessionManager([r2], cache2))
            {
                var s2 = await mgr2.GetOrLoadAsync(fpCsproj);
                Assert.Contains(s2.Graph.GetNodes(NodeKind.Type), n => n.FullName == "Fp.NewType");
                Assert.DoesNotContain(s2.Graph.GetNodes(NodeKind.Type), n => n.FullName == "Fp.OldType");
            }

            // 5. Roslyn extraction: ServiceDescriptor factory/instance, keyed int descriptor, primitive ctor selection,
            //    if(false) dead branch, AddExceptionHandler serviceType, global-ns consumer count, ??= & switch when CC,
            //    dead-type method reachability, and missing .slnx member in projectsExpected
            var semDir = Path.Combine(tempDir, "SemTest");
            WriteFile(semDir, "SemTest.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);
            WriteFile(semDir, "DiStubs.cs", """
                namespace Microsoft.Extensions.DependencyInjection
                {
                    public enum ServiceLifetime { Singleton = 0, Scoped = 1, Transient = 2 }
                    public sealed class ServiceDescriptor
                    {
                        public ServiceDescriptor(Type serviceType, object instance) { }
                        public ServiceDescriptor(Type serviceType, object? key, object instance) { }
                        public ServiceDescriptor(Type serviceType, object? key, Type implType, ServiceLifetime lifetime) { }
                        public ServiceDescriptor(Type serviceType, Func<IServiceProvider, object> factory, ServiceLifetime lifetime) { }
                        public static ServiceDescriptor Singleton<TService>(Func<IServiceProvider, TService> factory) where TService : class => null!;
                        public static ServiceDescriptor Singleton<TService>(TService instance) where TService : class => null!;
                    }
                    public interface IServiceCollection { void Add(ServiceDescriptor descriptor); }
                    public static class ServiceCollectionExtensions
                    {
                        public static IServiceCollection AddSingleton<TService, TImpl>(this IServiceCollection s) where TImpl : TService => s;
                        public static IServiceCollection AddSingleton<TService>(this IServiceCollection s) => s;
                        public static IServiceCollection AddScoped<TService, TImpl>(this IServiceCollection s) where TImpl : TService => s;
                        public static IServiceCollection AddExceptionHandler<T>(this IServiceCollection s) => s;
                        public static T GetRequiredService<T>(this IServiceProvider sp) => default!;
                    }
                    [AttributeUsage(AttributeTargets.Parameter)]
                    public sealed class FromKeyedServicesAttribute(object key) : Attribute { }
                }
                """);
            WriteFile(semDir, "Program.cs", """
                using Microsoft.Extensions.DependencyInjection;

                public interface IScopedDep { }
                public sealed class ScopedDep : IScopedDep { }
                public interface IHiddenSvc { }
                public sealed class HiddenImpl : IHiddenSvc { }
                public interface IManualSvc { }
                public sealed class ManualImpl(IScopedDep dep) : IManualSvc { }
                public interface IInstSvc { }
                public sealed class InstImpl(IScopedDep dep) : IInstSvc { }
                public sealed class KeyedSafeConsumer([FromKeyedServices(1)] IScopedDep dep) { }
                public sealed class PrimitiveCtorConsumer
                {
                    public PrimitiveCtorConsumer() { }
                    public PrimitiveCtorConsumer(IScopedDep dep, string connStr) { }
                }
                public interface IIfFalseDep { }
                public sealed class IfFalseDep : IIfFalseDep { }
                public sealed class IfFalseConsumer(IIfFalseDep dep) { }
                public sealed class MyExHandler { }
                public interface IUsedContract { void Called(); void NotCalled(); }
                public sealed class UsedImpl : IUsedContract { public void Called() { } public void NotCalled() { } }
                public sealed class NeverUsedImpl : IUsedContract { public void Called() { } public void NotCalled() { } }

                public static class Composition
                {
                    public static void Main(IServiceCollection services)
                    {
                        services.AddScoped<IScopedDep, ScopedDep>();
                        services.Add(ServiceDescriptor.Singleton<IHiddenSvc>(sp => { _ = sp.GetRequiredService<IScopedDep>(); return new HiddenImpl(); }));
                        services.Add(ServiceDescriptor.Singleton<IManualSvc>(_ => new ManualImpl(new ScopedDep())));
                        services.Add(ServiceDescriptor.Singleton<IInstSvc>(new InstImpl(new ScopedDep())));
                        services.Add(new ServiceDescriptor(typeof(IScopedDep), 1, typeof(ScopedDep), ServiceLifetime.Singleton));
                        services.AddSingleton<KeyedSafeConsumer>();
                        services.AddSingleton<PrimitiveCtorConsumer>();
                        services.AddSingleton<IIfFalseDep, IfFalseDep>();
                        if (false) { services.AddScoped<IIfFalseDep, IfFalseDep>(); }
                        services.AddSingleton<IfFalseConsumer>();
                        services.AddExceptionHandler<MyExHandler>();
                        IUsedContract c = new UsedImpl();
                        c.Called();
                    }

                    public static int ComplexitySample(ref string? s, int x)
                    {
                        s ??= "x";
                        return x switch
                        {
                            1 when s.Length > 0 => 1,
                            2 => 2,
                            _ => 0
                        };
                    }
                }
                """);
            WriteFile(semDir, "WithMissing.slnx", """
                <Solution>
                  <Project Path="SemTest.csproj" />
                  <Project Path="DoesNotExist/Missing.csproj" />
                </Solution>
                """);

            using var roslyn = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var semSnap = await roslyn.ExtractAsync(
                Path.Combine(semDir, "SemTest.csproj"),
                new ExtractionOptions(IncludeMethods: true));

            var diReport = analyzer.AnalyzeDi(semSnap.Graph);
            Assert.Contains(diReport.CaptiveDependencies, c => c.ConsumerTypeId == "HiddenImpl" && c.DependencyTypeId == "IScopedDep");
            Assert.DoesNotContain(diReport.CaptiveDependencies, c => c.ConsumerTypeId == "ManualImpl");
            Assert.DoesNotContain(diReport.CaptiveDependencies, c => c.ConsumerTypeId == "InstImpl");
            Assert.DoesNotContain(diReport.CaptiveDependencies, c => c.ConsumerTypeId == "KeyedSafeConsumer");
            Assert.DoesNotContain(diReport.CaptiveDependencies, c => c.ConsumerTypeId == "PrimitiveCtorConsumer");
            Assert.DoesNotContain(diReport.CaptiveDependencies, c => c.ConsumerTypeId == "IfFalseConsumer");
            Assert.Contains(diReport.Registrations, r => r.ServiceTypeId == "Microsoft.AspNetCore.Diagnostics.IExceptionHandler" && r.ImplementationTypeId == "MyExHandler");
            var unusedSingle = diReport.SingleImplementationInterfaces.FirstOrDefault(s => s.InterfaceTypeId == "IHiddenSvc");
            Assert.NotNull(unusedSingle);
            Assert.Equal(0, unusedSingle!.ConsumerCount);

            // Cyclomatic complexity includes ??= (+1) and switch when (+1): base 1 + ??= (1) + 2 non-discard arms (2) + when (1) = 5
            var ccMethod = semSnap.Graph.GetNodes(NodeKind.Method).First(m => m.Name == "ComplexitySample");
            Assert.Equal(5, ccMethod.CyclomaticComplexity);

            // Dead-code method scope flags NeverUsedImpl.Called() when NeverUsedImpl is dead
            var deadMethods = analyzer.AnalyzeDeadCode(semSnap.Graph, NodeKind.Method, profile: "app");
            Assert.Contains(deadMethods.Candidates, c => c.FullName.Contains("NeverUsedImpl.Called"));

            // Dead-code excludePatterns suppresses without inflating acknowledgedCount
            var deadWithExcl = analyzer.AnalyzeDeadCode(semSnap.Graph, NodeKind.Type, profile: "app", excludePatterns: ["NeverUsedImpl"]);
            Assert.DoesNotContain(deadWithExcl.Candidates, c => c.FullName == "NeverUsedImpl");
            Assert.Null(deadWithExcl.AcknowledgedCount);

            // Missing declared member in .slnx is counted in projectsExpected and projectsDegraded
            var missingSlnxSnap = await roslyn.ExtractAsync(Path.Combine(semDir, "WithMissing.slnx"));
            Assert.NotNull(missingSlnxSnap.Graph.LoadStatus);
            Assert.True(missingSlnxSnap.Graph.LoadStatus!.Degraded);
            Assert.Equal(2, missingSlnxSnap.Graph.LoadStatus.ProjectsExpected);
            Assert.Equal(1, missingSlnxSnap.Graph.LoadStatus.ProjectsLoaded);
            Assert.Equal(1, missingSlnxSnap.Graph.LoadStatus.ProjectsDegraded);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Cycle16Fixes_YamlDuplicateKeyNullEntriesBaselineStrictSarifDedupDiRemoveAllMetricsAndAttributes()
    {
        var tempDir = CreateTempDir();
        try
        {
            var analyzer = new ArchitectureAnalyzerService();
            var yamlProvider = new YamlRuleSetProvider();

            // 1. Duplicate YAML mapping keys and null list entries are rejected with clear errors
            var dupKeyYaml = Path.Combine(tempDir, "dup-key.yaml");
            File.WriteAllText(dupKeyYaml, "version: 1\nrules:\n  - id: R1\n    type: no_cycles\n    scope: project\nrules:\n  - id: R2\n    type: no_cycles\n    scope: project\n");
            Assert.Throws<InvalidDataException>(() => yamlProvider.LoadRules(dupKeyYaml));

            var nullRuleYaml = Path.Combine(tempDir, "null-rule.yaml");
            File.WriteAllText(nullRuleYaml, "version: 1\nrules:\n  - null\n");
            var nullRuleEx = Assert.Throws<InvalidDataException>(() => yamlProvider.LoadRules(nullRuleYaml));
            Assert.Contains("rules:", nullRuleEx.Message);

            var nullAckYaml = Path.Combine(tempDir, "null-ack.yaml");
            File.WriteAllText(nullAckYaml, "version: 1\nacknowledge:\n  - null\n");
            var nullAckEx = Assert.Throws<InvalidDataException>(() => yamlProvider.LoadRules(nullAckYaml));
            Assert.Contains("acknowledge:", nullAckEx.Message);

            // 2. BaselineStore strict overwrite guard & invalid kind / dangling edge validation
            var validNode = new CodeNode("App.A", "A", "App.A", NodeKind.Type, "App", "App", TypeKind.Class);
            var validGraph = new CodeGraph(
                rootPath: tempDir,
                nodes: [validNode],
                edges: [],
                projects: [new ArchLens.Model.ProjectInfo("App", "App.csproj", "net10.0", [], [], 1, 1)]);
            var validSnap = new ArchitectureSnapshot("snap-1", tempDir, DateTimeOffset.UtcNow, validGraph);

            var unrelatedOverlap = Path.Combine(tempDir, "unrelated-overlap.json");
            File.WriteAllText(unrelatedOverlap, """{"version":1,"snapshotId":"not-a-baseline","nodes":[],"projects":[],"sentinel":"keep"}""");
            Assert.Throws<InvalidOperationException>(() => BaselineStore.Save(unrelatedOverlap, validSnap));
            Assert.Contains("keep", File.ReadAllText(unrelatedOverlap));

            var missingNodeKind = Path.Combine(tempDir, "missing-node-kind.json");
            File.WriteAllText(missingNodeKind, """{"version":1,"snapshotId":"s","targetPath":"t","capturedAt":"2026-01-01T00:00:00Z","nodes":[{"id":"App.A","name":"A","fullName":"App.A","projectName":"App"}],"edges":[],"projects":[{"name":"App"}]}""");
            Assert.Throws<InvalidDataException>(() => BaselineStore.Load(missingNodeKind));

            var invalidEdgeKind = Path.Combine(tempDir, "invalid-edge-kind.json");
            File.WriteAllText(invalidEdgeKind, """{"version":1,"snapshotId":"s","targetPath":"t","capturedAt":"2026-01-01T00:00:00Z","nodes":[{"id":"App.A","name":"A","fullName":"App.A","kind":"type","projectName":"App"}],"edges":[{"fromId":"App.A","toId":"App.A","kind":999}],"projects":[{"name":"App"}]}""");
            Assert.Throws<InvalidDataException>(() => BaselineStore.Load(invalidEdgeKind));

            var danglingEdge = Path.Combine(tempDir, "dangling-edge.json");
            File.WriteAllText(danglingEdge, """{"version":1,"snapshotId":"s","targetPath":"t","capturedAt":"2026-01-01T00:00:00Z","nodes":[{"id":"App.A","name":"A","fullName":"App.A","kind":"type","projectName":"App"}],"edges":[{"fromId":"App.A","toId":"App.Missing","kind":"methodCall"}],"projects":[{"name":"App"}]}""");
            Assert.Throws<InvalidDataException>(() => BaselineStore.Load(danglingEdge));

            // 3. SARIF deduplication & toolExecutionNotifications warnings
            var dupDiag = new AnalyzerDiagnostic("AUD001", "Dup", "Design", RuleSeverity.Error, "Duplicate msg", new SourceLocation("Code.cs", 1, 1), "App");
            var sarifJson = SarifExporter.Export(
                analyzerDiagnostics: [dupDiag, dupDiag],
                toolWarnings: ["Warning: scope mismatch"]);
            using (var sdoc = System.Text.Json.JsonDocument.Parse(sarifJson))
            {
                var run = sdoc.RootElement.GetProperty("runs")[0];
                Assert.Equal(1, run.GetProperty("results").GetArrayLength());
                Assert.Equal(1, run.GetProperty("invocations")[0].GetProperty("toolExecutionNotifications").GetArrayLength());
            }

            // 4. Roslyn extraction: RemoveAll, TryAddEnumerable dedup, method-group concrete impl inference,
            //    top-level Program impact registration exclusion, method attribute type-level edge,
            //    expression-bodied indexer complexity, overload non-recursion, LCOM4 empty-stub class = 1,
            //    memberCount excluding accessors, and unrestored PackageReference project flagged degraded
            var projDir = Path.Combine(tempDir, "C16Sem");
            WriteFile(projDir, "C16Sem.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);
            WriteFile(projDir, "Stubs.cs", """
                namespace Microsoft.Extensions.DependencyInjection
                {
                    public enum ServiceLifetime { Singleton = 0, Scoped = 1, Transient = 2 }
                    public sealed class ServiceDescriptor
                    {
                        public static ServiceDescriptor Singleton<TS, TI>() where TI : TS => null!;
                    }
                    public interface IServiceCollection { void Clear(); }
                    public static class ServiceCollectionExtensions
                    {
                        public static IServiceCollection AddSingleton<TS, TI>(this IServiceCollection s) where TI : TS => s;
                        public static IServiceCollection AddSingleton<TS>(this IServiceCollection s, Func<IServiceProvider, TS> f) where TS : class => s;
                        public static IServiceCollection AddScoped<TS, TI>(this IServiceCollection s) where TI : TS => s;
                        public static IServiceCollection RemoveAll<TS>(this IServiceCollection s) => s;
                        public static void TryAddEnumerable(this IServiceCollection s, ServiceDescriptor d) { }
                    }
                }
                """);
            WriteFile(projDir, "Types.cs", """
                using Microsoft.Extensions.DependencyInjection;

                namespace C16Sem;

                public interface IScoped { }
                public sealed class ScopedImpl : IScoped { }
                public interface IRemoved { }
                public sealed class RemovedConsumer(IScoped s) : IRemoved { }
                public interface IPlugin { }
                public sealed class PluginImpl : IPlugin { }
                public interface IFactorySvc { }
                public sealed class ConcreteFactoryImpl : IFactorySvc { }

                [AttributeUsage(AttributeTargets.Method)]
                public sealed class MarkerAttribute : Attribute { }

                public static class Host
                {
                    [Marker]
                    public static void Run() { }
                    public static IFactorySvc MakeFactory(IServiceProvider sp) => new ConcreteFactoryImpl();
                }

                public sealed class SimpleDto
                {
                    public int Id { get; set; }
                    public string Name { get; set; } = "";
                }

                public sealed class NoStateStubs
                {
                    public void A() { }
                    public void B() { }
                }

                public sealed class IndexerAndOverload
                {
                    public int this[int i] => i > 0 ? 1 : 0;
                    public int Overload(int x) => x;
                    public int Overload(string s) => Overload(1);
                }
                """);
            WriteFile(projDir, "Program.cs", """
                using C16Sem;
                using Microsoft.Extensions.DependencyInjection;

                IServiceCollection services = null!;
                services.AddScoped<IScoped, ScopedImpl>();
                services.AddSingleton<IRemoved, RemovedConsumer>();
                services.RemoveAll<IRemoved>();
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IPlugin, PluginImpl>());
                services.TryAddEnumerable(ServiceDescriptor.Singleton<IPlugin, PluginImpl>());
                services.AddSingleton<IFactorySvc>(Host.MakeFactory);
                Host.Run();
                """);

            using var roslyn = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var semSnap = await roslyn.ExtractAsync(Path.Combine(projDir, "C16Sem.csproj"), new ExtractionOptions(IncludeMethods: true));

            var di = analyzer.AnalyzeDi(semSnap.Graph);
            Assert.DoesNotContain(di.CaptiveDependencies, c => c.ConsumerTypeId == "C16Sem.RemovedConsumer");
            Assert.Single(di.Registrations, r => r.ServiceTypeId == "C16Sem.IPlugin" && r.ImplementationTypeId == "C16Sem.PluginImpl");
            Assert.Contains(di.Registrations, r => r.ServiceTypeId == "C16Sem.IFactorySvc" && r.ImplementationTypeId == "C16Sem.ConcreteFactoryImpl");

            // Top-level Program registration edge excluded from impact
            var impact = analyzer.AnalyzeImpact(semSnap.Graph, "C16Sem.IScoped", excludeRegistrations: true)!;
            Assert.DoesNotContain(impact.ImpactedNodes, n => n.NodeId == "C16Sem.Program");
            Assert.True(impact.ExcludedRegistrationCount is > 0);

            // Method attribute keeps MarkerAttribute live in type-scope dead code
            var dead = analyzer.AnalyzeDeadCode(semSnap.Graph, NodeKind.Type, profile: "app");
            Assert.DoesNotContain(dead.Candidates, c => c.FullName == "C16Sem.MarkerAttribute");

            // Metrics: memberCount=2 for SimpleDto, LCOM4=1 for NoStateStubs, indexer CC=2/Cog=1, overload non-recursive Cog=0
            var dto = semSnap.Graph.FindNode("C16Sem.SimpleDto")!;
            Assert.Equal(2, dto.MemberCount);
            var noState = semSnap.Graph.FindNode("C16Sem.NoStateStubs")!;
            Assert.Equal(1, noState.Lcom4);
            var idxType = semSnap.Graph.FindNode("C16Sem.IndexerAndOverload")!;
            Assert.True(idxType.CyclomaticComplexity >= 4); // indexer(2) + Overload(int)(1) + Overload(string)(1) = 4
            Assert.Equal(1, idxType.CognitiveComplexity);   // indexer(1) + non-recursive overloads(0) = 1
            var overloadStr = semSnap.Graph.GetNodes(NodeKind.Method).First(m => m.FullName.Contains("Overload(string)"));
            Assert.Equal(0, overloadStr.CognitiveComplexity);

            // 5. Unrestored PackageReference project without obj/project.assets.json is flagged degraded (RESTORE_REQUIRED)
            var noRestoreDir = Path.Combine(tempDir, "NoRestorePkg");
            WriteFile(noRestoreDir, "NoRestorePkg.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Library</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageReference Include="No.Such.Package" Version="1.0.0" />
                  </ItemGroup>
                </Project>
                """);
            WriteFile(noRestoreDir, "Sample.cs", "namespace NoRestorePkg; public sealed class S { }");
            var noRestoreSnap = await roslyn.ExtractAsync(Path.Combine(noRestoreDir, "NoRestorePkg.csproj"));
            Assert.NotNull(noRestoreSnap.Graph.LoadStatus);
            Assert.True(noRestoreSnap.Graph.LoadStatus!.Degraded);
            Assert.Contains(noRestoreSnap.Graph.LoadStatus.DegradedDetails ?? [], d => d.ErrorCode == "RESTORE_REQUIRED");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Cycle17Fixes_DiKeyedRemoveAll_UnactivatableCtor_DbFactoryTransient_LocalFuncAttr_MethodImpactContract_YamlValidationAndSliceExclude_CcPatterns()
    {
        var tempDir = CreateTempDir();
        try
        {
            var analyzer = new ArchitectureAnalyzerService();
            var yaml = new YamlRuleSetProvider();

            // 1. GlobMatcher.MatchesAny ignores null/blank; YAML rejects null/blank entries in exclude/exclude_from/allow/acknowledge without symbol
            Assert.False(GlobMatcher.MatchesAny("App.Domain.Order", [null!, "", "   "]));
            Assert.Throws<InvalidOperationException>(() => yaml.ParseYaml("version: 1\nexclude:\n  - \nrules:\n  - id: R1\n    type: no_cycles\n"));
            Assert.Throws<InvalidOperationException>(() => yaml.ParseYaml("version: 1\nrules:\n  - id: R1\n    type: forbid_dependency\n    from: 'A.*'\n    to: ['B.*']\n    exclude_from: [null]\n"));
            Assert.Throws<InvalidOperationException>(() => yaml.ParseYaml("version: 1\nrules:\n  - id: R1\n    type: slice_isolation\n    segment: Modules\n    allow: [null]\n"));
            Assert.Throws<InvalidOperationException>(() => yaml.ParseYaml("version: 1\nacknowledge:\n  - reason: 'missing symbol'\nrules:\n  - id: R1\n    type: no_dead_code\n"));

            // 2. YAML slice_isolation respects global & rule exclude patterns; no_dead_code defaults profile to auto
            var sliceRules = yaml.ParseYaml("""
                version: 1
                exclude:
                  - "App.Modules.Billing.Adapter"
                rules:
                  - id: SLICE-1
                    type: slice_isolation
                    segment: Modules
                    allow: ["Contracts"]
                  - id: DEAD-1
                    type: no_dead_code
                """);
            var sliceRule = Assert.IsType<SliceIsolationRule>(sliceRules[0]);
            var deadRule = Assert.IsType<NoDeadCodeRule>(sliceRules[1]);
            // Under default profile='auto', an Exe project resolves to 'app' and flags an unreferenced public class
            var autoAppGraph = new CodeGraph(tempDir,
                [
                    new("project:MyApp", "MyApp", "MyApp", NodeKind.Project, "MyApp", Metadata: new Dictionary<string, string> { ["outputKind"] = "ConsoleApplication" }),
                    new("MyApp.Program", "Program", "MyApp.Program", NodeKind.Type, "MyApp", "MyApp", TypeKind.Class, Accessibility: AccessibilityKind.Internal, Metadata: new Dictionary<string, string> { ["hasEntryPoint"] = "true" }),
                    new("MyApp.UnusedPublic", "UnusedPublic", "MyApp.UnusedPublic", NodeKind.Type, "MyApp", "MyApp", TypeKind.Class, Accessibility: AccessibilityKind.Public)
                ],
                []);
            Assert.Contains(analyzer.CheckRules(autoAppGraph, [deadRule]).Violations, v => v.NodeId == "MyApp.UnusedPublic");

            var sliceGraph = new CodeGraph(tempDir,
                [
                    new("App.Modules.Billing.Adapter", "Adapter", "App.Modules.Billing.Adapter", NodeKind.Type, "App", "App.Modules.Billing", TypeKind.Class),
                    new("App.Modules.Billing.Direct", "Direct", "App.Modules.Billing.Direct", NodeKind.Type, "App", "App.Modules.Billing", TypeKind.Class),
                    new("App.Modules.Orders.InternalSvc", "InternalSvc", "App.Modules.Orders.InternalSvc", NodeKind.Type, "App", "App.Modules.Orders", TypeKind.Class)
                ],
                [
                    new("App.Modules.Billing.Adapter", "App.Modules.Orders.InternalSvc", DependencyKind.MethodCall),
                    new("App.Modules.Billing.Direct", "App.Modules.Orders.InternalSvc", DependencyKind.MethodCall)
                ]);
            var sliceViolations = analyzer.CheckRules(sliceGraph, [sliceRule]).Violations;
            Assert.Single(sliceViolations);
            Assert.Equal("App.Modules.Billing.Direct", sliceViolations[0].NodeId);

            // 3. Method-level impact on contract method reaches implementing method and its callers (without sibling leak)
            var impNodes = new List<CodeNode>
            {
                new("App.IDispatch", "IDispatch", "App.IDispatch", NodeKind.Type, "App", "App", TypeKind.Interface),
                new("App.Target", "Target", "App.Target", NodeKind.Type, "App", "App", TypeKind.Class),
                new("App.Sibling", "Sibling", "App.Sibling", NodeKind.Type, "App", "App", TypeKind.Class),
                new("App.Caller", "Caller", "App.Caller", NodeKind.Type, "App", "App", TypeKind.Class),
                new("App.IDispatch.Run()", "Run", "App.IDispatch.Run()", NodeKind.Method, "App", "App", ContainingTypeId: "App.IDispatch"),
                new("App.Target.Run()", "Run", "App.Target.Run()", NodeKind.Method, "App", "App", ContainingTypeId: "App.Target"),
                new("App.Sibling.Run()", "Run", "App.Sibling.Run()", NodeKind.Method, "App", "App", ContainingTypeId: "App.Sibling"),
                new("App.Caller.InvokeTarget()", "InvokeTarget", "App.Caller.InvokeTarget()", NodeKind.Method, "App", "App", ContainingTypeId: "App.Caller")
            };
            var impEdges = new List<DependencyEdge>
            {
                new("App.Target", "App.IDispatch", DependencyKind.Implements),
                new("App.Sibling", "App.IDispatch", DependencyKind.Implements),
                new("App.Caller.InvokeTarget()", "App.Target.Run()", DependencyKind.MethodCall)
            };
            var impGraph = new CodeGraph(tempDir, impNodes, impEdges, methodsIncluded: true);
            var contractImpact = analyzer.AnalyzeImpact(impGraph, "App.IDispatch.Run()")!;
            Assert.Contains(contractImpact.ImpactedNodes, n => n.NodeId == "App.Target.Run()" && n.Distance == 1);
            Assert.Contains(contractImpact.ImpactedNodes, n => n.NodeId == "App.Caller.InvokeTarget()" && n.Distance == 2);
            var implImpact = analyzer.AnalyzeImpact(impGraph, "App.Target.Run()")!;
            Assert.DoesNotContain(implImpact.ImpactedNodes, n => n.NodeId == "App.Sibling.Run()");

            // 4. End-to-end Roslyn: RemoveAll preserves keyed scoped; unactivatable primitive ctor not captive;
            //    AddDbContextFactory Transient context not captive; local function parameter attribute live;
            //    CC counts 'and'/'or' patterns & '?.'; RoslynCodeGraphExtractor rejects binary-only dir with NotSupportedException.
            var binOnlyDir = Path.Combine(tempDir, "BinOnly");
            Directory.CreateDirectory(binOnlyDir);
            File.WriteAllBytes(Path.Combine(binOnlyDir, "Sample.dll"), [0x4D, 0x5A]);
            Assert.Throws<NotSupportedException>(() => RoslynCodeGraphExtractor.ResolveTargetFile(binOnlyDir));

            var projDir = Path.Combine(tempDir, "C17Probe");
            WriteFile(projDir, "C17Probe.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
                </Project>
                """);
            WriteFile(projDir, "Program.cs", """
                using C17Probe;
                IServiceCollection services = null!;
                services.AddKeyedScoped<IScoped, KeyedScoped>("k1");
                services.AddScoped<IScoped, UnkeyedScoped>();
                services.RemoveAll<IScoped>();
                services.AddSingleton<KeyedHolder>();
                services.AddSingleton<UnactivatableSingleton>();
                services.AddDbContextFactory<TransientDb>(ServiceLifetime.Transient);
                services.AddSingleton<SingletonTakesTransientDb>();
                LocalAttrUser.Run("ok");
                CcProbe.Evaluate("abc");

                namespace Microsoft.Extensions.DependencyInjection
                {
                    [System.AttributeUsage(System.AttributeTargets.Parameter)]
                    public sealed class FromKeyedServicesAttribute(object key) : System.Attribute { public object Key { get; } = key; }
                }
                namespace C17Probe
                {
                    public enum ServiceLifetime { Singleton, Scoped, Transient }
                    public interface IServiceCollection { }
                    public static class DiExt
                    {
                        public static IServiceCollection AddScoped<T1, T2>(this IServiceCollection s) => s;
                        public static IServiceCollection AddKeyedScoped<T1, T2>(this IServiceCollection s, object key) => s;
                        public static IServiceCollection RemoveAll<T1>(this IServiceCollection s) => s;
                        public static IServiceCollection AddSingleton<T1>(this IServiceCollection s) => s;
                        public static IServiceCollection AddDbContextFactory<TContext>(this IServiceCollection s, ServiceLifetime lifetime) => s;
                    }
                    public interface IScoped { }
                    public sealed class KeyedScoped : IScoped { }
                    public sealed class UnkeyedScoped : IScoped { }
                    public sealed class KeyedHolder([Microsoft.Extensions.DependencyInjection.FromKeyedServices("k1")] IScoped dep) { }
                    public sealed class UnactivatableSingleton(IScoped dep, string rawConfig) { }
                    public sealed class TransientDb { }
                    public sealed class SingletonTakesTransientDb(TransientDb db) { }

                    [System.AttributeUsage(System.AttributeTargets.Parameter)]
                    public sealed class LocalParamTagAttribute : System.Attribute { }

                    public static class LocalAttrUser
                    {
                        public static int Run(string input)
                        {
                            static int Helper([LocalParamTag] string s) => s.Length;
                            return Helper(input);
                        }
                    }

                    public static class CcProbe
                    {
                        public static int Evaluate(string? s)
                        {
                            var len = s?.Length ?? 0;
                            if (len is > 0 and < 10 or 42) return 1;
                            return 0;
                        }
                    }
                }
                """);

            using var roslyn = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snap = await roslyn.ExtractAsync(Path.Combine(projDir, "C17Probe.csproj"), new ExtractionOptions(IncludeMethods: true));

            var di = analyzer.AnalyzeDi(snap.Graph);
            Assert.Contains(di.CaptiveDependencies, c => c.ConsumerTypeId == "C17Probe.KeyedHolder" && c.ResolvedImplementationTypeId == "C17Probe.KeyedScoped");
            Assert.DoesNotContain(di.CaptiveDependencies, c => c.ConsumerTypeId == "C17Probe.UnactivatableSingleton");
            Assert.DoesNotContain(di.CaptiveDependencies, c => c.ConsumerTypeId == "C17Probe.SingletonTakesTransientDb");

            var dead = analyzer.AnalyzeDeadCode(snap.Graph, NodeKind.Type, profile: "app");
            Assert.DoesNotContain(dead.Candidates, c => c.NodeId == "C17Probe.LocalParamTagAttribute");

            var ccMethod = snap.Graph.GetNodes(NodeKind.Method)
                .FirstOrDefault(m => m.FullName.StartsWith("C17Probe.CcProbe.Evaluate(", StringComparison.Ordinal));
            Assert.NotNull(ccMethod);
            // Base 1 + '?.' (1) + '??' (1) + 'if' (1) + 'and' (1) + 'or' (1) = 6
            Assert.Equal(6, ccMethod!.CyclomaticComplexity);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Cycle18Fixes_IncrementalLoadStatus_RuleEvalException_ActivatorSuppliedArgs_ClosedGeneric_EfLifetimes_Nameof_Lcom4Override_TypeImpact_YamlDupId()
    {
        var tempDir = CreateTempDir();
        try
        {
            var analyzer = new ArchitectureAnalyzerService();
            var yaml = new YamlRuleSetProvider();

            // 1. Duplicate custom rule IDs inside one YAML rules: list are rejected
            Assert.Throws<InvalidOperationException>(() => yaml.ParseYaml("""
                version: 1
                rules:
                  - id: DUP-1
                    type: no_cycles
                  - id: DUP-1
                    type: no_cycles
                """));

            // 2. Throwing rule evaluation produces Error + ConfigurationError = true (not silent warning)
            var emptyGraph = new CodeGraph(tempDir, [], []);
            var throwCheck = analyzer.CheckRules(emptyGraph, [new ThrowingTestRule()]);
            Assert.True(throwCheck.ConfigurationError);
            Assert.Equal(1, throwCheck.ErrorCount);

            // 3. IL fail-closed rules set ConfigurationError = true and populate diff.Note
            var ilGraph = new CodeGraph(tempDir, [], [],
                loadStatus: new GraphLoadStatus("il", 1, 1, 0, false, true));
            var ilSnap = new ArchitectureSnapshot("il-1", tempDir, DateTimeOffset.UtcNow, ilGraph);
            var ilDiff = analyzer.DiffSnapshots(ilSnap, ilSnap, [
                new NoCaptiveDependenciesRule("CAP-1", "No captive", "", RuleSeverity.Error),
                new MaxMetricRule("COG-1", "Cog", "", RuleSeverity.Warning, "cognitive_complexity", 10)
            ]);
            Assert.True(ilDiff.ConfigurationError);
            Assert.False(string.IsNullOrWhiteSpace(ilDiff.Note));

            // 4. Type-level impact bridges concrete implementation -> interface consumers without sibling leak
            var impNodes = new List<CodeNode>
            {
                new("App.IService", "IService", "App.IService", NodeKind.Type, "App", "App", TypeKind.Interface),
                new("App.Service", "Service", "App.Service", NodeKind.Type, "App", "App", TypeKind.Class),
                new("App.OtherService", "OtherService", "App.OtherService", NodeKind.Type, "App", "App", TypeKind.Class),
                new("App.Consumer", "Consumer", "App.Consumer", NodeKind.Type, "App", "App", TypeKind.Class)
            };
            var impEdges = new List<DependencyEdge>
            {
                new("App.Service", "App.IService", DependencyKind.Implements),
                new("App.OtherService", "App.IService", DependencyKind.Implements),
                new("App.Consumer", "App.IService", DependencyKind.Parameter)
            };
            var impGraph = new CodeGraph(tempDir, impNodes, impEdges);
            var svcImpact = analyzer.AnalyzeImpact(impGraph, "App.Service")!;
            Assert.Equal(1, svcImpact.DirectDependentsCount);
            Assert.Contains(svcImpact.ImpactedNodes, n => n.NodeId == "App.Consumer");
            Assert.DoesNotContain(svcImpact.ImpactedNodes, n => n.NodeId == "App.OtherService");

            // 5. DiffSnapshots respects cross_project_only on NoCyclesRule
            var cycBase = new ArchitectureSnapshot("b", tempDir, DateTimeOffset.UtcNow, new CodeGraph(tempDir, [], []));
            var cycCurr = new ArchitectureSnapshot("c", tempDir, DateTimeOffset.UtcNow, new CodeGraph(tempDir,
                [
                    new("App.A.X", "X", "App.A.X", NodeKind.Type, "App", "App.A", TypeKind.Class),
                    new("App.B.Y", "Y", "App.B.Y", NodeKind.Type, "App", "App.B", TypeKind.Class)
                ],
                [
                    new("App.A.X", "App.B.Y", DependencyKind.FieldOrProperty),
                    new("App.B.Y", "App.A.X", DependencyKind.FieldOrProperty)
                ]));
            var crossOnlyRule = new NoCyclesRule("CYC-X", "Cross-project", "", RuleSeverity.Error, NodeKind.Namespace, crossProjectOnly: true);
            var crossDiff = analyzer.DiffSnapshots(cycBase, cycCurr, [crossOnlyRule]);
            Assert.Empty(crossDiff.AddedCycles);

            // 6. End-to-end Roslyn: ActivatorUtilities explicit args not captive; closed generic IRepo<int> does not match IRepo<string>;
            //    AddDbContext(Transient) keeps DbContextOptions Scoped; AddPooledDbContextFactory registers factory/options/context;
            //    nameof(NameofTarget.Run) keeps method live; assembly attr keeps AsmOnly live; LCOM4 override call connects;
            //    incremental edit updates LoadStatus.Degraded cleanly.
            var projDir = Path.Combine(tempDir, "C18Probe");
            WriteFile(projDir, "C18Probe.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
                </Project>
                """);
            WriteFile(projDir, "Program.cs", """
                using C18Probe;
                [assembly: Marker(typeof(AsmOnly))]

                IServiceCollection services = null!;
                services.AddScoped<IScopedDep, ScopedDep>();
                services.AddSingleton<ExplicitActivatorConsumer>(sp =>
                    new ExplicitActivatorConsumer(ActivatorUtilities.CreateInstance<ExplicitTarget>(sp, new ScopedDep(), "x")));
                services.AddScoped<IClosedRepo<int>, ClosedRepo<int>>();
                services.AddSingleton<ClosedRepoConsumer>();
                services.AddDbContext<TransientCtx>(ServiceLifetime.Transient);
                services.AddSingleton<TransientOptsConsumer>();
                services.AddPooledDbContextFactory<PooledCtx>();
                _ = nameof(NameofTarget.Run);
                _ = new OverrideCohesion().UsesValue();

                namespace Microsoft.EntityFrameworkCore
                {
                    public class DbContextOptions<TContext> { }
                    public interface IDbContextFactory<TContext> { }
                }
                namespace C18Probe
                {
                    public enum ServiceLifetime { Singleton, Scoped, Transient }
                    public interface IServiceProviderEx { }
                    public interface IServiceCollection { }
                    public static class ActivatorUtilities
                    {
                        public static T CreateInstance<T>(IServiceProviderEx sp, params object[] args) => default!;
                    }
                    public static class DiExt
                    {
                        public static IServiceCollection AddScoped<T1, T2>(this IServiceCollection s) => s;
                        public static IServiceCollection AddSingleton<T1>(this IServiceCollection s) => s;
                        public static IServiceCollection AddSingleton<T1>(this IServiceCollection s, System.Func<IServiceProviderEx, T1> f) => s;
                        public static IServiceCollection AddDbContext<TContext>(this IServiceCollection s, ServiceLifetime contextLifetime) => s;
                        public static IServiceCollection AddPooledDbContextFactory<TContext>(this IServiceCollection s) => s;
                    }

                    [System.AttributeUsage(System.AttributeTargets.Assembly)]
                    public sealed class MarkerAttribute(System.Type t) : System.Attribute { public System.Type T { get; } = t; }
                    public sealed class AsmOnly { public void UnusedMethod() { } }

                    public interface IScopedDep { }
                    public sealed class ScopedDep : IScopedDep { }
                    public sealed class ExplicitTarget(IScopedDep dep, string name) { }
                    public sealed class ExplicitActivatorConsumer(ExplicitTarget t) { }

                    public interface IClosedRepo<T> { }
                    public sealed class ClosedRepo<T> : IClosedRepo<T> { }
                    public sealed class ClosedRepoConsumer(IClosedRepo<string> repo) { }

                    public sealed class TransientCtx { }
                    public sealed class TransientOptsConsumer(Microsoft.EntityFrameworkCore.DbContextOptions<TransientCtx> opts) { }
                    public sealed class PooledCtx { }

                    public static class NameofTarget { public static void Run() { } }

                    public class OverrideBase { public virtual int Value() => 0; }
                    public class OverrideCohesion : OverrideBase
                    {
                        private int _m;
                        public override int Value() => 1;
                        public int UsesValue() => Value();
                        public int UsesValueTwice() => Value() + Value();
                        public int TouchM() => _m;
                    }
                }
                """);
            WriteFile(projDir, "Mutable.cs", "namespace C18Probe; public sealed class Mutable { public int Ok() => 1; }");

            using var roslyn = new RoslynCodeGraphExtractor(keepWorkspacesWarm: true);
            var csprojPath = Path.Combine(projDir, "C18Probe.csproj");
            var snap = await roslyn.ExtractAsync(csprojPath, new ExtractionOptions(IncludeMethods: true));
            Assert.False(snap.Graph.LoadStatus?.Degraded ?? false);

            var di = analyzer.AnalyzeDi(snap.Graph);
            Assert.DoesNotContain(di.CaptiveDependencies, c => c.ConsumerTypeId == "C18Probe.ExplicitActivatorConsumer");
            Assert.DoesNotContain(di.CaptiveDependencies, c => c.ConsumerTypeId == "C18Probe.ClosedRepoConsumer");
            Assert.Contains(di.CaptiveDependencies, c => c.ConsumerTypeId == "C18Probe.TransientOptsConsumer" && c.DependencyTypeId.Contains("DbContextOptions<C18Probe.TransientCtx>"));
            Assert.Contains(di.Registrations, r => r.ServiceTypeId.Contains("IDbContextFactory<C18Probe.PooledCtx>") && r.Lifetime == ServiceLifetimeKind.Singleton);
            Assert.Contains(di.Registrations, r => r.ServiceTypeId.Contains("DbContextOptions<C18Probe.PooledCtx>") && r.Lifetime == ServiceLifetimeKind.Singleton);

            var deadTypes = analyzer.AnalyzeDeadCode(snap.Graph, NodeKind.Type, profile: "app");
            Assert.DoesNotContain(deadTypes.Candidates, c => c.NodeId == "C18Probe.AsmOnly");
            var deadMethods = analyzer.AnalyzeDeadCode(snap.Graph, NodeKind.Method, profile: "app");
            Assert.DoesNotContain(deadMethods.Candidates, c => c.FullName.Contains("NameofTarget.Run"));

            Assert.Equal(2, snap.Graph.FindNode("C18Probe.OverrideCohesion")!.Lcom4);

            // Incremental edit introducing CS0246 flips LoadStatus.Degraded to true; fixing it flips back to false
            var mutablePath = Path.Combine(projDir, "Mutable.cs");
            File.WriteAllText(mutablePath, "namespace C18Probe; public sealed class Mutable { public MissingType Broken() => null!; }");
            var brokenSnap = await roslyn.ExtractIncrementalAsync(csprojPath, snap, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mutablePath }, new ExtractionOptions(IncludeMethods: true));
            Assert.True(brokenSnap.Graph.LoadStatus?.Degraded ?? false);

            File.WriteAllText(mutablePath, "namespace C18Probe; public sealed class Mutable { public int Fixed() => 2; }");
            var fixedSnap = await roslyn.ExtractIncrementalAsync(csprojPath, brokenSnap, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mutablePath }, new ExtractionOptions(IncludeMethods: true));
            Assert.False(fixedSnap.Graph.LoadStatus?.Degraded ?? true);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Cycle19Fixes_AllowOnlyWildcard_ImpactSiblings_OpenGenericDiAndTryAdd_CacheLinkedAndEditorConfig_BaselineSarifDiffYamlAndRazor()
    {
        var tempDir = CreateTempDir();
        try
        {
            var analyzer = new ArchitectureAnalyzerService();
            var yaml = new YamlRuleSetProvider();

            // 1. YAML: reject case-equivalent duplicate layers keys and misplaced exclude: on forbid_dependency
            Assert.Throws<InvalidOperationException>(() => yaml.ParseYaml("""
                version: 1
                preset: clean-architecture
                layers:
                  domain: ["App.Domain.*"]
                  Domain: ["App.Other.*"]
                """));
            var misplacedEx = Assert.Throws<InvalidOperationException>(() => yaml.ParseYaml("""
                version: 1
                rules:
                  - id: F1
                    type: forbid_dependency
                    from: "App.Domain.*"
                    to: ["App.Infra.*"]
                    exclude: ["App.Domain.Allowed"]
                """));
            Assert.Contains("exclude_from", misplacedEx.Message);

            // 2. AllowOnlyDependenciesRule with from: "*" is NOT vacuously skipped as same-source-layer
            var allowGraph = new CodeGraph(
                rootPath: tempDir,
                nodes:
                [
                    new CodeNode("App.Api.Ctrl", "Ctrl", "App.Api.Ctrl", NodeKind.Type, "App", "App.Api", TypeKind.Class),
                    new CodeNode("App.Core.Svc", "Svc", "App.Core.Svc", NodeKind.Type, "App", "App.Core", TypeKind.Class),
                    new CodeNode("App.Legacy.Old", "Old", "App.Legacy.Old", NodeKind.Type, "App", "App.Legacy", TypeKind.Class)
                ],
                edges:
                [
                    new DependencyEdge("App.Api.Ctrl", "App.Core.Svc", DependencyKind.MethodCall),
                    new DependencyEdge("App.Api.Ctrl", "App.Legacy.Old", DependencyKind.MethodCall)
                ],
                projects: [new ArchLens.Model.ProjectInfo("App", "App.csproj", "net10.0", [], [], 3, 2)]);
            var allowRule = new AllowOnlyDependenciesRule("ALL-1", "Only Core", "", RuleSeverity.Error, "*", ["App.Core.*"]);
            var allowReport = analyzer.CheckRules(allowGraph, [allowRule]);
            Assert.Single(allowReport.Violations);
            Assert.Equal("App.Legacy.Old", allowReport.Violations[0].TargetNodeId);

            // 3. Partial unmatched custom selector advisory + RulesEvaluated == EvaluatedRules.Count with ARCHLENS-CONFIG-001
            var activeRule = new ForbidDependencyRule("REAL-1", "Real", "", RuleSeverity.Error, "App.Api.*", ["App.Core.*"]);
            var ghostRule = new ForbidDependencyRule("GHOST-1", "Ghost", "", RuleSeverity.Error, "App.DoesNotExist.*", ["App.Core.*"]);
            var partialReport = analyzer.CheckRules(allowGraph, [activeRule, ghostRule]);
            Assert.NotNull(partialReport.Note);
            Assert.Contains("GHOST-1", partialReport.Note);

            var vacuousReport = analyzer.CheckRules(allowGraph, [activeRule], rootNamespace: "NonExistentRoot");
            Assert.True(vacuousReport.VacuousConfiguration);
            Assert.Equal(vacuousReport.EvaluatedRules!.Count, vacuousReport.RulesEvaluated);

            // 4. DiffSnapshots filters namespace cycles by NoCyclesRule.TargetPattern
            var baseCycleGraph = new CodeGraph(
                rootPath: tempDir,
                nodes:
                [
                    new CodeNode("App.OutA.X", "X", "App.OutA.X", NodeKind.Type, "App", "App.OutA", TypeKind.Class),
                    new CodeNode("App.OutB.Y", "Y", "App.OutB.Y", NodeKind.Type, "App", "App.OutB", TypeKind.Class)
                ],
                edges: [],
                projects: [new ArchLens.Model.ProjectInfo("App", "App.csproj", "net10.0", [], [], 2, 0)]);
            var currCycleGraph = new CodeGraph(
                rootPath: tempDir,
                nodes:
                [
                    new CodeNode("App.OutA.X", "X", "App.OutA.X", NodeKind.Type, "App", "App.OutA", TypeKind.Class),
                    new CodeNode("App.OutB.Y", "Y", "App.OutB.Y", NodeKind.Type, "App", "App.OutB", TypeKind.Class),
                    new CodeNode("ns:App.OutA", "App.OutA", "App.OutA", NodeKind.Namespace, "App", "App.OutA"),
                    new CodeNode("ns:App.OutB", "App.OutB", "App.OutB", NodeKind.Namespace, "App", "App.OutB")
                ],
                edges:
                [
                    new DependencyEdge("App.OutA.X", "App.OutB.Y", DependencyKind.MethodCall),
                    new DependencyEdge("App.OutB.Y", "App.OutA.X", DependencyKind.MethodCall)
                ],
                projects: [new ArchLens.Model.ProjectInfo("App", "App.csproj", "net10.0", [], [], 2, 2)]);
            var narrowCycleRule = new NoCyclesRule("CYC-1", "Scoped", "", RuleSeverity.Error, NodeKind.Namespace, targetPattern: "App.InScope.*");
            var scopedDiff = analyzer.DiffSnapshots(
                new ArchitectureSnapshot("b1", tempDir, DateTimeOffset.UtcNow, baseCycleGraph),
                new ArchitectureSnapshot("c1", tempDir, DateTimeOffset.UtcNow, currCycleGraph),
                [narrowCycleRule]);
            Assert.Empty(scopedDiff.AddedCycles);
            Assert.Empty(scopedDiff.NewViolations);

            // 5. BaselineStore rejects duplicate JSON keys, duplicate node IDs, and directory targets
            Assert.Throws<ArgumentException>(() => BaselineStore.Save(tempDir, new ArchitectureSnapshot("s1", tempDir, DateTimeOffset.UtcNow, allowGraph)));
            var dupKeyJson = Path.Combine(tempDir, "dup-key.json");
            File.WriteAllText(dupKeyJson, """{"version":7,"version":1,"snapshotId":"s","targetPath":"t","capturedAt":"2026-01-01T00:00:00Z","nodes":[],"edges":[],"projects":[]}""");
            Assert.Throws<InvalidDataException>(() => BaselineStore.Load(dupKeyJson));
            var dupNodeJson = Path.Combine(tempDir, "dup-node.json");
            File.WriteAllText(dupNodeJson, """{"version":1,"snapshotId":"s","targetPath":"t","capturedAt":"2026-01-01T00:00:00Z","nodes":[{"id":"A","name":"A","fullName":"A","kind":"type","projectName":"P"},{"id":"A","name":"A","fullName":"A","kind":"type","projectName":"P"}],"edges":[],"projects":[{"name":"P"}]}""");
            Assert.Throws<InvalidDataException>(() => BaselineStore.Load(dupNodeJson));

            // 6. SARIF executionSuccessful: false with toolErrors
            var failedSarif = SarifExporter.Export(toolErrors: ["Degraded workspace"], executionSuccessful: false);
            using (var sdoc = System.Text.Json.JsonDocument.Parse(failedSarif))
            {
                var inv = sdoc.RootElement.GetProperty("runs")[0].GetProperty("invocations")[0];
                Assert.False(inv.GetProperty("executionSuccessful").GetBoolean());
                Assert.Equal("error", inv.GetProperty("toolExecutionNotifications")[0].GetProperty("level").GetString());
            }

            // 7. WorkspaceSessionManager: degraded snapshot is not auto-seeded as implicit session baseline
            var degGraph = new CodeGraph(
                rootPath: tempDir,
                nodes: [],
                edges: [],
                projects: [],
                loadStatus: new GraphLoadStatus("roslyn", 1, 0, 1, Degraded: true, Approximate: false, Note: "broken"));
            using var degMgr = new WorkspaceSessionManager([new StubGraphExtractor(degGraph)]);
            _ = await degMgr.GetOrLoadAsync(tempDir);
            Assert.Null(degMgr.GetBaseline(tempDir));

            // 8. Linked <Compile Include="..\external\LinkedCode.cs" /> + ancestor .editorconfig invalidate SQLite cache
            var cacheRoot = Path.Combine(tempDir, "LinkedCache");
            var ancestorEditorConfig = Path.Combine(cacheRoot, ".editorconfig");
            WriteFile(cacheRoot, ".editorconfig", "root = true\n\n[*.cs]\ndotnet_diagnostic.CA1000.severity = none\n");
            var extFile = Path.Combine(cacheRoot, "external", "LinkedCode.cs");
            WriteFile(cacheRoot, "external/LinkedCode.cs", "namespace LinkedApp; public sealed class LinkedV1 { }");
            var appCsproj = Path.Combine(cacheRoot, "app", "LinkedApp.csproj");
            WriteFile(cacheRoot, "app/LinkedApp.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Library</OutputType>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="..\external\LinkedCode.cs" Link="LinkedCode.cs" />
                  </ItemGroup>
                </Project>
                """);
            WriteFile(cacheRoot, "app/Local.cs", "namespace LinkedApp; public sealed class LocalType { }");
            var cacheDb = Path.Combine(tempDir, "c19-linked.db");
            using (var c1 = new SqliteGraphCache(cacheDb))
            using (var r1 = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false))
            using (var m1 = new WorkspaceSessionManager([r1], c1))
            {
                var snap1 = await m1.GetOrLoadAsync(appCsproj);
                Assert.Contains(snap1.Graph.GetNodes(NodeKind.Type), n => n.FullName == "LinkedApp.LinkedV1");
            }
            File.WriteAllText(extFile, "namespace LinkedApp; public sealed class LinkedV2 { }");
            string snap2Id;
            using (var c2 = new SqliteGraphCache(cacheDb))
            using (var r2 = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false))
            using (var m2 = new WorkspaceSessionManager([r2], c2))
            {
                var snap2 = await m2.GetOrLoadAsync(appCsproj);
                snap2Id = snap2.SnapshotId;
                Assert.Contains(snap2.Graph.GetNodes(NodeKind.Type), n => n.FullName == "LinkedApp.LinkedV2");
                Assert.DoesNotContain(snap2.Graph.GetNodes(NodeKind.Type), n => n.FullName == "LinkedApp.LinkedV1");
            }
            // Editing inherited ancestor .editorconfig invalidates persistent SQLite cache
            File.WriteAllText(ancestorEditorConfig, "root = true\n\n[*.cs]\ndotnet_diagnostic.CA1000.severity = warning\n");
            using (var c3 = new SqliteGraphCache(cacheDb))
            using (var r3 = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false))
            using (var m3 = new WorkspaceSessionManager([r3], c3))
            {
                var snap3 = await m3.GetOrLoadAsync(appCsproj);
                Assert.NotEqual(snap2Id, snap3.SnapshotId);
            }

            // 9. Roslyn semantics: type & method impact sibling isolation, open-generic DI substitution, conditional TryAdd, Razor @{...} complexity
            var semDir = Path.Combine(tempDir, "C19Sem");
            WriteFile(semDir, "C19Sem.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);
            WriteFile(semDir, "DiStubs.cs", """
                namespace Microsoft.Extensions.DependencyInjection
                {
                    public interface IServiceCollection { }
                    public static class ServiceCollectionExtensions
                    {
                        public static IServiceCollection AddSingleton<TS>(this IServiceCollection s) => s;
                        public static IServiceCollection AddSingleton<TS, TI>(this IServiceCollection s) where TI : TS => s;
                        public static IServiceCollection AddScoped<TS, TI>(this IServiceCollection s) where TI : TS => s;
                        public static IServiceCollection AddTransient(this IServiceCollection s, Type svc, Type impl) => s;
                        public static void TryAddSingleton<TS, TI>(this IServiceCollection s) where TI : TS { }
                    }
                }
                """);
            WriteFile(semDir, "Program.cs", """
                using Microsoft.Extensions.DependencyInjection;

                namespace C19Sem;

                public interface IShape { void Draw(); }
                public sealed class Circle : IShape { public void Draw() { } }
                public sealed class Square : IShape { public void Draw() { } }
                public sealed class Drawer(IShape s) { public void Render() => s.Draw(); }

                public abstract class BaseOp { public virtual void Execute() { } }
                public sealed class Derived1 : BaseOp { public override void Execute() { } }
                public sealed class Derived2 : BaseOp { public override void Execute() { } }
                public sealed class OpCaller { public void Call(BaseOp b) => b.Execute(); }

                public interface IDep<T> { }
                public sealed class DepInt : IDep<int> { }
                public interface IRepo<T> { }
                public sealed class RepoImpl<T>(IDep<T> dep) : IRepo<T> { }
                public sealed class GenericConsumer(IRepo<int> repo) { }

                public interface IBranchSvc { }
                public sealed class ScopedA : IBranchSvc { }
                public sealed class ScopedB : IBranchSvc { }
                public sealed class SingletonC : IBranchSvc { }
                public sealed class BranchConsumer(IBranchSvc svc) { }

                public interface ISingleBranchSvc { }
                public sealed class SingleScoped : ISingleBranchSvc { }
                public sealed class SingleSingleton : ISingleBranchSvc { }
                public sealed class SingleBranchConsumer(ISingleBranchSvc svc) { }

                public static class Program
                {
                    public static void Main(IServiceCollection services, bool cond)
                    {
                        services.AddScoped<IDep<int>, DepInt>();
                        services.AddTransient(typeof(IRepo<>), typeof(RepoImpl<>));
                        services.AddSingleton<GenericConsumer>();

                        if (cond) { services.AddScoped<IBranchSvc, ScopedA>(); }
                        else { services.AddScoped<IBranchSvc, ScopedB>(); }
                        services.TryAddSingleton<IBranchSvc, SingletonC>();
                        services.AddSingleton<BranchConsumer>();

                        if (cond) { services.AddScoped<ISingleBranchSvc, SingleScoped>(); }
                        services.TryAddSingleton<ISingleBranchSvc, SingleSingleton>();
                        services.AddSingleton<SingleBranchConsumer>();
                    }
                }
                """);
            WriteFile(semDir, "Widget.razor", """
                @namespace C19Sem
                @{
                    var total = 0;
                    if (total == 0)
                    {
                        for (var i = 0; i < 3; i++)
                        {
                            total += i;
                        }
                    }
                }
                <p>@total</p>
                """);

            using var roslyn = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var semSnap = await roslyn.ExtractAsync(Path.Combine(semDir, "C19Sem.csproj"), new ExtractionOptions(IncludeMethods: true));

            // Type impact: Circle reaches Drawer, does NOT leak Square
            var circleImpact = analyzer.AnalyzeImpact(semSnap.Graph, "C19Sem.Circle")!;
            Assert.Contains(circleImpact.ImpactedNodes, n => n.NodeId == "C19Sem.Drawer");
            Assert.DoesNotContain(circleImpact.ImpactedNodes, n => n.NodeId == "C19Sem.Square");

            // Method impact: Derived1.Execute reaches OpCaller.Call, does NOT leak Derived2.Execute
            var d1Impact = analyzer.AnalyzeImpact(semSnap.Graph, "C19Sem.Derived1.Execute()")!;
            Assert.Contains(d1Impact.ImpactedNodes, n => n.FullName.Contains("OpCaller.Call"));
            Assert.DoesNotContain(d1Impact.ImpactedNodes, n => n.FullName.Contains("Derived2.Execute"));

            // DI: open-generic substitution + conditional TryAdd both detect captives
            var diReport = analyzer.AnalyzeDi(semSnap.Graph);
            Assert.Contains(diReport.CaptiveDependencies, c => c.ConsumerTypeId == "C19Sem.GenericConsumer" && c.ResolvedImplementationTypeId == "C19Sem.DepInt");
            Assert.Contains(diReport.CaptiveDependencies, c => c.ConsumerTypeId == "C19Sem.BranchConsumer");
            Assert.Contains(diReport.CaptiveDependencies, c => c.ConsumerTypeId == "C19Sem.SingleBranchConsumer");

            // Razor inline @{ ... } block complexity counted (base 1 + if 1 + for 1 = 3; cog = 1 + 2 = 3)
            var razorNode = semSnap.Graph.FindNode("C19Sem.Widget");
            Assert.NotNull(razorNode);
            Assert.True(razorNode!.CyclomaticComplexity >= 3);
            Assert.True(razorNode.CognitiveComplexity >= 3);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Cycle20Fixes_PresetCollision_MethodMetricGuard_SelectorAdvisories_SqlAlias_SliceEmpty_FileLocal_Operators_Reflection_DiScope_SarifProject()
    {
        var tempDir = CreateTempDir();
        try
        {
            var analyzer = new ArchitectureAnalyzerService();
            var yaml = new YamlRuleSetProvider();

            // 1. Cross-type preset ID shadowing is rejected (AL-01)
            Assert.Throws<InvalidOperationException>(() => yaml.ParseYaml("""
                version: 1
                preset: clean-architecture
                rules:
                  - id: CLEAN-001
                    type: max_metric
                    metric: lcom4
                    threshold: 999
                """));

            // 2. Explicit empty allow: [] on slice_isolation is preserved (AL-14)
            var sliceRules = yaml.ParseYaml("""
                version: 1
                rules:
                  - id: SL-EMPTY
                    type: slice_isolation
                    segment: Features
                    allow: []
                """);
            var sliceGraph = new CodeGraph(tempDir,
                [
                    new("App.Features.Orders.Handler", "Handler", "App.Features.Orders.Handler", NodeKind.Type, "App", "App.Features.Orders", TypeKind.Class),
                    new("App.Features.Billing.Contracts.Invoice", "Invoice", "App.Features.Billing.Contracts.Invoice", NodeKind.Type, "App", "App.Features.Billing.Contracts", TypeKind.Class)
                ],
                [
                    new("App.Features.Orders.Handler", "App.Features.Billing.Contracts.Invoice", DependencyKind.Parameter)
                ]);
            var sliceCheck = analyzer.CheckRules(sliceGraph, sliceRules);
            Assert.Equal(1, sliceCheck.TotalViolations);

            // 3. MaxMetricRule(scope: Method) fails closed when MethodsIncluded == false (AL-02)
            var noMethodsGraph = new CodeGraph(tempDir,
                [new("App.Foo", "Foo", "App.Foo", NodeKind.Type, "App", "App", TypeKind.Class)],
                [],
                methodsIncluded: false);
            var methodMetricRule = new MaxMetricRule("MET-M", "Method CC", "", RuleSeverity.Error, "cyclomatic_complexity", 5, scope: NodeKind.Method);
            var noMethodCheck = analyzer.CheckRules(noMethodsGraph, [methodMetricRule]);
            Assert.True(noMethodCheck.ConfigurationError);

            // 4. Unmatched selectors surface advisory on MaxMetricRule / GodClassRule / NamingConventionRule / SliceIsolationRule (AL-03)
            var advisoryCheck = analyzer.CheckRules(noMethodsGraph,
            [
                new MaxMetricRule("M-MISS", "M", "", RuleSeverity.Warning, "lcom4", 2, targetPattern: "NoSuch.*"),
                new GodClassRule("G-MISS", "G", "", RuleSeverity.Warning, targetPatterns: ["NoSuch.*"]),
                new NamingConventionRule("N-MISS", "N", "", RuleSeverity.Warning, "NoSuch.*", "*Svc"),
                new SliceIsolationRule("S-MISS", "S", "", RuleSeverity.Warning, "NonExistentSegment", ["Contracts"])
            ]);
            Assert.NotNull(advisoryCheck.Note);
            Assert.Contains("M-MISS", advisoryCheck.Note);
            Assert.Contains("G-MISS", advisoryCheck.Note);
            Assert.Contains("N-MISS", advisoryCheck.Note);
            Assert.Contains("S-MISS", advisoryCheck.Note);

            // 5. SQL read-only validator allows reserved keywords as column aliases after AS (AL-07)
            GraphSqlQueryEngine.ValidateReadOnlySql("SELECT name AS delete, id AS attach FROM nodes LIMIT 1");

            // 6. SARIF preserves project attribution on linked-file duplicate diagnostics (AL-06)
            var sarifJson = SarifExporter.Export(analyzerDiagnostics:
            [
                new AnalyzerDiagnostic("CA1822", "Mark static", "Performance", RuleSeverity.Info, "Mark static", new SourceLocation("../Shared.cs", 5), "P1"),
                new AnalyzerDiagnostic("CA1822", "Mark static", "Performance", RuleSeverity.Info, "Mark static", new SourceLocation("../Shared.cs", 5), "P2")
            ]);
            using (var doc = System.Text.Json.JsonDocument.Parse(sarifJson))
            {
                var results = doc.RootElement.GetProperty("runs")[0].GetProperty("results");
                Assert.Equal(2, results.GetArrayLength());
            }

            // 7. Analyzer diagnostics reconciled into EvaluatedRules (AUD-02)
            var diagGraph = new CodeGraph(tempDir,
                [new("App.Foo", "Foo", "App.Foo", NodeKind.Type, "App", "App", TypeKind.Class)],
                [],
                analyzerDiagnostics:
                [
                    new AnalyzerDiagnostic("CA1822", "Mark static", "Performance", RuleSeverity.Warning, "Mark static", new SourceLocation("Foo.cs", 5), "App")
                ]);
            var diagCheck = analyzer.CheckRules(diagGraph, [new NoCyclesRule("CYC", "Cycles", "", RuleSeverity.Error, NodeKind.Namespace)], includeAnalyzerDiagnostics: true);
            Assert.NotNull(diagCheck.EvaluatedRules);
            Assert.Equal(diagCheck.RulesEvaluated, diagCheck.EvaluatedRules!.Count);
            Assert.Equal(diagCheck.TotalViolations, diagCheck.EvaluatedRules.Sum(r => r.ViolationCount));

            // 8. DiffSnapshots propagates VacuousConfiguration into ConfigurationError (AL-13)
            var vacSnap = new ArchitectureSnapshot("v1", tempDir, DateTimeOffset.UtcNow, noMethodsGraph);
            var vacDiff = analyzer.DiffSnapshots(vacSnap, vacSnap, [new NoCyclesRule("CYC", "Cycles", "", RuleSeverity.Error, NodeKind.Namespace)], rootNamespace: "DoesNotExistRoot");
            Assert.True(vacDiff.VacuousConfiguration);
            Assert.True(vacDiff.ConfigurationError);

            // 9. Exact unqualified method name without params matches full method signature before suffix/contains (IMP-01)
            var methGraph = new CodeGraph(tempDir,
                [
                    new("App.Svc.Run()", "Run", "App.Svc.Run()", NodeKind.Method, "App", "App", ContainingTypeId: "App.Svc"),
                    new("App.Other.Run()", "Run", "App.Other.Run()", NodeKind.Method, "App", "App", ContainingTypeId: "App.Other")
                ],
                [],
                methodsIncluded: true);
            var (resolvedM, ambigM) = ArchitectureAnalyzerService.ResolveNodeWithAmbiguity(methGraph, "App.Svc.Run");
            Assert.NotNull(resolvedM);
            Assert.Empty(ambigM);
            Assert.Equal("App.Svc.Run()", resolvedM!.Id);

            // 10. End-to-end Roslyn: file-local types, user-defined operator edge, delegate Invoke, partial BaseList location,
            //     reflection GetMethod entry point, DI separate container Clear() isolation, DI RemoveAll wiring, Avalonia x:DataType
            var semDir = Path.Combine(tempDir, "C20Sem");
            WriteFile(semDir, "C20Sem.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
                </Project>
                """);
            WriteFile(semDir, "FileA.cs", """
                namespace C20Sem;
                file sealed class Secret { public static int V() => 1; }
                public static class UserA { public static int Go() => Secret.V(); }
                """);
            WriteFile(semDir, "FileB.cs", """
                namespace C20Sem;
                file sealed class Secret { public static int V() => 2; }
                public static class UserB { public static int Go() => Secret.V(); }
                """);
            WriteFile(semDir, "PartA.cs", """
                namespace C20Sem;
                public interface IPartMarker { }
                public partial class SplitType { public int A => 1; }
                """);
            WriteFile(semDir, "PartB.cs", """
                namespace C20Sem;
                public partial class SplitType : IPartMarker { public int B => 2; }
                """);
            WriteFile(semDir, "View.axaml", """
                <UserControl xmlns="https://github.com/avaloniaui"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             xmlns:vm="clr-namespace:C20Sem"
                             x:DataType="vm:AvaloniaVm" />
                """);
            WriteFile(semDir, "Program.cs", """
                using System;
                using C20Sem;

                [assembly: AsmMark(typeof(AsmMarkedTarget))]

                var a = new Vec(1) + new Vec(2);
                MyCallback cb = () => a.X;
                _ = cb();
                _ = UserA.Go() + UserB.Go();
                _ = new SplitType();
                _ = typeof(ReflTarget).GetMethod("Run");

                var s1 = new ServiceCollection();
                s1.AddScoped<IScopedA, ScopedA>();
                s1.AddSingleton<SingletonA>();
                var s2 = new ServiceCollection();
                s2.Clear();

                s1.RemoveAll<IWired>();

                namespace C20Sem
                {
                    [AttributeUsage(AttributeTargets.Assembly)]
                    public sealed class AsmMarkAttribute(Type t) : Attribute { public Type T { get; } = t; }
                    public sealed class AsmMarkedTarget { }
                    public delegate int MyCallback();
                    public sealed class AvaloniaVm { }
                    public sealed class ReflTarget { public void Run() { } }
                    public readonly record struct Vec(int X) { public static Vec operator +(Vec a, Vec b) => new(a.X + b.X); }

                    public interface IServiceCollection { void Clear(); }
                    public sealed class ServiceCollection : IServiceCollection { public void Clear() { } }
                    public static class DiExt
                    {
                        public static IServiceCollection AddScoped<T1, T2>(this IServiceCollection s) => s;
                        public static IServiceCollection AddSingleton<T1>(this IServiceCollection s) => s;
                        public static IServiceCollection RemoveAll<T1>(this IServiceCollection s) => s;
                    }
                    public interface IScopedA { }
                    public sealed class ScopedA : IScopedA { }
                    public sealed class SingletonA(IScopedA dep) { }
                    public interface IWired { }
                    public sealed class WiredImpl : IWired { }
                }
                """);

            using var roslyn = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var semSnap = await roslyn.ExtractAsync(Path.Combine(semDir, "C20Sem.csproj"), new ExtractionOptions(IncludeMethods: true));

            // File-local types get distinct IDs
            var secrets = semSnap.Graph.GetNodes(NodeKind.Type).Where(n => n.Name == "Secret").ToList();
            Assert.Equal(2, secrets.Count);
            Assert.NotEqual(secrets[0].Id, secrets[1].Id);

            // Operator + method node + call edge
            var opNode = semSnap.Graph.GetNodes(NodeKind.Method).FirstOrDefault(m => m.Name == "op_Addition" && m.ContainingTypeId == "C20Sem.Vec");
            Assert.NotNull(opNode);
            Assert.Contains(semSnap.Graph.Edges, e => e.Kind == DependencyKind.MethodCall && e.ToId == opNode!.Id);

            // Delegate Invoke exists and is NOT flagged dead
            var delInvoke = semSnap.Graph.GetNodes(NodeKind.Method).FirstOrDefault(m => m.FullName.Contains("MyCallback.Invoke"));
            Assert.NotNull(delInvoke);
            var deadMethods = analyzer.AnalyzeDeadCode(semSnap.Graph, NodeKind.Method, profile: "app");
            Assert.DoesNotContain(deadMethods.Candidates, c => c.NodeId == delInvoke!.Id);
            Assert.DoesNotContain(deadMethods.Candidates, c => c.FullName.Contains("ReflTarget.Run"));

            // Partial BaseList location points to PartB.cs
            var implEdge = semSnap.Graph.Edges.FirstOrDefault(e => e.FromId == "C20Sem.SplitType" && e.ToId == "C20Sem.IPartMarker" && e.Kind == DependencyKind.Implements);
            Assert.NotNull(implEdge);
            Assert.NotNull(implEdge!.Location);
            Assert.EndsWith("PartB.cs", implEdge.Location!.FilePath, StringComparison.OrdinalIgnoreCase);

            // Separate container Clear() did NOT erase s1's captive dependency
            var diReport = analyzer.AnalyzeDi(semSnap.Graph);
            Assert.Contains(diReport.CaptiveDependencies, c => c.ConsumerTypeId == "C20Sem.SingletonA");

            // Avalonia x:DataType keeps AvaloniaVm live
            var deadTypes = analyzer.AnalyzeDeadCode(semSnap.Graph, NodeKind.Type, profile: "app");
            Assert.DoesNotContain(deadTypes.Candidates, c => c.NodeId == "C20Sem.AvaloniaVm");

            // Assembly attribute edge from project node
            Assert.Contains(semSnap.Graph.Edges, e => e.FromId == "project:C20Sem" && e.ToId == "C20Sem.AsmMarkedTarget" && e.Kind == DependencyKind.Attribute);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Cycle21Fixes_GodClassBaseline_DegradedBaselineDiff_AnalyzerRuleIdCollision_SarifUriBase_SingletonChain_IndependentContainers_Conversions_NestedGenerics_RazorEdges()
    {
        var tempDir = CreateTempDir();
        try
        {
            var analyzer = new ArchitectureAnalyzerService();

            // 1. GodClassRule metric drift on an already-baselined type produces 0 new/resolved violations (AL-01)
            var godRule = new GodClassRule("GOD-1", "God class", "", RuleSeverity.Error, maxLines: 500, maxComplexity: 50, maxCoupling: 20, minConditions: 1);
            var g1 = new CodeGraph(tempDir, [new("App.Big", "Big", "App.Big", NodeKind.Type, "App", "App", TypeKind.Class, LineCount: 525)], []);
            var g2 = new CodeGraph(tempDir, [new("App.Big", "Big", "App.Big", NodeKind.Type, "App", "App", TypeKind.Class, LineCount: 526)], []);
            var godDiff = analyzer.DiffSnapshots(
                new ArchitectureSnapshot("s1", tempDir, DateTimeOffset.UtcNow, g1),
                new ArchitectureSnapshot("s2", tempDir, DateTimeOffset.UtcNow, g2),
                [godRule]);
            Assert.Empty(godDiff.NewViolations);
            Assert.Empty(godDiff.ResolvedViolations);

            // 2. DiffSnapshots symmetrically excludes degraded baseline projects when excludeDegradedProjects=true (MCP-01)
            var degBaseGraph = new CodeGraph(tempDir,
                [new("Broken.Bad", "Bad", "Broken.Bad", NodeKind.Type, "Broken", "Broken", TypeKind.Class)],
                [],
                loadStatus: new GraphLoadStatus("roslyn", 2, 1, 1, true, false, DegradedProjects: ["Broken"]));
            var healthyCurrGraph = new CodeGraph(tempDir,
                [new("Broken.Bad", "Bad", "Broken.Bad", NodeKind.Type, "Broken", "Broken", TypeKind.Class)],
                [],
                loadStatus: new GraphLoadStatus("roslyn", 2, 2, 0, false, false));
            var namingRule = new NamingConventionRule("NAME-1", "Naming", "", RuleSeverity.Error, "Broken.*", "I*");
            var degDiff = analyzer.DiffSnapshots(
                new ArchitectureSnapshot("b", tempDir, DateTimeOffset.UtcNow, degBaseGraph),
                new ArchitectureSnapshot("c", tempDir, DateTimeOffset.UtcNow, healthyCurrGraph),
                [namingRule],
                excludeDegradedProjects: true);
            Assert.Empty(degDiff.NewViolations);

            // 3. YAML rule colliding with Roslyn analyzer ID gets separate EvaluatedRuleSummary rows (MCP-05)
            var colGraph = new CodeGraph(tempDir,
                [new("App.Bad", "Bad", "App.Bad", NodeKind.Type, "App", "App", TypeKind.Class)],
                [],
                analyzerDiagnostics:
                [
                    new AnalyzerDiagnostic("CA1822", "Mark static", "Performance", RuleSeverity.Warning, "Mark static", new SourceLocation("Bad.cs", 1), "App"),
                    new AnalyzerDiagnostic("CA1822", "Mark static", "Performance", RuleSeverity.Warning, "Mark static 2", new SourceLocation("Bad.cs", 2), "App")
                ]);
            var colReport = analyzer.CheckRules(colGraph, [new NamingConventionRule("CA1822", "Custom naming", "", RuleSeverity.Error, "App.*", "I*")], includeAnalyzerDiagnostics: true);
            Assert.NotNull(colReport.EvaluatedRules);
            Assert.Equal(2, colReport.EvaluatedRules!.Count);
            Assert.Equal(1, colReport.EvaluatedRules.Single(r => r.Name == "Custom naming").ViolationCount);
            Assert.Equal(2, colReport.EvaluatedRules.Single(r => r.Name.Contains("[analyzer]")).ViolationCount);
            Assert.Equal(colReport.TotalViolations, colReport.EvaluatedRules.Sum(r => r.ViolationCount));

            // 4. Default omitted categories in GetAnalyzerDiagnostics consistently returns default 3 categories (MCP-07)
            var anaDefault = analyzer.GetAnalyzerDiagnostics(colGraph, categories: null, minSeverity: RuleSeverity.Warning);
            var anaExplicit = analyzer.GetAnalyzerDiagnostics(colGraph, categories: ["Design", "Maintainability", "Performance"], minSeverity: RuleSeverity.Warning);
            Assert.Equal(anaExplicit.Categories, anaDefault.Categories);

            // 5. SARIF emits originalUriBaseIds and uriBaseId when rootPath is provided (AL-06)
            var sarifWithRoot = SarifExporter.Export(
                violations: [new RuleViolation("R1", "Rule 1", RuleSeverity.Error, "msg", Location: new SourceLocation("src/Foo.cs", 10, 2))],
                rootPath: tempDir);
            using (var doc = System.Text.Json.JsonDocument.Parse(sarifWithRoot))
            {
                var run = doc.RootElement.GetProperty("runs")[0];
                Assert.True(run.TryGetProperty("originalUriBaseIds", out var baseIds));
                Assert.True(baseIds.TryGetProperty("SRCROOT", out _));
                var artLoc = run.GetProperty("results")[0].GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation");
                Assert.Equal("SRCROOT", artLoc.GetProperty("uriBaseId").GetString());
            }

            // 6. Structurally empty baseline does not produce bogus resolved ARCHLENS-CONFIG-001 (AL-12)
            var emptyBaseSnap = new ArchitectureSnapshot("e1", tempDir, DateTimeOffset.UtcNow, new CodeGraph(tempDir, [], []));
            var nonEmptyCurrSnap = new ArchitectureSnapshot("e2", tempDir, DateTimeOffset.UtcNow, healthyCurrGraph);
            var emptyBaseDiff = analyzer.DiffSnapshots(emptyBaseSnap, nonEmptyCurrSnap, [namingRule], rootNamespace: "Broken");
            Assert.DoesNotContain(emptyBaseDiff.ResolvedViolations, v => v.RuleId == "ARCHLENS-CONFIG-001");

            // 7. End-to-end Roslyn: Singleton->Singleton->Scoped chain (DI-01), independent returned ServiceCollections (DI-02),
            //    explicit & implicit conversions (GRAPH-01), nested closed generic outer type arg (GRAPH-04), Razor @code qualified edges (UI-01)
            var semDir = Path.Combine(tempDir, "C21Sem");
            WriteFile(semDir, "C21Sem.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
                </Project>
                """);
            WriteFile(semDir, "Home.razor", """
                @page "/"
                @inject IRazorSvc Svc
                @code {
                    protected override void OnInitialized()
                    {
                        Svc.Do();
                        RazorHelper.Hit();
                    }
                }
                """);
            WriteFile(semDir, "Program.cs", """
                using C21Sem;

                var services = new ServiceCollection();
                services.AddScoped<IChainScoped, ChainScoped>();
                services.AddSingleton<ChainMiddle>();
                services.AddSingleton<ChainOuter>();

                _ = Containers.BuildAlpha();
                _ = Containers.BuildBeta();
                _ = Converter.Read(Converter.Make(1.5m));
                _ = new OuterHolder();

                namespace C21Sem
                {
                    public interface IServiceCollection { }
                    public sealed class ServiceCollection : IServiceCollection { }
                    public static class DiExt
                    {
                        public static IServiceCollection AddScoped<T1, T2>(this IServiceCollection s) => s;
                        public static IServiceCollection AddSingleton<T1>(this IServiceCollection s) => s;
                    }
                    public interface IChainScoped { }
                    public sealed class ChainScoped : IChainScoped { }
                    public sealed class ChainMiddle(IChainScoped dep) { }
                    public sealed class ChainOuter(ChainMiddle mid) { }

                    public interface ICross { }
                    public sealed class CrossImpl : ICross { }
                    public sealed class CrossConsumer(ICross c) { }
                    public static class Containers
                    {
                        public static IServiceCollection BuildAlpha() { var a = new ServiceCollection(); a.AddSingleton<CrossConsumer>(); return a; }
                        public static IServiceCollection BuildBeta() { var b = new ServiceCollection(); b.AddScoped<ICross, CrossImpl>(); return b; }
                    }

                    public readonly record struct Money(decimal Amount)
                    {
                        public static explicit operator Money(decimal d) => new(d);
                        public static implicit operator decimal(Money m) => m.Amount;
                    }
                    public static class Converter
                    {
                        public static Money Make(decimal d) => (Money)d;
                        public static decimal Read(Money m) => m;
                    }

                    public sealed class Person { }
                    public sealed class Target { }
                    public sealed class Outer2<T> { public sealed class Inner<U> { } }
                    public sealed class OuterHolder { public Outer2<Person>.Inner<Target>? Ref; }

                    public interface IRazorSvc { void Do(); }
                    public static class RazorHelper { public static void Hit() { } public static void Do() { } }
                }
                """);

            using var roslyn = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snap = await roslyn.ExtractAsync(Path.Combine(semDir, "C21Sem.csproj"), new ExtractionOptions(IncludeMethods: true));

            // DI-01 & DI-02
            var di = analyzer.AnalyzeDi(snap.Graph);
            Assert.Contains(di.CaptiveDependencies, c => c.ConsumerTypeId == "C21Sem.ChainOuter" && c.ResolvedImplementationTypeId == "C21Sem.ChainScoped");
            Assert.DoesNotContain(di.CaptiveDependencies, c => c.ConsumerTypeId == "C21Sem.CrossConsumer");

            // GRAPH-01: explicit + implicit conversion call edges
            var expOp = snap.Graph.GetNodes(NodeKind.Method).First(m => m.Name == "op_Explicit" && m.ContainingTypeId == "C21Sem.Money");
            var impOp = snap.Graph.GetNodes(NodeKind.Method).First(m => m.Name == "op_Implicit" && m.ContainingTypeId == "C21Sem.Money");
            Assert.Contains(snap.Graph.Edges, e => e.Kind == DependencyKind.MethodCall && e.ToId == expOp.Id);
            Assert.Contains(snap.Graph.Edges, e => e.Kind == DependencyKind.MethodCall && e.ToId == impOp.Id);

            // GRAPH-04: outer generic argument emits GenericArgument edge
            Assert.Contains(snap.Graph.Edges, e => e.FromId == "C21Sem.OuterHolder" && e.ToId == "C21Sem.Person" && e.Kind == DependencyKind.GenericArgument);

            // UI-01: Razor @code emits real MethodCall edge for RazorHelper.Hit() and does NOT falsely mark RazorHelper.Do() live
            var hitMethod = snap.Graph.GetNodes(NodeKind.Method).First(m => m.FullName == "C21Sem.RazorHelper.Hit()");
            Assert.Contains(snap.Graph.Edges, e => e.Kind == DependencyKind.MethodCall && e.FromId.Contains("Home.OnInitialized") && e.ToId == hitMethod.Id);
            var deadMethods = analyzer.AnalyzeDeadCode(snap.Graph, NodeKind.Method, profile: "app");
            Assert.Contains(deadMethods.Candidates, c => c.FullName == "C21Sem.RazorHelper.Do()");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Cycle22Fixes_WildcardCache_DuplicateProjectNames_AnalyzerCollision_ExpandedCycle_RoslynSemantics_And_IlParity()
    {
        var tempDir = CreateTempDir();
        try
        {
            var analyzer = new ArchitectureAnalyzerService();

            // 1. MCP-01: Colliding analyzer diagnostic ID gets namespaced as analyzer:{id}
            var collideGraph = new CodeGraph(
                rootPath: tempDir,
                nodes: [new CodeNode("C22.Sample", "Sample", "C22.Sample", NodeKind.Type, "C22", "C22", TypeKind.Class)],
                edges: [],
                projects: [new ArchLens.Model.ProjectInfo("C22", "C22.csproj", "net10.0", [], [], 1, 1)],
                analyzerDiagnostics:
                [
                    new AnalyzerDiagnostic("CA1822", "Mark members as static", "Performance", RuleSeverity.Info, "Member can be static", new SourceLocation("Sample.cs", 5, 1), "C22")
                ]);
            var collidingRule = new MaxMetricRule("CA1822", "Custom CA1822 rule", "Custom colliding rule", RuleSeverity.Warning, "lines", 0, scope: NodeKind.Type);
            var collideReport = analyzer.CheckRules(collideGraph, [collidingRule], includeAnalyzerDiagnostics: true);
            Assert.Contains(collideReport.EvaluatedRules!, r => r.Id == "CA1822" && r.Name == "Custom CA1822 rule");
            Assert.Contains(collideReport.EvaluatedRules!, r => r.Id == "analyzer:CA1822");
            Assert.Equal(2, collideReport.EvaluatedRules!.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count());
            Assert.Contains(collideReport.Violations, v => v.RuleId == "analyzer:CA1822");

            // 2. MCP-02: Expanding an SCC (X<->Y -> X<->Y<->Z) must NOT report X<->Y as resolved
            var baseCycleGraph = new CodeGraph(
                rootPath: tempDir,
                nodes:
                [
                    new CodeNode("ns:App.X", "App.X", "App.X", NodeKind.Namespace, "App", "App.X"),
                    new CodeNode("ns:App.Y", "App.Y", "App.Y", NodeKind.Namespace, "App", "App.Y"),
                    new CodeNode("App.X.A", "A", "App.X.A", NodeKind.Type, "App", "App.X", TypeKind.Class),
                    new CodeNode("App.Y.B", "B", "App.Y.B", NodeKind.Type, "App", "App.Y", TypeKind.Class)
                ],
                edges:
                [
                    new DependencyEdge("App.X.A", "App.Y.B", DependencyKind.FieldOrProperty),
                    new DependencyEdge("App.Y.B", "App.X.A", DependencyKind.FieldOrProperty)
                ],
                projects: [new ArchLens.Model.ProjectInfo("App", "App.csproj", "net10.0", [], [], 2, 2)]);
            var grownCycleGraph = new CodeGraph(
                rootPath: tempDir,
                nodes:
                [
                    new CodeNode("ns:App.X", "App.X", "App.X", NodeKind.Namespace, "App", "App.X"),
                    new CodeNode("ns:App.Y", "App.Y", "App.Y", NodeKind.Namespace, "App", "App.Y"),
                    new CodeNode("ns:App.Z", "App.Z", "App.Z", NodeKind.Namespace, "App", "App.Z"),
                    new CodeNode("App.X.A", "A", "App.X.A", NodeKind.Type, "App", "App.X", TypeKind.Class),
                    new CodeNode("App.Y.B", "B", "App.Y.B", NodeKind.Type, "App", "App.Y", TypeKind.Class),
                    new CodeNode("App.Z.C", "C", "App.Z.C", NodeKind.Type, "App", "App.Z", TypeKind.Class)
                ],
                edges:
                [
                    new DependencyEdge("App.X.A", "App.Y.B", DependencyKind.FieldOrProperty),
                    new DependencyEdge("App.Y.B", "App.X.A", DependencyKind.FieldOrProperty),
                    new DependencyEdge("App.X.A", "App.Z.C", DependencyKind.FieldOrProperty),
                    new DependencyEdge("App.Z.C", "App.X.A", DependencyKind.FieldOrProperty)
                ],
                projects: [new ArchLens.Model.ProjectInfo("App", "App.csproj", "net10.0", [], [], 3, 3)]);
            var diffReport = analyzer.DiffSnapshots(
                new ArchitectureSnapshot("b1", tempDir, DateTimeOffset.UtcNow.AddMinutes(-1), baseCycleGraph),
                new ArchitectureSnapshot("c1", tempDir, DateTimeOffset.UtcNow, grownCycleGraph));
            Assert.Empty(diffReport.ResolvedCycles);
            Assert.Single(diffReport.AddedCycles);

            // 3. AL-01: External wildcard <Compile Include="../ext/*.cs" /> invalidates warm cache when dir is created later
            var wildRoot = Path.Combine(tempDir, "WildRepo");
            var wildApp = Path.Combine(wildRoot, "App");
            WriteFile(wildApp, "App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Library</OutputType></PropertyGroup>
                  <ItemGroup><Compile Include="..\ext\*.cs" /></ItemGroup>
                </Project>
                """);
            WriteFile(wildApp, "Base.cs", "namespace WildApp; public sealed class BaseType { }");
            var wildCacheDb = Path.Combine(tempDir, "wild-cache.db");
            using (var cache = new SqliteGraphCache(wildCacheDb))
            using (var roslynWild = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false))
            using (var mgr = new WorkspaceSessionManager([roslynWild], cache))
            {
                var snap1 = await mgr.GetOrLoadAsync(Path.Combine(wildApp, "App.csproj"));
                Assert.DoesNotContain(snap1.Graph.GetNodes(NodeKind.Type), n => n.FullName == "WildApp.AddedExternal");

                WriteFile(Path.Combine(wildRoot, "ext"), "Added.cs", "namespace WildApp; public sealed class AddedExternal { }");
                var snap2 = await mgr.GetOrLoadAsync(Path.Combine(wildApp, "App.csproj"));
                Assert.Contains(snap2.Graph.GetNodes(NodeKind.Type), n => n.FullName == "WildApp.AddedExternal");
            }

            // 4. AL-02: Duplicate .csproj file stems with distinct AssemblyName load cleanly without colliding
            var dupRoot = Path.Combine(tempDir, "DupProj");
            WriteFile(dupRoot, "Dup.slnx", """
                <Solution>
                  <Project Path="One/Shared.csproj" />
                  <Project Path="Two/Shared.csproj" />
                </Solution>
                """);
            WriteFile(dupRoot, "One/Shared.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>One.Shared</AssemblyName></PropertyGroup>
                </Project>
                """);
            WriteFile(dupRoot, "One/A.cs", "namespace OneNs; public sealed class A { }");
            WriteFile(dupRoot, "Two/Shared.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>Two.Shared</AssemblyName></PropertyGroup>
                  <ItemGroup><ProjectReference Include="..\One\Shared.csproj" /></ItemGroup>
                </Project>
                """);
            WriteFile(dupRoot, "Two/B.cs", "namespace TwoNs; public sealed class B { public OneNs.A? Ref { get; set; } }");

            using var roslyn = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var dupSnap = await roslyn.ExtractAsync(Path.Combine(dupRoot, "Dup.slnx"));
            Assert.Equal(2, dupSnap.Graph.Projects.Count);
            Assert.Contains(dupSnap.Graph.Projects, p => p.Name == "One.Shared");
            Assert.Contains(dupSnap.Graph.Projects, p => p.Name == "Two.Shared");

            // 5. Roslyn semantics: GRAPH-01 (explicit iface impls), GRAPH-02 (1x attr weights), GRAPH-03 (outer generic edge),
            //    GRAPH-05 (C# 14 extension member ID), DI-01 (AddHttpClient TryAdd dedup), DEAD-01 (Razor @code property/nested calls),
            //    DEAD-03 (IMessageHandler entry point)
            var semDir = Path.Combine(tempDir, "C22Sem");
            WriteFile(semDir, "C22Sem.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);
            WriteFile(semDir, "Comp.razor", """
                @page "/c22"
                <span>@Message</span>
                <span>@Helper.Describe()</span>
                @code {
                    public string Message => new FirstService().Do();
                    private static class Helper
                    {
                        public static string Describe() => new SecondService().Do();
                    }
                }
                """);
            WriteFile(semDir, "Program.cs", """
                using System;
                using C22Sem;
                using Microsoft.Extensions.DependencyInjection;

                var services = new ServiceCollection();
                services.AddHttpClient<FirstService>();
                services.AddHttpClient<SecondService>();
                _ = new NestedUser();
                _ = "hi".Whisper();
                _ = new Multi();

                namespace Microsoft.Extensions.DependencyInjection
                {
                    public interface IServiceCollection { }
                    public sealed class ServiceCollection : IServiceCollection { }
                    public static class HttpClientFactoryServiceCollectionExtensions
                    {
                        public static IServiceCollection AddHttpClient<TClient>(this IServiceCollection s) where TClient : class => s;
                    }
                }

                namespace C22Sem
                {
                    public interface IA { void M(); }
                    public interface IB { void M(); }
                    public sealed class Multi : IA, IB
                    {
                        void IA.M() { }
                        void IB.M() { }
                    }

                    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
                    public sealed class TagAttribute : Attribute { }

                    [Tag]
                    [Tag]
                    public sealed class AttrTarget
                    {
                        [Tag]
                        public void Run([Tag] int x) { }
                    }

                    public sealed class OuterArg { }
                    public sealed class InnerArg { }
                    public sealed class OuterBox<T>
                    {
                        public sealed class InnerBox<U> { }
                    }
                    public sealed class NestedUser
                    {
                        public OuterBox<OuterArg>.InnerBox<InnerArg>? Field;
                    }

                    public static class StringExt
                    {
                        extension(string text)
                        {
                            public string Whisper() => text.ToLowerInvariant();
                        }
                    }

                    public sealed class FirstService { public string Do() => "a"; }
                    public sealed class SecondService { public string Do() => "b"; }

                    public interface IMessageHandler<T> { void Handle(T msg); }
                    public sealed class OrderMsg { }
                    public sealed class OrderMsgHandler : IMessageHandler<OrderMsg>
                    {
                        public void Handle(OrderMsg msg) { }
                    }
                }
                """);

            var semSnap = await roslyn.ExtractAsync(Path.Combine(semDir, "C22Sem.csproj"), new ExtractionOptions(IncludeMethods: true));

            // GRAPH-01: both IA.M and IB.M exist as distinct Method nodes
            var multiMethods = semSnap.Graph.GetNodes(NodeKind.Method)
                .Where(m => m.ContainingTypeId == "C22Sem.Multi")
                .ToList();
            Assert.Equal(2, multiMethods.Count);
            Assert.Contains(multiMethods, m => m.Id.Contains("IA.M()", StringComparison.Ordinal));
            Assert.Contains(multiMethods, m => m.Id.Contains("IB.M()", StringComparison.Ordinal));

            // GRAPH-02: exact 1x attribute weight (2 on class + 1 on method + 1 on param = 4, NOT 8!)
            var attrEdge = Assert.Single(semSnap.Graph.Edges,
                e => e.FromId == "C22Sem.AttrTarget" && e.ToId == "C22Sem.TagAttribute" && e.Kind == DependencyKind.Attribute);
            Assert.Equal(4, attrEdge.Weight);

            // GRAPH-03: OuterBox<T> receives TypeReference edge and is NOT reported dead
            Assert.Contains(semSnap.Graph.Edges,
                e => e.FromId == "C22Sem.NestedUser" && e.ToId == "C22Sem.OuterBox<T>" && e.Kind == DependencyKind.TypeReference);
            var deadTypes = analyzer.AnalyzeDeadCode(semSnap.Graph, NodeKind.Type, profile: "app");
            Assert.DoesNotContain(deadTypes.Candidates, c => c.FullName == "C22Sem.OuterBox<T>");

            // DEAD-03: IMessageHandler implementation is recognized as entry point
            Assert.DoesNotContain(deadTypes.Candidates, c => c.FullName == "C22Sem.OrderMsgHandler");

            // GRAPH-05: C# 14 extension member method ID includes receiver parameter type
            Assert.Contains(semSnap.Graph.GetNodes(NodeKind.Method),
                m => m.FullName == "C22Sem.StringExt.Whisper(string)");

            // DI-01: AddHttpClient emits deduplicated single IHttpClientFactory (TryAdd)
            var di = analyzer.AnalyzeDi(semSnap.Graph);
            Assert.Single(di.Registrations, r => r.ServiceTypeId == "System.Net.Http.IHttpClientFactory");

            // DEAD-01: Razor @code property & nested class calls keep FirstService.Do() and SecondService.Do() alive
            var deadMethods = analyzer.AnalyzeDeadCode(semSnap.Graph, NodeKind.Method, profile: "app");
            Assert.DoesNotContain(deadMethods.Candidates, c => c.FullName == "C22Sem.FirstService.Do()");
            Assert.DoesNotContain(deadMethods.Candidates, c => c.FullName == "C22Sem.SecondService.Do()");

            // 6. IL-01 (record struct -> Record) & IL-03 (generic parameter names instead of M0/T0) + DEAD-02 warning note
            var ilSrc = """
                namespace IlCheck;
                public readonly record struct Point(int X, int Y);
                public sealed class GenericBox<TItem>
                {
                    public TItem Echo(TItem value) => value;
                    public TResult Map<TResult>(TResult input) => input;
                }
                """;
            var syntaxTree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(ilSrc);
            var refs = new[]
            {
                Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(System.Reflection.Assembly.Load("System.Runtime").Location)
            };
            var ilComp = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "IlCheck",
                [syntaxTree],
                refs,
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));
            var ilDll = Path.Combine(tempDir, "IlCheck.dll");
            var emitRes = ilComp.Emit(ilDll);
            Assert.True(emitRes.Success, string.Join("; ", emitRes.Diagnostics));

            var ilExtractor = new MetadataCodeGraphExtractor();
            var ilSnap = await ilExtractor.ExtractAsync(ilDll, new ExtractionOptions(IncludeMethods: true));
            var pointNode = ilSnap.Graph.FindNode("IlCheck.Point");
            Assert.NotNull(pointNode);
            Assert.Equal(TypeKind.Record, pointNode!.TypeKind);
            Assert.Contains(ilSnap.Graph.GetNodes(NodeKind.Method), m => m.FullName == "IlCheck.GenericBox<TItem>.Echo(TItem)");
            Assert.Contains(ilSnap.Graph.GetNodes(NodeKind.Method), m => m.FullName == "IlCheck.GenericBox<TItem>.Map<TResult>(TResult)");

            var ilDeadMethodReport = analyzer.AnalyzeDeadCode(ilSnap.Graph, NodeKind.Method, profile: "app");
            Assert.Contains("IL backend", ilDeadMethodReport.Note ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Feedback_DiffReevaluatesSessionBaselineWhenOnlyTheCyclePolicyChanges()
    {
        var tempDir = CreateTempDir();
        try
        {
            const string configPath = ".archlens.yaml";
            var nodes = new CodeNode[]
            {
                new("App.Parent", "Parent", "App.Parent", NodeKind.Type, "App", "App"),
                new("App.Child.Component", "Component", "App.Child.Component", NodeKind.Type, "App", "App.Child")
            };
            var graph = new CodeGraph(tempDir, nodes,
                [
                    new(nodes[0].Id, nodes[1].Id, DependencyKind.MethodCall),
                    new(nodes[1].Id, nodes[0].Id, DependencyKind.MethodCall)
                ]);
            var policy = """
                rules:
                  - id: CYC
                    type: no_cycles
                    scope: namespace
                    severity: warning
                    include_parent_child: true
                """;
            WriteFile(tempDir, configPath, policy);
            using var sessions = new WorkspaceSessionManager([new StubGraphExtractor(graph)]);
            var tools = new ArchLensTools(sessions, new ArchitectureAnalyzerService(), new GraphSqlQueryEngine(), [new YamlRuleSetProvider()]);

            var check = await tools.CheckRulesAsync(tempDir);
            Assert.NotEqual(true, check.IsError);
            using var checkJson = JsonDocument.Parse(((ModelContextProtocol.Protocol.TextContentBlock)check.Content[0]).Text);
            Assert.Equal(1, checkJson.RootElement.GetProperty("totalViolations").GetInt32());

            WriteFile(tempDir, configPath, policy.Replace("scope: namespace", "scope: project", StringComparison.Ordinal));
            var recheck = await tools.CheckRulesAsync(tempDir);
            Assert.NotEqual(true, recheck.IsError);
            var result = await tools.DiffAgainstBaselineAsync(tempDir, summaryOnly: true);
            Assert.NotEqual(true, result.IsError);
            using var json = JsonDocument.Parse(((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text);
            Assert.True(json.RootElement.GetProperty("baselineRulesDiffer").GetBoolean());
            Assert.Contains("evaluated with the current rules", json.RootElement.GetProperty("note").GetString());
            var diff = json.RootElement.GetProperty("diff");
            Assert.Equal(0, diff.GetProperty("totalAddedCycles").GetInt32());
            Assert.Equal(0, diff.GetProperty("totalResolvedCycles").GetInt32());
            Assert.Equal(0, diff.GetProperty("totalResolvedViolations").GetInt32());
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public void Feedback_DiffReturnsCycleDetailsForTheConfiguredScope()
    {
        var nodes = new CodeNode[]
        {
            new("App.First.Node", "Node", "App.First.Node", NodeKind.Type, "First", "App.First"),
            new("App.Second.Node", "Node", "App.Second.Node", NodeKind.Type, "Second", "App.Second")
        };
        var graph = new CodeGraph("C:/repo", nodes,
            [
                new(nodes[0].Id, nodes[1].Id, DependencyKind.TypeReference),
                new(nodes[1].Id, nodes[0].Id, DependencyKind.TypeReference)
            ]);
        var baseline = new ArchitectureSnapshot("base", graph.RootPath, DateTimeOffset.UtcNow, new CodeGraph(graph.RootPath, nodes, []));
        var current = baseline with { SnapshotId = "current", Graph = graph };
        var rules = new[] { new NoCyclesRule("CYC", "Project cycles", "", RuleSeverity.Warning, NodeKind.Project) };

        var diff = new ArchitectureAnalyzerService().DiffSnapshots(baseline, current, rules);

        Assert.Equal(NodeKind.Project, Assert.Single(diff.AddedCycles).Scope);
        Assert.Equal(RuleSeverity.Warning, Assert.Single(diff.NewViolations).Severity);
    }

    [Fact]
    public void Feedback_MetricAcknowledgementsOnlyAcceptDebtUpToTheConfiguredLimits()
    {
        var rules = new YamlRuleSetProvider().ParseYaml("""
            rules:
              - id: LOC
                type: max_metric
                severity: warning
                metric: line_count
                threshold: 10
                acknowledge:
                  - symbol: App.Legacy*
                    reason: Migration in progress
                    max_value: 20
              - id: SIZE
                type: god_class
                severity: warning
                max_lines: 10
                min_conditions: 1
                acknowledge:
                  - symbol: App.Legacy*
                    reason: Reviewed in the debt register
                    max_lines: 20
            """);
        var graph = new CodeGraph("C:/repo",
            [
                new("App.Legacy", "Legacy", "App.Legacy", NodeKind.Type, "App", "App", LineCount: 20),
                new("App.LegacyGrew", "LegacyGrew", "App.LegacyGrew", NodeKind.Type, "App", "App", LineCount: 21),
                new("App.New", "New", "App.New", NodeKind.Type, "App", "App", LineCount: 20)
            ], []);

        var report = new ArchitectureAnalyzerService().CheckRules(graph, rules);

        Assert.Equal(4, report.WarningCount);
        Assert.Equal(2, report.AcknowledgedViolationCount);
        Assert.All(report.AcknowledgedViolations!, ack => Assert.Equal("App.Legacy", ack.Symbol));
        Assert.Contains(report.AcknowledgedViolations!, ack => ack.Reason == "Migration in progress");
        Assert.All(report.Violations, v => Assert.NotEqual("App.Legacy", v.NodeId));
    }

    [Fact]
    public void Feedback_ImpactCountsEachContainingTypeOnceAtTheNearestDistance()
    {
        var target = new CodeNode("A", "A", "A", NodeKind.Type, "App");
        var graph = new CodeGraph("C:/repo",
            [
                target,
                new("A.Request", "Request", "A.Request", NodeKind.Type, "App", ContainingTypeId: "A"),
                new("Agent", "Agent", "Agent", NodeKind.Type, "App"),
                new("Agent.Event", "Event", "Agent.Event", NodeKind.Type, "App", ContainingTypeId: "Agent"),
                new("Agent.Event.Payload", "Payload", "Agent.Event.Payload", NodeKind.Type, "App", ContainingTypeId: "Agent.Event"),
                new("Caller", "Caller", "Caller", NodeKind.Type, "App")
            ],
            [
                new("A.Request", "A", DependencyKind.TypeReference),
                new("Agent.Event.Payload", "A.Request", DependencyKind.MethodCall),
                new("Agent.Event", "Agent", DependencyKind.TypeReference),
                new("Caller", "Agent", DependencyKind.MethodCall)
            ]);

        var impact = ImpactAnalyzer.Analyze(graph, target, maxDepth: 2);

        Assert.Equal(1, impact.DirectDependentsCount);
        Assert.Equal(2, impact.TotalTransitiveDependentsCount);
        Assert.Collection(impact.ImpactedNodes,
            node => { Assert.Equal("Agent", node.NodeId); Assert.Equal(1, node.Distance); },
            node => { Assert.Equal("Caller", node.NodeId); Assert.Equal(2, node.Distance); });
        Assert.Equal("Agent.Event.Payload", Assert.Single(ImpactAnalyzer.Analyze(graph, graph.FindNode("A.Request")!).ImpactedNodes).NodeId);
    }

    private sealed class ThrowingTestRule : IArchitectureRule
    {
        public string Id => "THROW-1";
        public string Name => "Throwing test rule";
        public string Description => "Always throws";
        public RuleSeverity Severity => RuleSeverity.Error;
        public IEnumerable<RuleViolation> Evaluate(CodeGraph graph, IReadOnlyDictionary<string, NodeMetrics> metrics) =>
            throw new InvalidOperationException("boom");
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
