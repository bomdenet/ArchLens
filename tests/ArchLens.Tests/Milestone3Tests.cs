using ArchLens.Analysis;
using ArchLens.Extraction.Metadata;
using ArchLens.Extraction.Roslyn;
using ArchLens.Model;
using ArchLens.Rules.Yaml;

namespace ArchLens.Tests;

public sealed class Milestone3Tests
{
    [Fact]
    public async Task MetadataIlExtractor_ExtractsCompiledAssembliesWithPdbLocationsMethodGraphAndZeroRuleErrors()
    {
        var repoRoot = FindRepoRoot();
        var slnxPath = Path.Combine(repoRoot, "ArchLens.slnx");

        var ilExtractor = new MetadataCodeGraphExtractor();
        var snapshot = await ilExtractor.ExtractAsync(slnxPath, new ExtractionOptions(IncludeTestProjects: false, IncludeMethods: true));

        Assert.True(snapshot.Graph.Projects.Count >= 4);
        Assert.True(snapshot.Graph.GetNodes(NodeKind.Type).Count() >= 20);
        Assert.True(snapshot.Graph.GetNodes(NodeKind.Method).Count() >= 50);
        Assert.True(snapshot.Graph.Edges.Count > 50);

        // Verify Portable PDB source locations were resolved
        var typesWithLocations = snapshot.Graph.GetNodes(NodeKind.Type).Where(t => t.Location is not null && t.Location.Line > 0).ToList();
        Assert.True(typesWithLocations.Count >= 10, "Expected Portable PDB source locations on compiled ArchLens types.");

        // Verify key types and dependencies match semantic structure
        Assert.NotNull(snapshot.Graph.FindNode("ArchLens.Analysis.CycleDetector"));
        Assert.Contains(snapshot.Graph.Edges, e => e.FromId == "ArchLens.Analysis.CycleDetector" && e.ToId == "ArchLens.Model.CodeGraph");

        // Verify method-level call graph edges exist in IL output
        var methodEdges = snapshot.Graph.Edges
            .Where(e => snapshot.Graph.FindNode(e.FromId)?.Kind == NodeKind.Method &&
                        snapshot.Graph.FindNode(e.ToId)?.Kind == NodeKind.Method)
            .ToList();
        Assert.NotEmpty(methodEdges);

        // Verify .archlens.yaml architecture rules pass with 0 errors on IL-extracted graph!
        var rulesPath = YamlRuleSetProvider.DiscoverConfigFile(repoRoot)!;
        var rules = new YamlRuleSetProvider().LoadRules(rulesPath);
        var report = new ArchitectureAnalyzerService().CheckRules(snapshot.Graph, rules);
        Assert.Equal(0, report.ErrorCount);
    }

    [Fact]
    public async Task MetadataIlExtractor_CanHandleSingleDllAndComputesIlComplexityAndLcom4()
    {
        var analysisDll = typeof(CycleDetector).Assembly.Location;
        Assert.True(File.Exists(analysisDll));

        var ilExtractor = new MetadataCodeGraphExtractor();
        Assert.True(ilExtractor.CanHandle(analysisDll));

        var snapshot = await ilExtractor.ExtractAsync(analysisDll);
        Assert.Single(snapshot.Graph.Projects);
        Assert.Equal("ArchLens.Analysis", snapshot.Graph.Projects[0].Name);

        var cycleDetector = snapshot.Graph.FindNode("ArchLens.Analysis.CycleDetector");
        Assert.NotNull(cycleDetector);
        Assert.True(cycleDetector.CyclomaticComplexity > 1);
        Assert.Equal(0, cycleDetector.Lcom4); // static class -> 0

        // Cohesive non-static class (MetricsEngine) should have LCOM4 >= 1, and Record nodes should be detected as Record
        var metricsEngine = snapshot.Graph.FindNode("ArchLens.Analysis.MetricsEngine");
        Assert.NotNull(metricsEngine);
        Assert.True(metricsEngine.Lcom4 >= 1);

        var overviewReport = snapshot.Graph.FindNode("ArchLens.Analysis.SolutionOverviewReport");
        Assert.NotNull(overviewReport);
        Assert.Equal(TypeKind.Record, overviewReport.TypeKind);
    }

