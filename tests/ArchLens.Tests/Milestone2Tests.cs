using System.Text.Json;
using ArchLens.Analysis;
using ArchLens.Extraction.Roslyn;
using ArchLens.Model;
using ArchLens.Rules.Yaml;

namespace ArchLens.Tests;

public sealed class Milestone2Tests
{
    [Fact]
    public void DeadCodeAnalyzer_DetectsUnreferencedAndDeadIslandsAcrossBuiltInDetectors()
    {
        var nodes = new List<CodeNode>
        {
            new("App.Program", "Program", "App.Program", NodeKind.Type, "App", "App", TypeKind.Class, AccessibilityKind.Internal,
                Metadata: new Dictionary<string, string> { ["isEntryPointMain"] = "true", ["outputKind"] = "ConsoleApplication" }),
            new("App.UsedService", "UsedService", "App.UsedService", NodeKind.Type, "App", "App", TypeKind.Class, AccessibilityKind.Internal,
                Metadata: new Dictionary<string, string> { ["outputKind"] = "ConsoleApplication" }),
            new("App.OrdersController", "OrdersController", "App.OrdersController", NodeKind.Type, "App", "App", TypeKind.Class, AccessibilityKind.Public,
                Metadata: new Dictionary<string, string> { ["attributes"] = "ApiControllerAttribute", ["outputKind"] = "ConsoleApplication" }),
            new("App.CreateOrderHandler", "CreateOrderHandler", "App.CreateOrderHandler", NodeKind.Type, "App", "App", TypeKind.Class, AccessibilityKind.Internal,
                Metadata: new Dictionary<string, string> { ["baseTypes"] = "MediatR.IRequestHandler<CreateOrder>", ["outputKind"] = "ConsoleApplication" }),
            new("App.OrderConsumer", "OrderConsumer", "App.OrderConsumer", NodeKind.Type, "App", "App", TypeKind.Class, AccessibilityKind.Internal,
                Metadata: new Dictionary<string, string> { ["baseTypes"] = "MassTransit.IConsumer<OrderCreated>", ["outputKind"] = "ConsoleApplication" }),
            new("App.DiRegisteredService", "DiRegisteredService", "App.DiRegisteredService", NodeKind.Type, "App", "App", TypeKind.Class, AccessibilityKind.Internal,
                Metadata: new Dictionary<string, string> { ["outputKind"] = "ConsoleApplication" }),
            new("App.OrderTests", "OrderTests", "App.OrderTests", NodeKind.Type, "App", "App", TypeKind.Class, AccessibilityKind.Public,
                Metadata: new Dictionary<string, string> { ["attributes"] = "TestFixtureAttribute", ["outputKind"] = "ConsoleApplication" }),
            new("Lib.PublicContract", "PublicContract", "Lib.PublicContract", NodeKind.Type, "Lib", "Lib", TypeKind.Class, AccessibilityKind.Public,
                Metadata: new Dictionary<string, string> { ["outputKind"] = "DynamicallyLinkedLibrary" }),
            new("App.DeadOrphan", "DeadOrphan", "App.DeadOrphan", NodeKind.Type, "App", "App", TypeKind.Class, AccessibilityKind.Internal,
                Metadata: new Dictionary<string, string> { ["outputKind"] = "ConsoleApplication" }),
            new("App.DeadIslandA", "DeadIslandA", "App.DeadIslandA", NodeKind.Type, "App", "App", TypeKind.Class, AccessibilityKind.Internal,
                Metadata: new Dictionary<string, string> { ["outputKind"] = "ConsoleApplication" }),
            new("App.DeadIslandB", "DeadIslandB", "App.DeadIslandB", NodeKind.Type, "App", "App", TypeKind.Class, AccessibilityKind.Internal,
                Metadata: new Dictionary<string, string> { ["outputKind"] = "ConsoleApplication" })
        };

        var edges = new List<DependencyEdge>
        {
            new("App.Program", "App.UsedService", DependencyKind.MethodCall),
            // Mutual cycle disconnected from any entry point (dead island!)
            new("App.DeadIslandA", "App.DeadIslandB", DependencyKind.MethodCall),
            new("App.DeadIslandB", "App.DeadIslandA", DependencyKind.MethodCall)
        };

        var diRegs = new List<DiRegistration>
        {
            new("App.DiRegisteredService", "App.DiRegisteredService", ServiceLifetimeKind.Scoped)
        };

        var graph = new CodeGraph("C:/repo", nodes, edges, diRegistrations: diRegs);
        var analyzer = new ArchitectureAnalyzerService();

        var report = analyzer.AnalyzeDeadCode(graph, NodeKind.Type, treatPublicApiAsEntryPoint: true);

        Assert.Equal(3, report.UnreferencedCount);
        Assert.Contains(report.Candidates, c => c.NodeId == "App.DeadOrphan" && c.Reason.Contains("0 incoming"));
        Assert.Contains(report.Candidates, c => c.NodeId == "App.DeadIslandA" && c.Reason.Contains("Unreachable island"));
        Assert.Contains(report.Candidates, c => c.NodeId == "App.DeadIslandB" && c.Reason.Contains("Unreachable island"));
        Assert.DoesNotContain(report.Candidates, c => c.NodeId == "App.UsedService");
        Assert.DoesNotContain(report.Candidates, c => c.NodeId == "App.OrdersController");
        Assert.DoesNotContain(report.Candidates, c => c.NodeId == "App.CreateOrderHandler");
        Assert.DoesNotContain(report.Candidates, c => c.NodeId == "App.OrderConsumer");
        Assert.DoesNotContain(report.Candidates, c => c.NodeId == "App.DiRegisteredService");
        Assert.DoesNotContain(report.Candidates, c => c.NodeId == "App.OrderTests");
        Assert.DoesNotContain(report.Candidates, c => c.NodeId == "Lib.PublicContract");

        // Custom entry point detector can exempt DeadOrphan
        var customAnalyzer = new ArchitectureAnalyzerService(customEntryPointDetectors: [new CustomPrefixDetector("DeadOrphan")]);
        var customReport = customAnalyzer.AnalyzeDeadCode(graph, NodeKind.Type, treatPublicApiAsEntryPoint: true);
        Assert.Equal(2, customReport.UnreferencedCount);
        Assert.DoesNotContain(customReport.Candidates, c => c.NodeId == "App.DeadOrphan");
    }

