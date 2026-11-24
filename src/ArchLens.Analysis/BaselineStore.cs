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
            AnalyzerDiagnostics: snapshot.Graph.AnalyzerDiagnostics.ToList());

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
        var dto = JsonSerializer.Deserialize<BaselineDocument>(json, JsonOptions)
            ?? throw new InvalidDataException($"Baseline file '{fullPath}' is empty or invalid.");

        if (dto.Version != 1)
        {
            throw new InvalidDataException($"Unsupported baseline version '{dto.Version}' in '{fullPath}' (expected version 1).");
        }

        if (string.IsNullOrWhiteSpace(dto.SnapshotId) || string.IsNullOrWhiteSpace(dto.TargetPath))
        {
            throw new InvalidDataException($"Baseline file '{fullPath}' is missing required fields (snapshotId, targetPath).");
        }

        var graph = new CodeGraph(
            rootPath: dto.RootPath ?? string.Empty,
            nodes: dto.Nodes ?? [],
            edges: dto.Edges ?? [],
            projects: dto.Projects ?? [],
            diagnostics: [],
            diRegistrations: dto.DiRegistrations ?? [],
            analyzerDiagnostics: dto.AnalyzerDiagnostics ?? []);

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

        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        var candidate = Path.Combine(dir, DefaultFileName);
        return File.Exists(candidate) ? candidate : null;
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
        List<AnalyzerDiagnostic>? AnalyzerDiagnostics);
}
