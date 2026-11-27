using System.Text.Json;
using System.Text.Json.Serialization;
using ArchLens.Model;

namespace ArchLens.Analysis;

/// <summary>
/// Persists and loads ArchitectureSnapshots to/from .archlens-baseline.json on disk
/// so branches and pull requests can be compared against a saved baseline across sessions and CI runs.
/// </summary>
public static class BaselineStore
{
    public const string DefaultFileName = ".archlens-baseline.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static void Save(string filePath, ArchitectureSnapshot snapshot)
    {
        var fullPath = Path.GetFullPath(filePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var dto = new BaselineDocument(
            Version: 1,
            SnapshotId: snapshot.SnapshotId,
            TargetPath: snapshot.TargetPath,
            CapturedAt: snapshot.CapturedAt,
            RootPath: snapshot.Graph.RootPath,
            Nodes: snapshot.Graph.Nodes.ToList(),
            Edges: snapshot.Graph.Edges.ToList(),
            Projects: snapshot.Graph.Projects.ToList(),
            DiRegistrations: snapshot.Graph.DiRegistrations.ToList(),
            AnalyzerDiagnostics: snapshot.Graph.AnalyzerDiagnostics.ToList(),
            LoadStatus: snapshot.Graph.LoadStatus,
            ExcludedTestProjectCount: snapshot.Graph.ExcludedTestProjectCount,
            MethodsIncluded: snapshot.Graph.MethodsIncluded);

        var json = JsonSerializer.Serialize(dto, JsonOptions);
        File.WriteAllText(fullPath, json);
    }

    public static ArchitectureSnapshot Load(string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Baseline file not found: '{fullPath}'.", fullPath);
        }

        var json = File.ReadAllText(fullPath);
        BaselineDocument dto;
        try
        {
            dto = JsonSerializer.Deserialize<BaselineDocument>(json, JsonOptions)
                ?? throw new InvalidDataException($"Baseline file '{fullPath}' is empty or invalid.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid baseline JSON in '{fullPath}': {ex.Message}", ex);
        }

        if (dto.Version != 1)
        {
            throw new InvalidDataException($"Unsupported baseline version '{dto.Version}' in '{fullPath}' (expected version 1).");
        }

        if (string.IsNullOrWhiteSpace(dto.SnapshotId) || string.IsNullOrWhiteSpace(dto.TargetPath))
        {
            throw new InvalidDataException($"Baseline file '{fullPath}' is missing required fields (snapshotId, targetPath).");
        }

        if (dto.Nodes is null || dto.Edges is null || dto.Projects is null)
        {
            throw new InvalidDataException($"Baseline file '{fullPath}' is missing required graph fields (nodes, edges, projects).");
        }

        if (dto.Nodes.Any(n => n is null) ||
            dto.Edges.Any(e => e is null) ||
            dto.Projects.Any(p => p is null) ||
            (dto.DiRegistrations is not null && dto.DiRegistrations.Any(r => r is null)) ||
            (dto.AnalyzerDiagnostics is not null && dto.AnalyzerDiagnostics.Any(d => d is null)))
        {
            throw new InvalidDataException($"Invalid baseline JSON in '{fullPath}': graph arrays must not contain null entries.");
        }

        var graph = new CodeGraph(
            rootPath: dto.RootPath ?? string.Empty,
            nodes: dto.Nodes,
            edges: dto.Edges,
            projects: dto.Projects,
            diagnostics: [],
            diRegistrations: dto.DiRegistrations ?? [],
            analyzerDiagnostics: dto.AnalyzerDiagnostics ?? [],
            loadStatus: dto.LoadStatus,
            excludedTestProjectCount: dto.ExcludedTestProjectCount,
            methodsIncluded: dto.MethodsIncluded);

        return new ArchitectureSnapshot(
            SnapshotId: dto.SnapshotId,
            TargetPath: dto.TargetPath,
            CapturedAt: dto.CapturedAt,
            Graph: graph);
    }