    [Fact]
    public async Task RoslynExtractor_ParsesDiGraphDetectsDirectAndTransitiveCaptiveDependenciesAndSingleImpls()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "DiSample.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            WriteFile(tempDir, "DiAbstractions.cs", """
                namespace Microsoft.Extensions.DependencyInjection
                {
                    public interface IServiceCollection { }
                    public interface IServiceScopeFactory { }

                    public static class ServiceCollectionExtensions
                    {
                        public static IServiceCollection AddSingleton<TService, TImpl>(this IServiceCollection s) where TImpl : TService => s;
                        public static IServiceCollection AddSingleton<TService>(this IServiceCollection s) => s;
                        public static IServiceCollection AddSingleton<TService>(this IServiceCollection s, Func<IServiceProvider, TService> factory) => s;
                        public static IServiceCollection AddScoped<TService, TImpl>(this IServiceCollection s) where TImpl : TService => s;
                        public static IServiceCollection AddScoped<TService>(this IServiceCollection s) => s;
                        public static IServiceCollection AddTransient<TService, TImpl>(this IServiceCollection s) where TImpl : TService => s;
                    }
                }
                """);

            WriteFile(tempDir, "Services.cs", """
                using Microsoft.Extensions.DependencyInjection;

                namespace DiSample;

                public interface IDbContext { }
                public class AppDbContext : IDbContext { }

                public interface IValidator { }
                public class OrderValidator(IDbContext db) : IValidator { public IDbContext Db => db; }

                public interface IMulti { }
                public class MultiA : IMulti { }
                public class MultiB : IMulti { }

                // Direct captive dependency: Singleton -> Scoped
                public class BadDirectSingleton(IDbContext db) { public IDbContext Db => db; }

                // Transitive captive dependency: Singleton -> Transient -> Scoped
                public class BadTransitiveSingleton(IValidator validator) { public IValidator Validator => validator; }

                // Safe singleton using IServiceScopeFactory
                public class SafeSingleton(IServiceScopeFactory scopeFactory) { public IServiceScopeFactory Factory => scopeFactory; }

