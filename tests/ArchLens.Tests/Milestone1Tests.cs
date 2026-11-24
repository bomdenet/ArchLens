using ArchLens.Analysis;
using ArchLens.Extraction.Roslyn;
using ArchLens.Model;
using ArchLens.Rules.Yaml;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using ModelTypeKind = ArchLens.Model.TypeKind;

namespace ArchLens.Tests;

public sealed class Milestone1Tests
{
    [Fact]
    public void CognitiveComplexity_ComputesNestingSwitchBooleanAndRecursionAccurately()
    {
        var code = """
            public class Sample
            {
                public int StraightLine(int x) => x + 1;

                public int ElseIfChain(int x)
                {
                    if (x == 1) return 1;
                    else if (x == 2) return 2;
                    else return 3;
                }

                public int NestedControlFlow(int[] items, bool flag)
                {
                    if (flag) // +1 (nesting 0)
                    {
                        foreach (var i in items) // +1 + 1 = +2 (nesting 1)
                        {
                            if (i > 0) // +1 + 2 = +3 (nesting 2)
                            {
                                return i;
                            }
                        }
                    }
                    return 0;
                }

                public int SwitchOnlyOneIncrement(int x)
                {
                    switch (x) // +1 (nesting 0)
                    {
                        case 1: return 10;
                        case 2: return 20;
                        case 3: return 30;
                        case 4: return 40;
                        default: return 0;
                    }
                }

                public bool BooleanSequences(bool a, bool b, bool c, bool d)
                {
                    // Same operator chain: && -> +1
                    var same = a && b && c;
                    // Mixed operator chain: && (+1), || (+1), && (+1) -> +3
                    var mixed = a && b || c && d;
                    return same && mixed; // +1
                }

                public int Recursive(int n)
                {
                    if (n <= 1) return 1; // +1
                    return Recursive(n - 1) + Recursive(n - 2); // +1 for recursion
                }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(code);
        var methods = tree.GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
            .ToDictionary(m => m.Identifier.ValueText);

        Assert.Equal(0, CognitiveComplexityWalker.Calculate(methods["StraightLine"]));
        Assert.Equal(3, CognitiveComplexityWalker.Calculate(methods["ElseIfChain"]));
        Assert.Equal(6, CognitiveComplexityWalker.Calculate(methods["NestedControlFlow"]));
        Assert.Equal(1, CognitiveComplexityWalker.Calculate(methods["SwitchOnlyOneIncrement"]));
        Assert.Equal(5, CognitiveComplexityWalker.Calculate(methods["BooleanSequences"]));
        Assert.Equal(2, CognitiveComplexityWalker.Calculate(methods["Recursive"]));
    }

    [Fact]
    public void Lcom4_DistinguishesCohesiveAndSplitCandidateClasses()
    {
        var code = """
            namespace Demo;

            public interface IContract { void Run(); }

            public class CohesiveService
            {
                private int _state;
                public void A() { _state++; }
                public void B() { _state--; }
                public void C() { B(); }
            }

            public class PrimaryCtorCohesive(string name)
            {
                public string First() => name.ToUpperInvariant();
                public string Second() => name.ToLowerInvariant();
            }

            public class SplitCandidateGodClass
            {
                private int _orderCount;
                private string _emailHost = "";
                private double _taxRate;

                public void AddOrder() => _orderCount++;
                public int GetOrders() => _orderCount;

                public void SetHost(string h) => _emailHost = h;
                public string GetHost() => _emailHost;

                public double ComputeTax(double amount) => amount * _taxRate;
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(code);
        var refs = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var compilation = CSharpCompilation.Create("Lcom4Test", [tree], refs);
        var semanticModel = compilation.GetSemanticModel(tree);

        int Calc(string typeName)
        {
            var sym = compilation.GetTypeByMetadataName($"Demo.{typeName}")!;
            return Lcom4Calculator.Calculate(sym, _ => semanticModel);
        }

        Assert.Equal(0, Calc("IContract"));
        Assert.Equal(1, Calc("CohesiveService"));
        Assert.Equal(1, Calc("PrimaryCtorCohesive"));
        Assert.Equal(3, Calc("SplitCandidateGodClass"));
    }

    [Fact]
    public void MaxMetricAndHotspots_SupportCognitiveComplexityAndLcom4()
    {
        var nodes = new List<CodeNode>
        {
            new("App.Clean", "Clean", "App.Clean", NodeKind.Type, "App", "App", ModelTypeKind.Class,
                LineCount: 40, CyclomaticComplexity: 4, CognitiveComplexity: 2, Lcom4: 1,
                Metadata: new Dictionary<string, string> { ["maxMethodComplexity"] = "2", ["maxMethodCognitiveComplexity"] = "1" }),
            new("App.GodClass", "GodClass", "App.GodClass", NodeKind.Type, "App", "App", ModelTypeKind.Class,
                LineCount: 400, CyclomaticComplexity: 30, CognitiveComplexity: 45, Lcom4: 4,
                Metadata: new Dictionary<string, string> { ["maxMethodComplexity"] = "12", ["maxMethodCognitiveComplexity"] = "22" })
        };

        var graph = new CodeGraph("C:/repo", nodes, []);
        var yaml = """
            version: 1
            rules:
              - id: COG-1
                name: Max cognitive complexity
                severity: warning
                type: max_metric
                metric: cognitive_complexity
                threshold: 15
              - id: COH-1
                name: Max LCOM4
                severity: error
                type: max_metric
                metric: lcom4
                threshold: 2
            """;

        var rules = new YamlRuleSetProvider().ParseYaml(yaml);
        var analyzer = new ArchitectureAnalyzerService();

        var report = analyzer.CheckRules(graph, rules);
        Assert.Equal(1, report.ErrorCount);
        Assert.Equal(1, report.WarningCount);
        Assert.Contains(report.Violations, v => v.RuleId == "COG-1" && v.NodeId == "App.GodClass");
        Assert.Contains(report.Violations, v => v.RuleId == "COH-1" && v.NodeId == "App.GodClass");

        var cogHotspots = analyzer.GetHotspots(graph, sortBy: "cognitive");
        Assert.Equal("App.GodClass", cogHotspots.Items[0].NodeId);
        Assert.Equal(22, cogHotspots.Items[0].MaxMethodCognitiveComplexity);

        var lcomHotspots = analyzer.GetHotspots(graph, sortBy: "lcom4");
        Assert.Equal("App.GodClass", lcomHotspots.Items[0].NodeId);
        Assert.Equal(4, lcomHotspots.Items[0].Lcom4);
    }

    [Fact]
    public async Task RoslynExtractor_ExtractsMethodCallGraphAndMethodMetrics()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "MethodDemo.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            WriteFile(tempDir, "Demo.cs", """
                namespace MethodDemo;

                public class Worker
                {
                    public int Entry(int x) => Helper(x) + UnusedBranch(x);
                    private int Helper(int x) => Leaf(x);
                    private int UnusedBranch(int x) => x > 0 ? Leaf(x) : 0;
                    private int Leaf(int x) => x * 2;
                    public void Orphan() { }
                }
                """);

            var csprojPath = Path.Combine(tempDir, "MethodDemo.csproj");
            var extractor = new RoslynCodeGraphExtractor();
            var snapshot = await extractor.ExtractAsync(csprojPath, new ExtractionOptions(IncludeMethods: true));

            var methodNodes = snapshot.Graph.GetNodes(NodeKind.Method).ToList();
            Assert.True(methodNodes.Count >= 5);
            Assert.Contains(methodNodes, m => m.Name == "Entry" && m.ContainingTypeId == "MethodDemo.Worker");
            Assert.Contains(methodNodes, m => m.Name == "Orphan");

            var analyzer = new ArchitectureAnalyzerService();
            var callGraph = analyzer.GetDependencyGraph(snapshot.Graph, NodeKind.Method);
            Assert.Contains(callGraph.Edges, e => e.From.Contains(".Entry(") && e.To.Contains(".Helper("));
            Assert.Contains(callGraph.Edges, e => e.From.Contains(".Helper(") && e.To.Contains(".Leaf("));

            var leafImpact = analyzer.AnalyzeImpact(snapshot.Graph, "MethodDemo.Worker.Leaf(int)");
            Assert.NotNull(leafImpact);
            Assert.Equal(2, leafImpact.DirectDependentsCount); // Helper and UnusedBranch
            Assert.Equal(3, leafImpact.TotalTransitiveDependentsCount); // + Entry

            var methodHotspots = analyzer.GetHotspots(snapshot.Graph, sortBy: "cognitive", scope: NodeKind.Method);
            Assert.NotEmpty(methodHotspots.Items);
            Assert.Equal("MethodDemo.Worker.UnusedBranch(int)", methodHotspots.Items[0].NodeId);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task RoslynExtractor_SelectsRequestedTargetFrameworkOnMultiTargetedProject()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "MultiTfm.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFrameworks>net10.0;netstandard2.0</TargetFrameworks>
                    <LangVersion>latest</LangVersion>
                  </PropertyGroup>
                </Project>
                """);

