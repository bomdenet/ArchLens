using System.Globalization;
using System.Text.Json;
using ArchLens.Analysis;
using ArchLens.Extraction.Metadata;
using ArchLens.Extraction.Roslyn;
using ArchLens.Mcp;
using ArchLens.Model;
using ArchLens.Query;
using ArchLens.Rules.Yaml;
using ModelContextProtocol.Protocol;

namespace ArchLens.Tests;

public sealed class DefectFixRegressionTests
{
    [Fact]
    public async Task Def01_StaticMemberConstAndUsingStaticAccessesEmitTypeEdgesAndPreventFalseDeadCode()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "Def01.slnx", """
                <Solution>
                  <Project Path="src/Core/Core.csproj" />
                  <Project Path="src/App/App.csproj" />
                </Solution>
                """);

            WriteFile(tempDir, "src/Core/Core.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            WriteFile(tempDir, "src/Core/Types.cs", """
                using static Core.StaticImportedConstants;

                namespace Core;

                internal sealed class Registry
                {
                    public static Registry Instance { get; } = new();
                }

                internal static class TaxRate
                {
                    public const decimal Value = 1.2m;
                }

                internal static class StaticImportedConstants
                {
                    public const int Magic = 7;
                }

                internal sealed class GenericHolder<T>
                {
                    public static string Tag => typeof(T).Name;
                }

                internal sealed class TagPayload { }

                internal sealed class TrulyDeadOrphan { }

                public sealed class Consumer
                {
                    public string DirectStaticMember() => Registry.Instance.GetType().Name;
                    public decimal ConstMember(decimal price) => price * TaxRate.Value + Magic;
                    public string GenericStatic() => Core.GenericHolder<TagPayload>.Tag;
                }
                """);

            WriteFile(tempDir, "src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../Core/Core.csproj" />
                  </ItemGroup>
                </Project>
                """);

            WriteFile(tempDir, "src/App/Program.cs", """
                var c = new Core.Consumer();
                Console.WriteLine(c.DirectStaticMember() + c.ConstMember(10m) + c.GenericStatic());
                """);

            using var extractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snapshot = await extractor.ExtractAsync(Path.Combine(tempDir, "Def01.slnx"));
            var graph = snapshot.Graph;

            Assert.Contains(graph.Edges, e => e.FromId == "Core.Consumer" && e.ToId == "Core.Registry");
            Assert.Contains(graph.Edges, e => e.FromId == "Core.Consumer" && e.ToId == "Core.TaxRate");
            Assert.Contains(graph.Edges, e => e.FromId == "Core.Consumer" && e.ToId == "Core.StaticImportedConstants");
            Assert.Contains(graph.Edges, e => e.FromId == "Core.Consumer" && e.ToId == "Core.GenericHolder<T>");
            Assert.Contains(graph.Edges, e => e.FromId == "Core.Consumer" && e.ToId == "Core.TagPayload");

            var deadReport = new ArchitectureAnalyzerService().AnalyzeDeadCode(graph, NodeKind.Type, treatPublicApiAsEntryPoint: false);
            Assert.DoesNotContain(deadReport.Candidates, c => c.NodeId == "Core.Registry");
            Assert.DoesNotContain(deadReport.Candidates, c => c.NodeId == "Core.TaxRate");
            Assert.DoesNotContain(deadReport.Candidates, c => c.NodeId == "Core.StaticImportedConstants");
            Assert.DoesNotContain(deadReport.Candidates, c => c.NodeId == "Core.TagPayload");
            Assert.Contains(deadReport.Candidates, c => c.NodeId == "Core.TrulyDeadOrphan");

            // DEF-12: Synthesized top-level Program belongs to App namespace (not "(global)") and is tagged synthetic
            var programNode = graph.FindNode("App.Program");
            Assert.NotNull(programNode);
            Assert.Equal("App", programNode.Namespace);
            Assert.Equal("true", programNode.Metadata?["isSynthetic"]);
            Assert.Equal("compilationUnit", programNode.Metadata?["syntheticKind"]);
            Assert.DoesNotContain(graph.GetNodes(NodeKind.Namespace), n => n.FullName == "(global)");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Def02_XamlStartupUriAndClrNamespaceReferencesExemptWpfWindowsAndControlsFromDeadCode()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "WpfLike.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>WinExe</OutputType>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            WriteFile(tempDir, "App.xaml", """
                <Application x:Class="WpfLike.App"
                             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             StartupUri="Views/MainWindow.xaml">
                </Application>
                """);

            WriteFile(tempDir, "Views/MainWindow.xaml", """
                <Window x:Class="WpfLike.Views.MainWindow"
                        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                        xmlns:controls="clr-namespace:WpfLike.Controls">
                  <controls:StatusBadge />
                </Window>
                """);

            WriteFile(tempDir, "Code.cs", """
                namespace System.Windows
                {
                    public class Application { }
                    public class Window { }
                }

                namespace WpfLike
                {
                    public partial class App : System.Windows.Application
                    {
                        public static void Main() { }
                    }
                }

                namespace WpfLike.Views
                {
                    internal sealed partial class MainWindow : System.Windows.Window { }
                    internal sealed class UnusedDialog : System.Windows.Window { }
                }

                namespace WpfLike.Controls
                {
                    internal sealed class StatusBadge { }
                }
                """);

            using var extractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snapshot = await extractor.ExtractAsync(Path.Combine(tempDir, "WpfLike.csproj"));

            var deadReport = new ArchitectureAnalyzerService().AnalyzeDeadCode(snapshot.Graph, NodeKind.Type, treatPublicApiAsEntryPoint: false);
            Assert.Contains("UiFrameworks", deadReport.ActiveDetectors);
            Assert.DoesNotContain(deadReport.Candidates, c => c.NodeId == "WpfLike.App");
            Assert.DoesNotContain(deadReport.Candidates, c => c.NodeId == "WpfLike.Views.MainWindow");
            Assert.DoesNotContain(deadReport.Candidates, c => c.NodeId == "WpfLike.Controls.StatusBadge");
            Assert.Contains(deadReport.Candidates, c => c.NodeId == "WpfLike.Views.UnusedDialog");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Def03_DeadCodeReportsWhenTestProjectsWereExcluded()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "WithTests.slnx", """
                <Solution>
                  <Project Path="src/Lib/Lib.csproj" />
                  <Project Path="tests/Lib.Tests/Lib.Tests.csproj" />
                </Solution>
                """);

            WriteFile(tempDir, "src/Lib/Lib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                </Project>
                """);
            WriteFile(tempDir, "src/Lib/Helper.cs", "namespace Lib; internal sealed class TestOnlyHelper { }");

            WriteFile(tempDir, "tests/Lib.Tests/Lib.Tests.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                  </PropertyGroup>
                  <ItemGroup>
                    <ProjectReference Include="../../src/Lib/Lib.csproj" />
                  </ItemGroup>
                </Project>
                """);
            WriteFile(tempDir, "tests/Lib.Tests/HelperTests.cs", """
                namespace Lib.Tests;
                public class HelperTests { public void Run() { } }
                """);

            using var extractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var excludedSnap = await extractor.ExtractAsync(Path.Combine(tempDir, "WithTests.slnx"), new ExtractionOptions(IncludeTestProjects: false));
            Assert.Equal(1, excludedSnap.Graph.ExcludedTestProjectCount);

            var deadExcluded = new ArchitectureAnalyzerService().AnalyzeDeadCode(excludedSnap.Graph, NodeKind.Type, treatPublicApiAsEntryPoint: true, testsExcluded: true);
            Assert.True(deadExcluded.ReferencesFromTestsExcluded);
            Assert.Equal(1, deadExcluded.ExcludedTestProjectCount);
            Assert.Contains("test project", deadExcluded.Note ?? "", StringComparison.OrdinalIgnoreCase);

            var includedSnap = await extractor.ExtractAsync(Path.Combine(tempDir, "WithTests.slnx"), new ExtractionOptions(IncludeTestProjects: true));
            Assert.Equal(0, includedSnap.Graph.ExcludedTestProjectCount);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Def09_AutoPropertiesDoNotInflateTypeCyclomaticComplexity_AndMaxMetricSupportsMethodScope()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "DtoCc.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            WriteFile(tempDir, "Models.cs", """
                namespace DtoCc;

                public sealed class PureDto
                {
                    public int Id { get; set; }
                    public string Name { get; set; } = "";
                    public string Email { get; init; } = "";
                    public decimal Amount { get; set; }
                    public bool Active { get; set; }
                    public DateTime CreatedAt { get; set; }
                    public string? Notes { get; set; }
                    public int Version { get; set; }
                }

                public sealed class BranchyWorker
                {
                    public int Simple() => 1;
                    public int Complex(int x)
                    {
                        if (x == 1) return 1;
                        if (x == 2) return 2;
                        if (x == 3) return 3;
                        if (x == 4) return 4;
                        return 0;
                    }
                }
                """);

            using var extractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snapshot = await extractor.ExtractAsync(Path.Combine(tempDir, "DtoCc.csproj"), new ExtractionOptions(IncludeMethods: true));

            var dto = snapshot.Graph.FindNode("DtoCc.PureDto");
            Assert.NotNull(dto);
            Assert.Equal(0, dto.CyclomaticComplexity);

            var methodRule = new MaxMetricRule("CC-M", "Method CC", "", RuleSeverity.Warning, "cyclomatic_complexity", 3, "DtoCc.*", NodeKind.Method);
            var report = new ArchitectureAnalyzerService().CheckRules(snapshot.Graph, [methodRule]);
            Assert.Single(report.Violations);
            Assert.Contains("Complex", report.Violations[0].NodeId);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Def11_DiFactoryDelegateRegistrationsPreserveConstructedGenericType()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "DiFactory.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            WriteFile(tempDir, "Program.cs", """
                using Microsoft.Extensions.DependencyInjection;

                namespace Microsoft.Extensions.DependencyInjection
                {
                    public interface IServiceCollection { }
                    public static class Ext
                    {
                        public static IServiceCollection AddSingleton<TService>(this IServiceCollection s, Func<IServiceProvider, TService> f) => s;
                    }
                }

                namespace DiFactory
                {
                    public interface ISubagentRuntime { }
                    public sealed class RuntimeImpl : ISubagentRuntime { }

                    public static class Setup
                    {
                        public static void Main()
                        {
                            IServiceCollection services = null!;
                            services.AddSingleton<Func<ISubagentRuntime>>(sp => () => new RuntimeImpl());
                        }
                    }
                }
                """);

            using var extractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snapshot = await extractor.ExtractAsync(Path.Combine(tempDir, "DiFactory.csproj"));
            var di = new ArchitectureAnalyzerService().AnalyzeDi(snapshot.Graph);

            Assert.Single(di.Registrations);
            Assert.Equal("System.Func<DiFactory.ISubagentRuntime>", di.Registrations[0].ServiceTypeId);
            Assert.Equal("System.Func<DiFactory.ISubagentRuntime>", di.Registrations[0].ImplementationTypeId);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Def13_AnalyzerDiagnosticsAlwaysUseInvariantCulture()
    {
        var tempDir = CreateTempDir();
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("ru-RU");

            WriteFile(tempDir, "LocDemo.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <AnalysisLevel>latest-all</AnalysisLevel>
                    <EnableNETAnalyzers>true</EnableNETAnalyzers>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            WriteFile(tempDir, "Sample.cs", """
                namespace LocDemo;
                public class Sample
                {
                    public int Compute(int x) => x + 1;
                }
                """);

            using var extractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var snapshot = await extractor.ExtractAsync(
                Path.Combine(tempDir, "LocDemo.csproj"),
                new ExtractionOptions(IncludeAnalyzerDiagnostics: true, AnalyzerCategories: ["Performance"]));

            var ca1822 = snapshot.Graph.AnalyzerDiagnostics.FirstOrDefault(d => d.Id == "CA1822");
            Assert.NotNull(ca1822);
            // Must be invariant English, not Russian ("Пометьте члены как статические")
            Assert.Contains("static", ca1822.Title, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("статич", ca1822.Title, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalUiCulture;
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task Def04_05_06_07_08_10_14_McpToolsReturnStructuredErrorsCompactPayloadsAndHonestMetadata()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "readme.md", "# not a solution");
            var emptyDir = Path.Combine(tempDir, "empty");
            Directory.CreateDirectory(emptyDir);

            using var roslyn = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var il = new MetadataCodeGraphExtractor();
            using var sessions = new WorkspaceSessionManager([roslyn, il]);
            var analyzer = new ArchitectureAnalyzerService();
            var tools = new ArchLensTools(sessions, analyzer, new GraphSqlQueryEngine(), [new YamlRuleSetProvider()]);

            // DEF-06: Structured MCP errors with IsError = true and JSON error/hint
            var missingRes = await tools.GetOverviewAsync(Path.Combine(tempDir, "DoesNotExist"));
            Assert.True(missingRes.IsError);
            using (var doc = ParseResultJson(missingRes))
            {
                Assert.Equal("path_not_found", doc.RootElement.GetProperty("error").GetString());
                Assert.True(doc.RootElement.TryGetProperty("hint", out _));
            }

            var emptyRes = await tools.GetOverviewAsync(emptyDir);
            Assert.True(emptyRes.IsError);
            using (var doc = ParseResultJson(emptyRes))
            {
                Assert.Equal("path_not_found", doc.RootElement.GetProperty("error").GetString());
            }

            var mdRes = await tools.GetOverviewAsync(Path.Combine(tempDir, "readme.md"));
            Assert.True(mdRes.IsError);
            using (var doc = ParseResultJson(mdRes))
            {
                Assert.Equal("unsupported_target", doc.RootElement.GetProperty("error").GetString());
            }

            // Test real ArchLens repo with IL backend (fast!) for DEF-05, DEF-08, DEF-10, DEF-14, and token controls
            var repoRoot = FindRepoRoot();
            var slnx = Path.Combine(repoRoot, "ArchLens.slnx");

            // DEF-08 + DEF-10 + DEF-14: overview
            var overviewRes = await tools.GetOverviewAsync(slnx, topNamespacesLimit: 2, backend: "il");
            Assert.NotEqual(true, overviewRes.IsError);
            using (var doc = ParseResultJson(overviewRes))
            {
                var root = doc.RootElement;
                Assert.False(root.TryGetProperty("methodCount", out _)); // DEF-14: null/omitted when includeMethods=false
                Assert.False(root.GetProperty("methodsIncluded").GetBoolean());
                Assert.True(root.GetProperty("topNamespacesTruncated").GetBoolean()); // DEF-08
                Assert.Equal(2, root.GetProperty("returnedNamespaceCount").GetInt32());
                var ls = root.GetProperty("loadStatus");
                Assert.Equal("il", ls.GetProperty("backend").GetString());
                Assert.True(ls.GetProperty("approximate").GetBoolean()); // DEF-10
                Assert.True(ls.TryGetProperty("note", out _));
            }

            // DEF-05: dependency_graph maxNodes=2 must ONLY emit edges between the 2 returned nodes, compact format uses index pairs
            var graphRes = await tools.GetDependencyGraphAsync(slnx, scope: "namespace", maxNodes: 2, format: "compact", backend: "il");
            Assert.NotEqual(true, graphRes.IsError);
            using (var doc = ParseResultJson(graphRes))
            {
                var root = doc.RootElement;
                Assert.Equal("compact", root.GetProperty("format").GetString());
                Assert.Equal("nodeIndexPairs", root.GetProperty("edgeFormat").GetString());
                Assert.Equal(2, root.GetProperty("nodes").GetArrayLength());
                foreach (var edge in root.GetProperty("edges").EnumerateArray())
                {
                    Assert.Equal(2, edge.GetArrayLength());
                    Assert.InRange(edge[0].GetInt32(), 0, 1);
                    Assert.InRange(edge[1].GetInt32(), 0, 1);
                }
            }

            // Section 3.3: hotspots compact projection + fields + summaryOnly
            var hotspotsRes = await tools.GetHotspotsAsync(slnx, sortBy: "complexity", topN: 3, backend: "il");
            using (var doc = ParseResultJson(hotspotsRes))
            {
                var first = doc.RootElement.GetProperty("items")[0];
                Assert.True(first.TryGetProperty("name", out _));
                Assert.False(first.TryGetProperty("nodeId", out _)); // no duplicate nodeId == name
                Assert.True(first.TryGetProperty("maxMethodComplexity", out _));
                Assert.False(first.TryGetProperty("afferentCoupling", out _)); // compact default omits unrelated metrics
            }

            // Section 3.3: inspect_symbol deduplicates node/metrics
            var inspectRes = await tools.InspectSymbolAsync(slnx, "MetricsEngine", backend: "il");
            using (var doc = ParseResultJson(inspectRes))
            {
                var root = doc.RootElement;
                Assert.False(root.TryGetProperty("node", out _)); // flattened, no node/metrics duplication
                Assert.Equal("ArchLens.Analysis.MetricsEngine", root.GetProperty("id").GetString());
                Assert.True(root.TryGetProperty("metrics", out _));
            }
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public void Def04_DiAnalysisFormatterOmitsRegistrationsByDefaultAndSupportsPagination()
    {
        var regs = Enumerable.Range(1, 10)
            .Select(i => new DiRegistration($"App.ISvc{i}", $"App.Svc{i}", ServiceLifetimeKind.Singleton, "App.Startup", ProjectName: "App"))
            .ToList();
        var singles = Enumerable.Range(1, 5)
            .Select(i => new SingleImplementationInterfaceInfo($"App.ISvc{i}", $"App.Svc{i}", true, i))
            .ToList();
        var report = new DiAnalysisReport(regs.Count, 0, singles.Count, regs, [], singles);

        // Default: includeRegistrations = false
        var defaultObj = McpResponseFormatter.FormatDiAnalysis(report, null, includeRegistrations: false, summaryOnly: false, offset: 0, limit: 3);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(defaultObj, McpResponseFormatter.JsonOptions)))
        {
            var root = doc.RootElement;
            Assert.Equal(10, root.GetProperty("registrationCount").GetInt32());
            Assert.False(root.GetProperty("registrationsIncluded").GetBoolean());
            Assert.False(root.TryGetProperty("registrations", out _));
            Assert.Equal(3, root.GetProperty("singleImplementationInterfaces").GetArrayLength());
            Assert.True(root.GetProperty("singleImplementationsTruncated").GetBoolean());
            Assert.True(root.GetProperty("truncated").GetBoolean());
        }

        // Explicit: includeRegistrations = true, paginated
        var pagedObj = McpResponseFormatter.FormatDiAnalysis(report, null, includeRegistrations: true, summaryOnly: false, offset: 2, limit: 4);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(pagedObj, McpResponseFormatter.JsonOptions)))
        {
            var root = doc.RootElement;
            Assert.True(root.GetProperty("registrationsIncluded").GetBoolean());
            Assert.Equal(4, root.GetProperty("registrations").GetArrayLength());
            Assert.Equal("App.ISvc3", root.GetProperty("registrations")[0].GetProperty("serviceTypeId").GetString());
            Assert.True(root.GetProperty("registrationsTruncated").GetBoolean());
        }

        // SummaryOnly
        var summaryObj = McpResponseFormatter.FormatDiAnalysis(report, null, includeRegistrations: true, summaryOnly: true, offset: 0, limit: 50);
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(summaryObj, McpResponseFormatter.JsonOptions)))
        {
            var root = doc.RootElement;
            Assert.Equal(10, root.GetProperty("registrationCount").GetInt32());
            Assert.False(root.TryGetProperty("registrations", out _));
            Assert.False(root.TryGetProperty("singleImplementationInterfaces", out _));
            Assert.False(root.TryGetProperty("captiveDependencies", out _));
        }
    }

    private static JsonDocument ParseResultJson(CallToolResult result)
    {
        var text = result.Content.OfType<TextContentBlock>().First().Text;
        return JsonDocument.Parse(text);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ArchLens.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate ArchLens.slnx.");
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "archlens-def-" + Guid.NewGuid().ToString("N")[..8]);
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