                public class UnusedHelper { }
                """);

            WriteFile(tempDir, "Program.cs", """
                using DiSample;
                using Microsoft.Extensions.DependencyInjection;

                IServiceCollection services = null!;
                services.AddScoped<IDbContext, AppDbContext>();
                services.AddTransient<IValidator, OrderValidator>();
                services.AddSingleton<BadDirectSingleton>();
                services.AddSingleton<BadTransitiveSingleton>();
                services.AddSingleton<SafeSingleton>();
                services.AddTransient<IMulti, MultiA>();
                services.AddTransient<IMulti, MultiB>();
                """);

            var csprojPath = Path.Combine(tempDir, "DiSample.csproj");
            var extractor = new RoslynCodeGraphExtractor();
            var snapshot = await extractor.ExtractAsync(csprojPath, new ExtractionOptions(IncludeMethods: true));

            var analyzer = new ArchitectureAnalyzerService();
            var diReport = analyzer.AnalyzeDi(snapshot.Graph);

            Assert.True(diReport.RegistrationCount >= 7);
            Assert.Equal(2, diReport.CaptiveDependencyCount);
            Assert.Contains(diReport.CaptiveDependencies, c =>
                c.ConsumerTypeId == "DiSample.BadDirectSingleton" && c.DependencyTypeId == "DiSample.IDbContext");
            Assert.Contains(diReport.CaptiveDependencies, c =>
                c.ConsumerTypeId == "DiSample.BadTransitiveSingleton" &&
                c.DependencyTypeId == "DiSample.IDbContext" &&
                c.DependencyPath.Contains("Transient"));
            Assert.DoesNotContain(diReport.CaptiveDependencies, c => c.ConsumerTypeId == "DiSample.SafeSingleton");

            // Single-implementation interfaces: IDbContext and IValidator (NOT IMulti which has 2 impls)
            Assert.Contains(diReport.SingleImplementationInterfaces, s =>
                s.InterfaceTypeId == "DiSample.IDbContext" && s.ImplementationTypeId == "DiSample.AppDbContext" && s.IsDiRegistered && s.ConsumerCount >= 2);
            Assert.Contains(diReport.SingleImplementationInterfaces, s =>
                s.InterfaceTypeId == "DiSample.IValidator" && s.ImplementationTypeId == "DiSample.OrderValidator" && s.IsDiRegistered);
            Assert.DoesNotContain(diReport.SingleImplementationInterfaces, s => s.InterfaceTypeId == "DiSample.IMulti");

            // YAML rule no_captive_dependencies catches both captive dependencies
            var yaml = """
                version: 1
                rules:
                  - id: DI-001
                    name: No captive dependencies
                    severity: error
                    type: no_captive_dependencies
                """;
            var rules = new YamlRuleSetProvider().ParseYaml(yaml);
            var checkReport = analyzer.CheckRules(snapshot.Graph, rules);
            Assert.Equal(2, checkReport.ErrorCount);

            // Dead code on executable: UnusedHelper is dead, DI-registered types are live
            var deadReport = analyzer.AnalyzeDeadCode(snapshot.Graph, NodeKind.Type, treatPublicApiAsEntryPoint: true);
            Assert.Contains(deadReport.Candidates, c => c.NodeId == "DiSample.UnusedHelper");
            Assert.DoesNotContain(deadReport.Candidates, c => c.NodeId == "DiSample.AppDbContext");
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public void SarifExporter_GeneratesValidSarif210WithViolationsAndAnalyzerDiagnostics()
    {
        var violations = new[]
        {
            new RuleViolation("ARCH-001", "No Domain->Infra", RuleSeverity.Error, "Forbidden edge",
                NodeId: "App.Domain.Order", Location: new SourceLocation("src\\Domain\\Order.cs", 14, 9)),
            new RuleViolation("ARCH-005", "Complexity limit", RuleSeverity.Warning, "CC is 18",
                NodeId: "App.Service", Location: new SourceLocation("src/Service.cs", 42, 1))
        };

        var analyzerDiags = new[]
        {
            new AnalyzerDiagnostic("CA1822", "Mark members as static", "Performance", RuleSeverity.Info,
                "Member 'Compute' does not access instance data", new SourceLocation("src/Calc.cs", 7, 16), "App")
        };

        var sarifJson = SarifExporter.Export(violations, analyzerDiags, toolVersion: "0.3.0");
        using var doc = JsonDocument.Parse(sarifJson);
        var root = doc.RootElement;

        Assert.Equal("2.1.0", root.GetProperty("version").GetString());
        Assert.Equal("https://json.schemastore.org/sarif-2.1.0.json", root.GetProperty("$schema").GetString());

        var run = root.GetProperty("runs")[0];
        var driver = run.GetProperty("tool").GetProperty("driver");
        Assert.Equal("ArchLens", driver.GetProperty("name").GetString());
        Assert.Equal("0.3.0", driver.GetProperty("version").GetString());
        Assert.Equal(3, driver.GetProperty("rules").GetArrayLength());

        var results = run.GetProperty("results");
        Assert.Equal(3, results.GetArrayLength());
        Assert.Equal("error", results[0].GetProperty("level").GetString());
        Assert.Equal("src/Domain/Order.cs", results[0].GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString());
        Assert.Equal(14, results[0].GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("region").GetProperty("startLine").GetInt32());
        Assert.Equal("warning", results[1].GetProperty("level").GetString());
        Assert.Equal("note", results[2].GetProperty("level").GetString());
        Assert.Equal("CA1822", results[2].GetProperty("ruleId").GetString());
    }

    [Fact]
    public async Task RoslynExtractor_AggregatesNetAnalyzerDiagnosticsByCategory()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "AnalyzerDemo.csproj", """
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
                namespace AnalyzerDemo;