            WriteFile(tempDir, "PlatformTypes.cs", """
                namespace MultiTfm;

                public class SharedType { }

                #if NET10_0
                public class Net10OnlyType { }
                #endif

                #if NETSTANDARD2_0
                public class NetStandardOnlyType { }
                #endif
                """);

            var csprojPath = Path.Combine(tempDir, "MultiTfm.csproj");
            var extractor = new RoslynCodeGraphExtractor();

            var net10Snap = await extractor.ExtractAsync(csprojPath, new ExtractionOptions(TargetFramework: "net10.0"));
            Assert.Equal("net10.0", net10Snap.Graph.Projects[0].TargetFramework);
            Assert.Contains(net10Snap.Graph.GetNodes(NodeKind.Type), t => t.Name == "Net10OnlyType");
            Assert.DoesNotContain(net10Snap.Graph.GetNodes(NodeKind.Type), t => t.Name == "NetStandardOnlyType");

            var netStdSnap = await extractor.ExtractAsync(csprojPath, new ExtractionOptions(TargetFramework: "netstandard2.0"));
            Assert.Equal("netstandard2.0", netStdSnap.Graph.Projects[0].TargetFramework);
            Assert.Contains(netStdSnap.Graph.GetNodes(NodeKind.Type), t => t.Name == "NetStandardOnlyType");
            Assert.DoesNotContain(netStdSnap.Graph.GetNodes(NodeKind.Type), t => t.Name == "Net10OnlyType");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task WorkspaceSessionManager_AutoInvalidatesCacheAndReExtractsOnlyDirtyProjects()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "Mini.slnx", """
                <Solution>
                  <Project Path="CoreLib/CoreLib.csproj" />
                  <Project Path="AppLib/AppLib.csproj" />
                  <Project Path="UtilLib/UtilLib.csproj" />
                </Solution>
                """);

            WriteFile(tempDir, "CoreLib/CoreLib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            WriteFile(tempDir, "CoreLib/CoreItem.cs", "namespace CoreLib; public class CoreItem { }");

            WriteFile(tempDir, "AppLib/AppLib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                  <ItemGroup><ProjectReference Include="../CoreLib/CoreLib.csproj" /></ItemGroup>
                </Project>
                """);
            WriteFile(tempDir, "AppLib/AppRunner.cs", "namespace AppLib; public class AppRunner { public CoreLib.CoreItem Create() => new(); }");

            WriteFile(tempDir, "UtilLib/UtilLib.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            WriteFile(tempDir, "UtilLib/Helper.cs", "namespace UtilLib; public class Helper { }");

            using var session = new WorkspaceSessionManager([new RoslynCodeGraphExtractor()]);
            var slnxPath = Path.Combine(tempDir, "Mini.slnx");

            // 1. Initial load
            var snap1 = await session.GetOrLoadAsync(slnxPath);
            Assert.Equal(3, snap1.Graph.Projects.Count);
            Assert.Contains(snap1.Graph.GetNodes(NodeKind.Type), t => t.FullName == "AppLib.AppRunner");

            // 2. Unmodified second load -> exact cache hit
            var snap2 = await session.GetOrLoadAsync(slnxPath);
            Assert.Equal(snap1.SnapshotId, snap2.SnapshotId);

            // 3. Edit a leaf project (AppLib) on disk without calling forceRefresh
            await Task.Delay(50);
            WriteFile(tempDir, "AppLib/AppRunner.cs", """
                namespace AppLib;
                public class AppRunner { public CoreLib.CoreItem Create() => new(); }
                public class AddedInApp { public CoreLib.CoreItem Item { get; } = new(); }
                """);

            var snap3 = await session.GetOrLoadAsync(slnxPath);
            Assert.NotEqual(snap1.SnapshotId, snap3.SnapshotId);
            Assert.Contains(snap3.Graph.GetNodes(NodeKind.Type), t => t.FullName == "AppLib.AddedInApp");
            Assert.Contains(snap3.Graph.GetNodes(NodeKind.Type), t => t.FullName == "UtilLib.Helper");
            Assert.Contains(snap3.Graph.Diagnostics, d => d.Source == "IncrementalExtractor" && d.Message.Contains("1/3") && d.Message.Contains("AppLib"));
            // Cross-project edge from newly added type to preserved CoreLib type should resolve!
            Assert.Contains(snap3.Graph.Edges, e => e.FromId == "AppLib.AddedInApp" && e.ToId == "CoreLib.CoreItem");

            // 4. Edit upstream CoreLib -> must re-extract CoreLib AND dependent AppLib (2/3), preserving UtilLib
            await Task.Delay(50);
            WriteFile(tempDir, "CoreLib/CoreItem.cs", """
                namespace CoreLib;
                public class CoreItem { }
                public class AddedInCore { }
                """);

            var snap4 = await session.GetOrLoadAsync(slnxPath);
            Assert.NotEqual(snap3.SnapshotId, snap4.SnapshotId);
            Assert.Contains(snap4.Graph.GetNodes(NodeKind.Type), t => t.FullName == "CoreLib.AddedInCore");
            Assert.Contains(snap4.Graph.Diagnostics, d => d.Source == "IncrementalExtractor" && d.Message.Contains("2/3"));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public void CognitiveComplexity_HandlesLocalFunctionsPatternsOverloadsAndNestedTypes()
    {
        var code = """
            public class EdgeCases
            {
                public int OverloadCaller() => OverloadCaller(42);
                public int OverloadCaller(int x) => x;

                public bool PatternCombinator(int x) => x is > 0 and < 10 or 42; // and (+1), or (+1) = 2

                public int NullCoalesceIsZero(int? a, int? b) => a ?? b ?? 0; // 0

                public int WithLocalFunction(int x)
                {
                    int Local(int y)
                    {
                        if (y > 0) return y; // nesting 1 -> +1 + 1 = +2 inside WithLocalFunction; +1 standalone!
                        return 0;
                    }
                    return Local(x);
                }
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(code);
        var methods = tree.GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>()
            .ToList();
        var localFn = tree.GetRoot()
            .DescendantNodes()
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.LocalFunctionStatementSyntax>()
            .Single();

        Assert.Equal(0, CognitiveComplexityWalker.Calculate(methods[0])); // OverloadCaller() -> not recursive
        Assert.Equal(2, CognitiveComplexityWalker.Calculate(methods[2])); // PatternCombinator
        Assert.Equal(0, CognitiveComplexityWalker.Calculate(methods[3])); // NullCoalesceIsZero
        Assert.Equal(2, CognitiveComplexityWalker.Calculate(methods[4])); // WithLocalFunction (local fn adds nesting 1)
        Assert.Equal(1, CognitiveComplexityWalker.Calculate(localFn));    // Standalone local function (no off-by-one!)
    }

    [Fact]
    public void Lcom4_HandlesBackedPropertiesInheritedStateAndNameof()
    {
        var code = """
            namespace Demo;

            public class BaseEntity
            {
                protected int Id;
            }

            public class DerivedCohesive : BaseEntity
            {
                public int ReadId() => Id;
                public void ResetId() => Id = 0;
            }

            public class BackedPropertyCohesive
            {
                private decimal _balance;
                public decimal Balance => _balance;
                public void Deposit(decimal amount) => _balance += amount;
                public bool IsPositive() => Balance > 0;
            }

            public class NameofNotCohesive
            {
                private int _a;
                public int ReadA() => _a;
                public string GetNameOnly() => nameof(_a);
            }
            """;

        var tree = CSharpSyntaxTree.ParseText(code);
        var refs = new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };
        var compilation = CSharpCompilation.Create("Lcom4EdgeTest", [tree], refs);
        var semanticModel = compilation.GetSemanticModel(tree);

        int Calc(string typeName) =>
            Lcom4Calculator.Calculate(compilation.GetTypeByMetadataName($"Demo.{typeName}")!, _ => semanticModel);

        Assert.Equal(1, Calc("DerivedCohesive"));
        Assert.Equal(1, Calc("BackedPropertyCohesive"));
        Assert.Equal(2, Calc("NameofNotCohesive"));
    }

    [Fact]
    public async Task RoslynExtractor_HandlesPrimaryConstructorsStaticCtorsRefOverloadsConditionalAccessAndMethodCycles()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "MethodEdges.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            WriteFile(tempDir, "Samples.cs", """
                namespace MethodEdges;

                public class PrimaryCtorSample(int seed)
                {
                    private readonly int _seed = seed;

                    public int ComplexMethod(int x)
                    {
                        if (x > 0)
                        {
                            if (x > 10) return Helper(x);
                        }
                        return _seed;
                    }

                    private int Helper(int v) => v + 1;
                }

                public class OverloadsAndCtors
                {
                    static OverloadsAndCtors() { }
                    public OverloadsAndCtors() : this(0) { }
                    public OverloadsAndCtors(int x) { }

                    public void Parse(int x) { }
                    public void Parse(out int x) { x = 1; }

                    public string UseConditionalAndNameof(OverloadsAndCtors? other)
                    {
                        other?.Parse(5);
                        return nameof(Parse);
                    }
                }

                public class MutualRecursion
                {
                    public int Ping(int n) => n <= 0 ? 0 : Pong(n - 1);
                    public int Pong(int n) => n <= 0 ? 0 : Ping(n - 1);
                }
                """);

            var csprojPath = Path.Combine(tempDir, "MethodEdges.csproj");
            var extractor = new RoslynCodeGraphExtractor();
            var snapshot = await extractor.ExtractAsync(csprojPath, new ExtractionOptions(IncludeMethods: true));
            var graph = snapshot.Graph;

            // Primary ctor must NOT absorb ComplexMethod's complexity or its call to Helper
            var primaryCtor = graph.FindNode("MethodEdges.PrimaryCtorSample.PrimaryCtorSample(int)");
            Assert.NotNull(primaryCtor);
            Assert.Equal(1, primaryCtor.CyclomaticComplexity);
            Assert.Equal(0, primaryCtor.CognitiveComplexity);
            Assert.DoesNotContain(graph.GetOutgoing(primaryCtor.Id), e => e.ToId.Contains("Helper"));

            // Static ctor and parameterless instance ctor must NOT collide
            Assert.NotNull(graph.FindNode("MethodEdges.OverloadsAndCtors.cctor()"));
            Assert.NotNull(graph.FindNode("MethodEdges.OverloadsAndCtors.OverloadsAndCtors()"));

            // Constructor chaining : this(0) must emit edge
            Assert.Contains(graph.GetOutgoing("MethodEdges.OverloadsAndCtors.OverloadsAndCtors()"),
                e => e.ToId == "MethodEdges.OverloadsAndCtors.OverloadsAndCtors(int)");

            // ref/out overload IDs must NOT collide
            Assert.NotNull(graph.FindNode("MethodEdges.OverloadsAndCtors.Parse(int)"));
            Assert.NotNull(graph.FindNode("MethodEdges.OverloadsAndCtors.Parse(out int)"));

            // Conditional access obj?.Parse(5) must have weight 1 (not double-counted), and nameof(Parse) must not add edge
            var useMethodEdges = graph.GetOutgoing("MethodEdges.OverloadsAndCtors.UseConditionalAndNameof(MethodEdges.OverloadsAndCtors)")
                .Where(e => e.ToId == "MethodEdges.OverloadsAndCtors.Parse(int)")
                .ToList();
            Assert.Single(useMethodEdges);
            Assert.Equal(1, useMethodEdges[0].Weight);

            // no_cycles at method scope must detect Ping <-> Pong mutual recursion
            var yaml = """
                version: 1
                rules:
                  - id: METH-CYC
                    name: No method cycles
                    severity: error
                    type: no_cycles
                    scope: method
                """;
            var rules = new YamlRuleSetProvider().ParseYaml(yaml);
            var report = new ArchitectureAnalyzerService().CheckRules(graph, rules);
            Assert.Equal(1, report.ErrorCount);
            Assert.Contains(report.Violations, v => v.RuleId == "METH-CYC" && v.Message.Contains("Ping"));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "archlens-test-" + Guid.NewGuid().ToString("N")[..8]);
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
        try { Directory.Delete(dir, recursive: true); } catch { /* ignore cleanup races */ }
    }
}
