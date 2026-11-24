using ArchLens.Analysis;
using ArchLens.Extraction.Roslyn;
using ArchLens.Model;
using ArchLens.Rules.Yaml;

namespace ArchLens.Tests;

public sealed class RoslynSelfAnalysisTests
{
    [Fact]
    public async Task RoslynExtractor_AnalyzesArchLensSolutionItselfWithZeroRuleErrors()
    {
        var repoRoot = FindRepoRoot();
        var slnxPath = Path.Combine(repoRoot, "ArchLens.slnx");
        Assert.True(File.Exists(slnxPath), $"Expected solution at {slnxPath}");

        using var extractor = new RoslynCodeGraphExtractor(keepWorkspacesWarm: false);
        var snapshot = await extractor.ExtractAsync(slnxPath, new ExtractionOptions(IncludeTestProjects: false));

        Assert.True(snapshot.Graph.Projects.Count >= 6);
        Assert.True(snapshot.Graph.GetNodes(NodeKind.Type).Count() >= 15);
        Assert.True(snapshot.Graph.Edges.Count > 0);

        var rulesPath = YamlRuleSetProvider.DiscoverConfigFile(repoRoot);
        Assert.NotNull(rulesPath);

        var rules = new YamlRuleSetProvider().LoadRules(rulesPath);
        var analyzer = new ArchitectureAnalyzerService();
        var report = analyzer.CheckRules(snapshot.Graph, rules);

        Assert.Equal(0, report.ErrorCount);
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

        throw new DirectoryNotFoundException("Could not locate ArchLens.slnx above test binary directory.");
    }
}