                public class PerfSample
                {
                    // Triggers CA1822 (Performance: Mark members as static) and CA1825 (Avoid zero-length array allocations)
                    public int Compute(int x)
                    {
                        var empty = new int[0];
                        return x + empty.Length;
                    }
                }
                """);

            var csprojPath = Path.Combine(tempDir, "AnalyzerDemo.csproj");
            var extractor = new RoslynCodeGraphExtractor();
            var snapshot = await extractor.ExtractAsync(
                csprojPath,
                new ExtractionOptions(IncludeAnalyzerDiagnostics: true, AnalyzerCategories: ["Design", "Maintainability", "Performance"]));

            Assert.NotEmpty(snapshot.Graph.AnalyzerDiagnostics);
            Assert.All(snapshot.Graph.AnalyzerDiagnostics, d =>
                Assert.Contains(d.Category, new[] { "Design", "Maintainability", "Performance" }, StringComparer.OrdinalIgnoreCase));
            Assert.Contains(snapshot.Graph.AnalyzerDiagnostics, d => d.Id is "CA1822" or "CA1825");

            var analyzer = new ArchitectureAnalyzerService();
            var checkReport = analyzer.CheckRules(snapshot.Graph, [], maxViolations: 100, includeAnalyzerDiagnostics: true);
            Assert.True(checkReport.TotalViolations > 0);
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public void BaselineStore_SavesLoadsAndDiffsAcrossDiskPersistence()
    {
        var tempDir = CreateTempDir();
        try
        {
            var nodes = new List<CodeNode>
            {
                new("App.Domain.Order", "Order", "App.Domain.Order", NodeKind.Type, "App", "App.Domain", TypeKind.Class),
                new("App.Infra.Repo", "Repo", "App.Infra.Repo", NodeKind.Type, "App", "App.Infra", TypeKind.Class)
            };
            var baselineGraph = new CodeGraph(
                tempDir, nodes,
                edges: [new("App.Infra.Repo", "App.Domain.Order", DependencyKind.MethodCall)],
                projects: [new("App", "App.csproj", "net10.0", [], [], 2, 2)],
                diRegistrations: [new("App.Infra.Repo", "App.Infra.Repo", ServiceLifetimeKind.Scoped)]);

            var baselineSnap = new ArchitectureSnapshot("snap-base", Path.Combine(tempDir, "App.csproj"), DateTimeOffset.UtcNow.AddMinutes(-5), baselineGraph);
            var baselinePath = Path.Combine(tempDir, BaselineStore.DefaultFileName);

            BaselineStore.Save(baselinePath, baselineSnap);
            Assert.Equal(baselinePath, BaselineStore.DiscoverBaselineFile(tempDir));

            var loaded = BaselineStore.Load(baselinePath);
            Assert.Equal("snap-base", loaded.SnapshotId);
            Assert.Equal(2, loaded.Graph.GetNodes(NodeKind.Type).Count());
            Assert.Single(loaded.Graph.Edges);
            Assert.Single(loaded.Graph.DiRegistrations);

            // Simulate PR introducing a forbidden reverse edge Domain -> Infra (also creates a cycle!)
            var prGraph = new CodeGraph(
                tempDir, nodes,
                edges:
                [
                    new("App.Infra.Repo", "App.Domain.Order", DependencyKind.MethodCall),
                    new("App.Domain.Order", "App.Infra.Repo", DependencyKind.TypeReference, new SourceLocation("Order.cs", 12, 5))
                ]);
            var prSnap = new ArchitectureSnapshot("snap-pr", baselineSnap.TargetPath, DateTimeOffset.UtcNow, prGraph);

            var rules = new IArchitectureRule[]
            {
                new ForbidDependencyRule("ARCH-001", "No Domain->Infra", "", RuleSeverity.Error, "App.Domain.*", ["App.Infra.*"])
            };

            var diff = new ArchitectureAnalyzerService().DiffSnapshots(loaded, prSnap, rules);
            Assert.Equal(1, diff.CycleCountDelta);
            Assert.Single(diff.AddedCycles);
            Assert.Equal(1, diff.ViolationCountDelta);
            Assert.Single(diff.NewViolations);
            Assert.Equal("ARCH-001", diff.NewViolations[0].RuleId);

            // Invalid/empty baseline JSON must throw InvalidDataException
            var invalidPath = Path.Combine(tempDir, "invalid.json");
            File.WriteAllText(invalidPath, "{}");
            Assert.Throws<InvalidDataException>(() => BaselineStore.Load(invalidPath));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public async Task RoslynDiAndDeadCode_EdgeCases_MultiGenericClosedGenericParameterlessSingletonXunitAndPropertyHelpers()
    {
        var tempDir = CreateTempDir();
        try
        {
            WriteFile(tempDir, "EdgeApp.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <OutputType>Exe</OutputType>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <Nullable>enable</Nullable>
                  </PropertyGroup>
                </Project>
                """);

            WriteFile(tempDir, "Stubs.cs", """
                namespace Microsoft.Extensions.DependencyInjection
                {
                    public interface IServiceCollection { }
                    public static class ServiceCollectionExtensions
                    {
                        public static IServiceCollection AddSingleton<TService, TImpl>(this IServiceCollection s) where TImpl : TService => s;
                        public static IServiceCollection AddSingleton<TService>(this IServiceCollection s) => s;
                        public static IServiceCollection AddSingleton(this IServiceCollection s, Type serviceType, Func<IServiceProvider, object> factory) => s;
                        public static IServiceCollection AddScoped<TService, TImpl>(this IServiceCollection s) where TImpl : TService => s;
                        public static IServiceCollection AddScoped<TService>(this IServiceCollection s) => s;
                        public static IServiceCollection AddHostedService<THosted>(this IServiceCollection s) => s;
                    }
                }
                namespace Xunit { public sealed class FactAttribute : Attribute { } }
                namespace MiniWeb
                {
                    public sealed class App { public void MapGet(string p, Delegate handler) { } }
                }
                """);

            WriteFile(tempDir, "Domain.cs", """
                using Microsoft.Extensions.DependencyInjection;
                using Xunit;

                namespace EdgeApp;

                public interface ICache<TKey, TValue> { }
                public class ScopedCache : ICache<string, int> { }

                public interface IRepo<T> { }
                public class OrderEntity { }
                public class ConfigEntity { }
                public class ScopedOrderRepo : IRepo<OrderEntity> { }
                public class SingletonConfigRepo : IRepo<ConfigEntity> { }

                // Multi-param generic captive dependency (comma inside <string, int> must not break parsing!)
                public class MultiGenericConsumer(ICache<string, int> cache) { public object C => cache; }

                // Safe: injects Singleton IRepo<ConfigEntity>, NOT Scoped IRepo<OrderEntity>
                public class SafeClosedGenericSingleton(IRepo<ConfigEntity> cfgRepo) { public object R => cfgRepo; }

                // Safe: parameterless constructor Singleton that only takes Scoped in a regular method parameter!
                public class ParameterlessSingleton
                {
                    public ParameterlessSingleton() { }
                    public void Process(IRepo<OrderEntity> repo) { }
                }

                // Non-generic typeof + factory registration target
                public interface IFactorySvc { }
                public class FactoryCaptiveImpl(IRepo<OrderEntity> repo) : IFactorySvc { public object R => repo; }

                // Abstract base implementing interface -> ConcreteRepo must be detected as single implementation
                public interface IAbstractedRepo { }
                public abstract class BaseRepo : IAbstractedRepo { }
                public class ConcreteRepo : BaseRepo { }

                // xUnit test class without class-level attribute (only [Fact] on method)
                internal class SampleFacts
                {
                    [Fact]
                    public void Works() { }
                }

                // Method-level dead code: property helper must be live, orphan private method must be dead
                public class WorkerWithProps
                {
                    public int Computed => ComputeFromProp();
                    private int ComputeFromProp() => 42;
                    private int TotallyDeadMethod() => 0;
                }

                public static class MinimalHandlers
                {
                    public static string HandleHealth() => "ok";
                }
                """);

            WriteFile(tempDir, "Program.cs", """
                using EdgeApp;
                using Microsoft.Extensions.DependencyInjection;
                using MiniWeb;

                IServiceCollection services = null!;
                services.AddScoped<ICache<string, int>, ScopedCache>();
                services.AddScoped<IRepo<OrderEntity>, ScopedOrderRepo>();
                services.AddSingleton<IRepo<ConfigEntity>, SingletonConfigRepo>();
                services.AddSingleton<MultiGenericConsumer>();
                services.AddSingleton<SafeClosedGenericSingleton>();
                services.AddSingleton<ParameterlessSingleton>();
                services.AddSingleton(typeof(IFactorySvc), sp => new FactoryCaptiveImpl(null!));
                services.AddSingleton<WorkerWithProps>();

                var app = new App();
                app.MapGet("/health", MinimalHandlers.HandleHealth);
                """);

            var csprojPath = Path.Combine(tempDir, "EdgeApp.csproj");
            var extractor = new RoslynCodeGraphExtractor();
            var analyzer = new ArchitectureAnalyzerService();

            // 1. Default scope (IncludeMethods: false) -> xUnit SampleFacts must STILL be recognized as entry point!
            var typeSnap = await extractor.ExtractAsync(csprojPath, new ExtractionOptions(IncludeMethods: false));
            var diReport = analyzer.AnalyzeDi(typeSnap.Graph);

            // MultiGenericConsumer and FactoryCaptiveImpl MUST be flagged as captive!
            Assert.Contains(diReport.CaptiveDependencies, c => c.ConsumerTypeId == "EdgeApp.MultiGenericConsumer");
            Assert.Contains(diReport.CaptiveDependencies, c => c.ConsumerTypeId == "EdgeApp.FactoryCaptiveImpl");
            // SafeClosedGenericSingleton and ParameterlessSingleton MUST NOT be flagged!
            Assert.DoesNotContain(diReport.CaptiveDependencies, c => c.ConsumerTypeId == "EdgeApp.SafeClosedGenericSingleton");
            Assert.DoesNotContain(diReport.CaptiveDependencies, c => c.ConsumerTypeId == "EdgeApp.ParameterlessSingleton");

            // ConcreteRepo inheriting IAbstractedRepo via BaseRepo MUST be found as single-implementation interface
            Assert.Contains(diReport.SingleImplementationInterfaces, s =>
                s.InterfaceTypeId == "EdgeApp.IAbstractedRepo" && s.ImplementationTypeId == "EdgeApp.ConcreteRepo");

            // Dead code at type scope: SampleFacts (xUnit) & MinimalHandlers (MapGet) & FactoryCaptiveImpl (DI) are live!
            var typeDead = analyzer.AnalyzeDeadCode(typeSnap.Graph, NodeKind.Type, treatPublicApiAsEntryPoint: false);
            Assert.DoesNotContain(typeDead.Candidates, c => c.NodeId == "EdgeApp.SampleFacts");
            Assert.DoesNotContain(typeDead.Candidates, c => c.NodeId == "EdgeApp.MinimalHandlers");
            Assert.DoesNotContain(typeDead.Candidates, c => c.NodeId == "EdgeApp.FactoryCaptiveImpl");

            // 2. Method scope: ComputeFromProp (called by property getter) is live; TotallyDeadMethod is dead!
            var methodSnap = await extractor.ExtractAsync(csprojPath, new ExtractionOptions(IncludeMethods: true));
            var methodDead = analyzer.AnalyzeDeadCode(methodSnap.Graph, NodeKind.Method, treatPublicApiAsEntryPoint: false);
            Assert.DoesNotContain(methodDead.Candidates, c => c.NodeId.Contains("ComputeFromProp"));
            Assert.DoesNotContain(methodDead.Candidates, c => c.NodeId.Contains("HandleHealth"));
            Assert.Contains(methodDead.Candidates, c => c.NodeId.Contains("TotallyDeadMethod"));
        }
        finally
        {
            TryDeleteDir(tempDir);
        }
    }

    [Fact]
    public void SarifExporter_EncodesRfc3986UrisEmitsRuleIndexAndHandlesRootedPaths()
    {
        var violations = new[]
        {
            new RuleViolation("R-B", "Rule B", RuleSeverity.Warning, "Msg B", Location: new SourceLocation("src/My Folder/File #1.cs", 5, 2)),
            new RuleViolation("R-A", "Rule A", RuleSeverity.Error, "Msg A", Location: new SourceLocation(Path.GetFullPath("Test File.cs"), 10, 1))
        };

        var json = SarifExporter.Export(violations);
        using var doc = JsonDocument.Parse(json);
        var run = doc.RootElement.GetProperty("runs")[0];
        var rules = run.GetProperty("tool").GetProperty("driver").GetProperty("rules");
        var results = run.GetProperty("results");

        // Rules are sorted alphabetically: R-A = index 0, R-B = index 1
        Assert.Equal("R-A", rules[0].GetProperty("id").GetString());
        Assert.Equal("R-B", rules[1].GetProperty("id").GetString());
        Assert.Equal(1, results[0].GetProperty("ruleIndex").GetInt32());
        Assert.Equal(0, results[1].GetProperty("ruleIndex").GetInt32());

        var relUri = results[0].GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString();
        Assert.Equal("src/My%20Folder/File%20%231.cs", relUri);

        var absUri = results[1].GetProperty("locations")[0].GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString();
        Assert.StartsWith("file:///", absUri);
        Assert.Contains("Test%20File.cs", absUri);
    }

    [Fact]
    public void SnapshotDiff_DoesNotFlagImprovedMetricViolationAsNewViolation()
    {
        var baseNode = new CodeNode("App.Big", "Big", "App.Big", NodeKind.Type, "App", "App", TypeKind.Class,
            CyclomaticComplexity: 20, Metadata: new Dictionary<string, string> { ["maxMethodComplexity"] = "20" });
        var improvedNode = baseNode with
        {
            CyclomaticComplexity = 17,
            Metadata = new Dictionary<string, string> { ["maxMethodComplexity"] = "17" }
        };

        var baseSnap = new ArchitectureSnapshot("s1", "App.csproj", DateTimeOffset.UtcNow.AddMinutes(-1), new CodeGraph("C:/", [baseNode], []));
        var currSnap = new ArchitectureSnapshot("s2", "App.csproj", DateTimeOffset.UtcNow, new CodeGraph("C:/", [improvedNode], []));
        var rules = new IArchitectureRule[] { new MaxMetricRule("M-1", "CC", "", RuleSeverity.Warning, "cyclomatic_complexity", 15) };

        var diff = new ArchitectureAnalyzerService().DiffSnapshots(baseSnap, currSnap, rules);
        Assert.Equal(0, diff.ViolationCountDelta);
        Assert.Empty(diff.NewViolations);
        Assert.Empty(diff.ResolvedViolations);
    }

    private sealed class CustomPrefixDetector(string name) : IEntryPointDetector
    {
        public string Name => "CustomPrefix";
        public bool IsEntryPoint(CodeNode node, CodeGraph graph) => node.Name == name;
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "archlens-m2-" + Guid.NewGuid().ToString("N")[..8]);
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
