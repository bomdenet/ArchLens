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
        if (File.Exists(fullPath) && !IsSafeToOverwriteBaselineFile(fullPath))
        {
            throw new InvalidOperationException(
                $"Refusing to overwrite existing non-baseline file '{fullPath}'. Choose a new file path or an existing ArchLens baseline JSON file.");
        }

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
        ValidateRawBaselineJson(json, fullPath, rejectExtraTopLevelKeys: false);
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

        if (dto.CapturedAt == default)
        {
            throw new InvalidDataException($"Baseline file '{fullPath}' is missing required field 'capturedAt'.");
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

        if (dto.Projects.Any(p => string.IsNullOrWhiteSpace(p.Name)))
        {
            throw new InvalidDataException($"Invalid baseline JSON in '{fullPath}': every project entry must define a non-empty 'name'.");
        }

        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in dto.Nodes)
        {
            if (string.IsNullOrWhiteSpace(n.Id) || string.IsNullOrWhiteSpace(n.Name) || string.IsNullOrWhiteSpace(n.FullName) || n.ProjectName is null)
            {
                throw new InvalidDataException($"Invalid baseline JSON in '{fullPath}': every node entry must define non-empty 'id', 'name', 'fullName', and 'projectName'.");
            }
            if (!Enum.IsDefined(n.Kind) || (n.TypeKind.HasValue && !Enum.IsDefined(n.TypeKind.Value)) || !Enum.IsDefined(n.Accessibility))
            {
                throw new InvalidDataException($"Invalid baseline JSON in '{fullPath}': node '{n.Id}' contains an undefined enum value for 'kind', 'typeKind', or 'accessibility'.");
            }
            nodeIds.Add(n.Id);
        }

        foreach (var e in dto.Edges)
        {
            if (string.IsNullOrWhiteSpace(e.FromId) || string.IsNullOrWhiteSpace(e.ToId))
            {
                throw new InvalidDataException($"Invalid baseline JSON in '{fullPath}': every edge entry must define non-empty 'fromId' and 'toId'.");
            }
            if (!Enum.IsDefined(e.Kind))
            {
                throw new InvalidDataException($"Invalid baseline JSON in '{fullPath}': edge '{e.FromId}' -> '{e.ToId}' contains an undefined 'kind' value.");
            }
            if (!nodeIds.Contains(e.FromId) || !nodeIds.Contains(e.ToId))
            {
                throw new InvalidDataException($"Invalid baseline JSON in '{fullPath}': edge '{e.FromId}' -> '{e.ToId}' references a node ID not present in 'nodes'.");
            }
        }

        if (dto.DiRegistrations is not null &&
            dto.DiRegistrations.Any(r => string.IsNullOrWhiteSpace(r.ServiceTypeId) || string.IsNullOrWhiteSpace(r.ImplementationTypeId)))
        {
            throw new InvalidDataException($"Invalid baseline JSON in '{fullPath}': every diRegistration entry must define non-empty 'serviceTypeId' and 'implementationTypeId'.");
        }

        if (dto.AnalyzerDiagnostics is not null &&
            dto.AnalyzerDiagnostics.Any(d => string.IsNullOrWhiteSpace(d.Id) || d.Message is null))
        {
            throw new InvalidDataException($"Invalid baseline JSON in '{fullPath}': every analyzerDiagnostic entry must define non-empty 'id' and 'message'.");
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

    private static readonly HashSet<string> AllowedBaselineTopLevelKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "version", "snapshotId", "targetPath", "capturedAt", "rootPath",
        "nodes", "edges", "projects", "diRegistrations", "analyzerDiagnostics",
        "loadStatus", "excludedTestProjectCount", "methodsIncluded"
    };

    private static void ValidateRawBaselineJson(string json, string fullPath, bool rejectExtraTopLevelKeys)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid baseline JSON in '{fullPath}': {ex.Message}", ex);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException($"Invalid baseline JSON in '{fullPath}': root element must be a JSON object.");
            }

            if (rejectExtraTopLevelKeys)
            {
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (!AllowedBaselineTopLevelKeys.Contains(prop.Name))
                    {
                        throw new InvalidDataException($"File '{fullPath}' contains non-baseline property '{prop.Name}'.");
                    }
                }
            }

            if (TryGetPropertyCaseInsensitive(doc.RootElement, "nodes", out var nodesEl) && nodesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in nodesEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && !TryGetPropertyCaseInsensitive(item, "kind", out _))
                    {
                        throw new InvalidDataException($"Invalid baseline JSON in '{fullPath}': every node entry must explicitly specify 'kind'.");
                    }
                }
            }

            if (TryGetPropertyCaseInsensitive(doc.RootElement, "edges", out var edgesEl) && edgesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in edgesEl.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && !TryGetPropertyCaseInsensitive(item, "kind", out _))
                    {
                        throw new InvalidDataException($"Invalid baseline JSON in '{fullPath}': every edge entry must explicitly specify 'kind'.");
                    }
                }
            }
        }
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
        {
            return true;
        }
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    private static bool IsSafeToOverwriteBaselineFile(string fullPath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (info.Length == 0)
            {
                return true;
            }

            var json = File.ReadAllText(fullPath);
            ValidateRawBaselineJson(json, fullPath, rejectExtraTopLevelKeys: true);
            _ = Load(fullPath);
            return true;
        }
        catch
        {
            return false;
        }
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