    [Fact]
    public async Task MetadataIlExtractor_FoldsAsyncLambdasCatchGenericArgsAutoPropertyLcom4AndDelegates()
    {
        var tempDir = CreateTempDir();
        try
        {
            // Compile a real C# assembly with Roslyn CSharpCompilation + Embedded Portable PDB into a temp DLL!
            var code = """
                using System;
                using System.Collections.Generic;
                using System.Threading.Tasks;

                namespace IlDeep;

                public class DomainException : Exception { }
                public class SecretPayload { }
                public class AsyncTarget { public int Compute(int x) => x * 2; }
                public delegate void MyCallback(int x);

                public class AsyncAndLambdaHost
                {
                    private readonly AsyncTarget _target = new();

                    public async Task<List<SecretPayload>> RunAsync(int x)
                    {
                        await Task.Yield();
                        try
                        {
                            if (x > 10)
                            {
                                var fn = new Func<int, int>(v => v > 0 ? _target.Compute(v) : 0);
                                _ = fn(x);
                            }
                        }
                        catch (DomainException)
                        {
                            return [];
                        }
                        return [new SecretPayload()];
                    }
                }

                public class AutoPropCohesive
                {
                    public int Value { get; set; }
                    public void Inc() => Value++;
                    public int Read() => Value;
                }

                public class SplitMe
                {
                    private int _a;
                    private string _b = "";
                    public void TouchA() => _a++;
                    public void TouchB() => _b = "x";
                }
                """;

            var tree = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
                code,
                path: Path.Combine(tempDir, "IlDeep.cs"),
                encoding: System.Text.Encoding.UTF8);
            var refs = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
                .Select(a => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(a.Location))
                .ToList();

            var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
                "IlDeep",
                [tree],
                refs,
                new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

            var dllPath = Path.Combine(tempDir, "IlDeep.dll");
            var emitOpts = new Microsoft.CodeAnalysis.Emit.EmitOptions(debugInformationFormat: Microsoft.CodeAnalysis.Emit.DebugInformationFormat.Embedded);
            Microsoft.CodeAnalysis.Emit.EmitResult emitResult;
            using (var peStream = File.Create(dllPath))
            {
                emitResult = compilation.Emit(peStream, options: emitOpts);
            }
            Assert.True(emitResult.Success, string.Join("\n", emitResult.Diagnostics));

            // Test binary-directory auto-detection through WorkspaceSessionManager with Roslyn registered FIRST!
            using var roslyn = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var metadata = new MetadataCodeGraphExtractor();
            using var session = new WorkspaceSessionManager([roslyn, metadata]);

            var snapshot = await session.GetOrLoadAsync(tempDir, options: new ExtractionOptions(IncludeMethods: true));
            var graph = snapshot.Graph;

            // Async state machine & lambda must be folded into AsyncAndLambdaHost!
            var host = graph.FindNode("IlDeep.AsyncAndLambdaHost");
            Assert.NotNull(host);
            Assert.NotNull(host.Location);
            Assert.True(host.CyclomaticComplexity >= 4, $"Expected folded async+lambda+catch CC >= 4, got {host.CyclomaticComplexity}");
            Assert.Contains(graph.Edges, e => e.FromId == "IlDeep.AsyncAndLambdaHost" && e.ToId == "IlDeep.DomainException");
            Assert.Contains(graph.Edges, e => e.FromId == "IlDeep.AsyncAndLambdaHost" && e.ToId == "IlDeep.SecretPayload");
            Assert.Contains(graph.Edges, e => e.FromId == "IlDeep.AsyncAndLambdaHost" && e.ToId == "IlDeep.AsyncTarget");

            // Return type must NOT emit bogus Parameter edge!
            Assert.DoesNotContain(graph.Edges, e => e.FromId == "IlDeep.AsyncAndLambdaHost" && e.ToId == "IlDeep.SecretPayload" && e.Kind == DependencyKind.Parameter);

            // Auto-property LCOM4 == 1, SplitMe LCOM4 == 2, Delegate LCOM4 == 0!
            Assert.Equal(1, graph.FindNode("IlDeep.AutoPropCohesive")!.Lcom4);
            Assert.Equal(2, graph.FindNode("IlDeep.SplitMe")!.Lcom4);
            Assert.Equal(0, graph.FindNode("IlDeep.MyCallback")!.Lcom4);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task SqliteGraphCache_HandlesMultiRootSiblingProjectReferencesAcrossRestarts()
    {
        var tempDir = CreateTempDir();
        var dbPath = Path.Combine(tempDir, "cache.db");
        try
        {
            WriteFile(tempDir, "SiblingCore/SiblingCore.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
                """);
            WriteFile(tempDir, "SiblingCore/CoreModel.cs", "namespace SiblingCore; public class CoreModel { }");

            WriteFile(tempDir, "LeafApp/LeafApp.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../SiblingCore/SiblingCore.csproj" /></ItemGroup>
                </Project>
                """);
            WriteFile(tempDir, "LeafApp/Consumer.cs", "namespace LeafApp; public class Consumer { public SiblingCore.CoreModel? M { get; set; } }");

            var leafCsproj = Path.Combine(tempDir, "LeafApp", "LeafApp.csproj");
            var extractCount = 0;
            using var inner = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
            var counting = new CountingExtractor(inner, () => extractCount++);

            string firstSnapId;
            using (var cache1 = new SqliteGraphCache(dbPath))
            using (var session1 = new WorkspaceSessionManager([counting], cache1))
            {
                var s1 = await session1.GetOrLoadAsync(leafCsproj);
                firstSnapId = s1.SnapshotId;
                Assert.Equal(1, extractCount);
            }

            // Cold start with multi-root sibling ProjectReference -> MUST HIT SQLite cache!
            using (var cache2 = new SqliteGraphCache(dbPath))
            using (var session2 = new WorkspaceSessionManager([counting], cache2))
            {
                var s2 = await session2.GetOrLoadAsync(leafCsproj);
                Assert.Equal(1, extractCount);
                Assert.Equal(firstSnapId, s2.SnapshotId);
            }

            // Edit sibling ../SiblingCore -> MUST invalidate SQLite cache on next restart!
            await Task.Delay(50);
            WriteFile(tempDir, "SiblingCore/CoreModel.cs", "namespace SiblingCore; public class CoreModel { } public class CoreV2 { }");

            using (var cache3 = new SqliteGraphCache(dbPath))
            using (var session3 = new WorkspaceSessionManager([counting], cache3))
            {
                var s3 = await session3.GetOrLoadAsync(leafCsproj);
                Assert.Equal(2, extractCount);
                Assert.NotEqual(firstSnapId, s3.SnapshotId);
            }
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task SqliteGraphCache_PersistsSnapshotsAcrossSessionRestartsAndInvalidatesOnFileChange()
    {
        var tempDir = CreateTempDir();
        var dbPath = Path.Combine(tempDir, "cache", "test-cache.db");
        try
        {
            WriteFile(tempDir, "CacheApp.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            WriteFile(tempDir, "Widget.cs", "namespace CacheApp; public class Widget { }");

            var csprojPath = Path.Combine(tempDir, "CacheApp.csproj");
            var extractCount = 0;
            var countingExtractor = new CountingExtractor(new RoslynCodeGraphExtractor(keepWorkspacesWarm: false), () => extractCount++);

            string firstSnapshotId;
            using (var cache1 = new SqliteGraphCache(dbPath))
            using (var session1 = new WorkspaceSessionManager([countingExtractor], cache1))
            {
                var snap1 = await session1.GetOrLoadAsync(csprojPath);
                firstSnapshotId = snap1.SnapshotId;
                Assert.Equal(1, extractCount);
                Assert.Contains(snap1.Graph.GetNodes(NodeKind.Type), t => t.FullName == "CacheApp.Widget");
            }

            Assert.True(File.Exists(dbPath));

            // Simulate MCP restart: brand new WorkspaceSessionManager & SqliteGraphCache -> cold-start HIT!
            using (var cache2 = new SqliteGraphCache(dbPath))
            using (var session2 = new WorkspaceSessionManager([countingExtractor], cache2))
            {
                var snap2 = await session2.GetOrLoadAsync(csprojPath);
                Assert.Equal(1, extractCount); // Extractor was NOT called!
                Assert.Equal(firstSnapshotId, snap2.SnapshotId);
                Assert.Contains(snap2.Graph.GetNodes(NodeKind.Type), t => t.FullName == "CacheApp.Widget");
            }

            // Modify file on disk while server is down -> fingerprint changes -> cache MISS + re-extract!
            await Task.Delay(50);
            WriteFile(tempDir, "Widget.cs", "namespace CacheApp; public class Widget { } public class Gadget { }");

            using (var cache3 = new SqliteGraphCache(dbPath))
            using (var session3 = new WorkspaceSessionManager([countingExtractor], cache3))
            {
                var snap3 = await session3.GetOrLoadAsync(csprojPath);
                Assert.Equal(2, extractCount);
                Assert.NotEqual(firstSnapshotId, snap3.SnapshotId);
                Assert.Contains(snap3.Graph.GetNodes(NodeKind.Type), t => t.FullName == "CacheApp.Gadget");
            }
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task RoslynIncrementalDocumentUpdate_HandlesModifyAddAndDeleteWithoutReopeningSolution()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "Warm.slnx", """
                <Solution>
                  <Project Path="CoreLib/CoreLib.csproj" />
                  <Project Path="AppLib/AppLib.csproj" />
                  <Project Path="UtilLib/UtilLib.csproj" />
                </Solution>
                """);

            WriteFile(tempDir, "CoreLib/CoreLib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
                """);
            WriteFile(tempDir, "CoreLib/Item.cs", "namespace CoreLib; public class Item { }");

            WriteFile(tempDir, "AppLib/AppLib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../CoreLib/CoreLib.csproj" /></ItemGroup>
                </Project>
                """);
            WriteFile(tempDir, "AppLib/Runner.cs", "namespace AppLib; public class Runner { public CoreLib.Item Create() => new(); }");

            WriteFile(tempDir, "UtilLib/UtilLib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>
                """);
            WriteFile(tempDir, "UtilLib/Util.cs", "namespace UtilLib; public class Util { }");

            var slnxPath = Path.Combine(tempDir, "Warm.slnx");
            using var roslyn = new RoslynCodeGraphExtractor(keepWorkspacesWarm: true);
            using var session = new WorkspaceSessionManager([roslyn]);

            var s1 = await session.GetOrLoadAsync(slnxPath);
            Assert.Equal(3, s1.Graph.Projects.Count);

            // 1. Modify existing file -> warm in-place Document.WithDocumentText
            await Task.Delay(50);
            WriteFile(tempDir, "AppLib/Runner.cs", """
                namespace AppLib;
                public class Runner { public CoreLib.Item Create() => new(); }
                public class ModifiedExtra { public CoreLib.Item Ref => new(); }
                """);

            var s2 = await session.GetOrLoadAsync(slnxPath);
            Assert.Contains(s2.Graph.GetNodes(NodeKind.Type), t => t.FullName == "AppLib.ModifiedExtra");
            Assert.Contains(s2.Graph.Edges, e => e.FromId == "AppLib.ModifiedExtra" && e.ToId == "CoreLib.Item");
            Assert.Contains(s2.Graph.Diagnostics, d => d.Source == "IncrementalRoslynDocumentUpdate" && d.Message.Contains("1 updated") && d.Message.Contains("1/3"));

            // 2. Add brand new .cs file -> warm in-place Solution.AddDocument
            await Task.Delay(50);
            var addedPath = Path.Combine(tempDir, "AppLib", "BrandNew.cs");
            WriteFile(tempDir, "AppLib/BrandNew.cs", "namespace AppLib; public class BrandNew { public Runner? R { get; set; } }");

            var s3 = await session.GetOrLoadAsync(slnxPath);
            Assert.Contains(s3.Graph.GetNodes(NodeKind.Type), t => t.FullName == "AppLib.BrandNew");
            Assert.Contains(s3.Graph.Edges, e => e.FromId == "AppLib.BrandNew" && e.ToId == "AppLib.Runner");
            Assert.Contains(s3.Graph.Diagnostics, d => d.Source == "IncrementalRoslynDocumentUpdate" && d.Message.Contains("1 added"));

            // 3. Delete .cs file -> warm in-place Solution.RemoveDocument
            await Task.Delay(50);
            File.Delete(addedPath);

            var s4 = await session.GetOrLoadAsync(slnxPath);
            Assert.DoesNotContain(s4.Graph.GetNodes(NodeKind.Type), t => t.FullName == "AppLib.BrandNew");
            Assert.Contains(s4.Graph.Diagnostics, d => d.Source == "IncrementalRoslynDocumentUpdate" && d.Message.Contains("1 removed"));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private sealed class CountingExtractor(ICodeGraphExtractor inner, Action onExtract) : ICodeGraphExtractor
    {
        public string Name => inner.Name;
        public bool CanHandle(string targetPath) => inner.CanHandle(targetPath);

        public Task<ArchitectureSnapshot> ExtractAsync(string targetPath, ExtractionOptions? options = null, CancellationToken cancellationToken = default)
        {
            onExtract();
            return inner.ExtractAsync(targetPath, options, cancellationToken);
        }
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
        var path = Path.Combine(Path.GetTempPath(), "archlens-m3-" + Guid.NewGuid().ToString("N")[..8]);
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