    public static string? DiscoverBaselineFile(string targetPath)
    {
        var dir = Directory.Exists(targetPath)
            ? Path.GetFullPath(targetPath)
            : Path.GetDirectoryName(Path.GetFullPath(targetPath));

        while (!string.IsNullOrWhiteSpace(dir))
        {
            var candidate = Path.Combine(dir, DefaultFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
            {
                break;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }

    public static string? GetScopeMismatchWarning(ArchitectureSnapshot baseline, ArchitectureSnapshot current)
    {
        var warnings = new List<string>();
        var baseProjects = baseline.Graph.Projects.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var currProjects = current.Graph.Projects.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (baseProjects.Count > 0 && currProjects.Count > 0 && !baseProjects.Overlaps(currProjects))
        {
            warnings.Add($"baseline '{baseline.SnapshotId}' (target: '{baseline.TargetPath}') has no projects in common with current target '{current.TargetPath}'");
        }
        else if (!string.Equals(baseline.TargetPath, current.TargetPath, StringComparison.OrdinalIgnoreCase) &&
                 !baseProjects.SetEquals(currProjects))
        {
            warnings.Add($"target scope differs (baseline target '{baseline.TargetPath}' covers {baseProjects.Count} project(s), current target '{current.TargetPath}' covers {currProjects.Count} project(s))");
        }

        var baseHasTests = baseline.Graph.GetNodes(NodeKind.Project)
            .Any(p => EntryPointDetectors.HasMetadataFlag(p, "isTestProject") || EntryPointDetectors.HasMetadataFlag(p, "isTestSupportProject"));
        var currHasTests = current.Graph.GetNodes(NodeKind.Project)
            .Any(p => EntryPointDetectors.HasMetadataFlag(p, "isTestProject") || EntryPointDetectors.HasMetadataFlag(p, "isTestSupportProject"));
        if (baseHasTests != currHasTests ||
            (baseline.Graph.ExcludedTestProjectCount > 0 && current.Graph.ExcludedTestProjectCount == 0 && currHasTests) ||
            (current.Graph.ExcludedTestProjectCount > 0 && baseline.Graph.ExcludedTestProjectCount == 0 && baseHasTests))
        {
            warnings.Add($"test-project inclusion differs (baseline includeTests={baseHasTests.ToString().ToLowerInvariant()}, current includeTests={currHasTests.ToString().ToLowerInvariant()})");
        }

        if (baseline.Graph.MethodsIncluded != current.Graph.MethodsIncluded)
        {
            warnings.Add($"method-level extraction differs (baseline includeMethods={baseline.Graph.MethodsIncluded.ToString().ToLowerInvariant()}, current includeMethods={current.Graph.MethodsIncluded.ToString().ToLowerInvariant()})");
        }

        var baseBackend = baseline.Graph.LoadStatus?.Backend;
        var currBackend = current.Graph.LoadStatus?.Backend;
        if (!string.IsNullOrWhiteSpace(baseBackend) && !string.IsNullOrWhiteSpace(currBackend) &&
            !string.Equals(baseBackend, currBackend, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"extraction backend differs (baseline={baseBackend}, current={currBackend})");
        }

        if (baseline.Graph.LoadStatus is { Degraded: true } baseDegraded)
        {
            warnings.Add($"baseline '{baseline.SnapshotId}' was captured from a degraded workspace ({baseDegraded.ProjectsLoaded}/{baseDegraded.ProjectsExpected} projects loaded cleanly)");
        }
        if (current.Graph.LoadStatus is { Degraded: true } currDegraded)
        {
            warnings.Add($"current snapshot was captured from a degraded workspace ({currDegraded.ProjectsLoaded}/{currDegraded.ProjectsExpected} projects loaded cleanly)");
        }

        var baseTfms = baseline.Graph.Projects
            .Where(p => !string.IsNullOrWhiteSpace(p.TargetFramework))
            .ToDictionary(p => p.Name, p => p.TargetFramework!, StringComparer.OrdinalIgnoreCase);
        var tfmMismatches = current.Graph.Projects
            .Where(p => !string.IsNullOrWhiteSpace(p.TargetFramework) &&
                        baseTfms.TryGetValue(p.Name, out var bTfm) &&
                        !string.Equals(bTfm, p.TargetFramework, StringComparison.OrdinalIgnoreCase))
            .Select(p => $"{p.Name}: {baseTfms[p.Name]} vs {p.TargetFramework}")
            .Take(3)
            .ToList();
        if (tfmMismatches.Count > 0)
        {
            warnings.Add($"targetFramework differs ({string.Join(", ", tfmMismatches)})");
        }

        return warnings.Count > 0
            ? $"Warning: {string.Join("; ", warnings)}; deltas may not be directly comparable."
            : null;
    }

    private sealed record BaselineDocument(
        int Version,
        string SnapshotId,
        string TargetPath,
        DateTimeOffset CapturedAt,
        string? RootPath,
        List<CodeNode>? Nodes,
        List<DependencyEdge>? Edges,
        List<ProjectInfo>? Projects,
        List<DiRegistration>? DiRegistrations,
        List<AnalyzerDiagnostic>? AnalyzerDiagnostics,
        GraphLoadStatus? LoadStatus = null,
        int ExcludedTestProjectCount = 0,
        bool MethodsIncluded = false);
}
