using System.Collections.Concurrent;
using System.Text;
using System.Xml.Linq;
using ArchLens.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using ModelProjectInfo = ArchLens.Model.ProjectInfo;
using ModelTypeKind = ArchLens.Model.TypeKind;

namespace ArchLens.Extraction.Roslyn;

/// <summary>
/// Extracts a canonical CodeGraph from a C# .slnx, .sln, .csproj, or directory using Roslyn semantic analysis.
/// Supports multi-targeted TFM selection, optional method-level call graphs, cognitive complexity, LCOM4,
/// and warm in-place Roslyn Document incremental recompilation (updating only changed Documents in MSBuildWorkspace).
/// </summary>
public sealed class RoslynCodeGraphExtractor : IIncrementalCodeGraphExtractor, IDisposable
{
    private readonly bool _keepWorkspacesWarm;
    private readonly ConcurrentDictionary<string, WarmWorkspaceSession> _warmSessions = new(StringComparer.OrdinalIgnoreCase);
    private int _disposed;

    public RoslynCodeGraphExtractor(bool keepWorkspacesWarm = true)
    {
        _keepWorkspacesWarm = keepWorkspacesWarm;
    }

    public string Name => "Roslyn";

    public bool CanHandle(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return false;
        }

        if (Directory.Exists(targetPath))
        {
            // Only handle directories containing source solution/project files so binary .dll dirs route to IL extractor
            return Directory.GetFiles(targetPath, "*.slnx", SearchOption.TopDirectoryOnly).Length > 0 ||
                   Directory.GetFiles(targetPath, "*.sln", SearchOption.TopDirectoryOnly).Length > 0 ||
                   Directory.GetFiles(targetPath, "*.csproj", SearchOption.TopDirectoryOnly).Length > 0;
        }

        if (!File.Exists(targetPath))
        {
            return false;
        }

        var ext = Path.GetExtension(targetPath);
        return ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ArchitectureSnapshot> ExtractAsync(
        string targetPath,
        ExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ExtractionOptions();
        MsBuildBootstrapper.EnsureRegistered();

        var resolvedTarget = ResolveTargetFile(targetPath);
        var rootDir = Path.GetDirectoryName(resolvedTarget) ?? Directory.GetCurrentDirectory();
        var diagnostics = new List<ExtractionDiagnostic>();
        var workspaces = new List<MSBuildWorkspace>();
        var keepWorkspacesAlive = false;

        try
        {
            var (projects, allWorkspaceProjects) = await LoadTargetProjectsAsync(
                resolvedTarget, options, diagnostics, workspaces, cancellationToken);

            var projectMetaByPath = ReadAllCsprojMetadata(allWorkspaceProjects);
            var (selectedProjects, excludedTestCount, testProjectsSet, testSupportProjectsSet) =
                SelectProjects(projects, projectMetaByPath, options, diagnostics);

            var knownTypeIds = new HashSet<string>(StringComparer.Ordinal);
            var knownMethodIds = new HashSet<string>(StringComparer.Ordinal);

            var compiled = await CompileAndCollectSymbolsAsync(
                selectedProjects, options, diagnostics, knownTypeIds, knownMethodIds, cancellationToken);

            var nodes = new List<CodeNode>();
            var edgeAggregator = new Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)>();
            var projectInfos = new List<ModelProjectInfo>();
            var namespacesSeen = new HashSet<string>(StringComparer.Ordinal);
            var diRegistrations = new List<DiRegistration>();
            var analyzerDiagnostics = new List<AnalyzerDiagnostic>();

            var (razorComponentCount, analyzersLoadedCount) = await ExtractGraphElementsAsync(
                compiled, projectMetaByPath, testProjectsSet, testSupportProjectsSet, options, rootDir,
                knownTypeIds, knownMethodIds, nodes, edgeAggregator, projectInfos, namespacesSeen,
                diRegistrations, analyzerDiagnostics, diagnostics, cancellationToken);

            var edges = edgeAggregator
                .Select(kvp => new DependencyEdge(kvp.Key.From, kvp.Key.To, kvp.Key.Kind, kvp.Value.Location, kvp.Value.Weight))
                .ToList();

            var loadStatus = BuildRoslynLoadStatus(selectedProjects, compiled, diagnostics, razorComponentCount);

            if (_keepWorkspacesWarm && Volatile.Read(ref _disposed) == 0 && workspaces.Count > 0)
            {
                var warmKey = NormalizeWarmKey(resolvedTarget, options);
                var session = new WarmWorkspaceSession(
                    workspaces,
                    workspaces.Select(w => w.CurrentSolution).ToList(),
                    projectMetaByPath);

                WarmWorkspaceSession? oldSession = null;
                _warmSessions.AddOrUpdate(
                    warmKey,
                    _ =>
                    {
                        oldSession = null;
                        return session;
                    },
                    (_, existing) =>
                    {
                        oldSession = existing;
                        return session;
                    });
                oldSession?.Release();

                if (Volatile.Read(ref _disposed) != 0 && _warmSessions.TryRemove(warmKey, out var removed))
                {
                    removed.Release();
                }
                else
                {
                    keepWorkspacesAlive = true;
                }
            }

            var graph = new CodeGraph(
                rootDir, nodes, edges, projectInfos, diagnostics, diRegistrations, analyzerDiagnostics,
                loadStatus: loadStatus,
                excludedTestProjectCount: excludedTestCount,
                methodsIncluded: options.IncludeMethods,
                analyzersLoadedCount: analyzersLoadedCount);
            return CreateSnapshot(resolvedTarget, graph);
        }
        finally
        {
            if (!keepWorkspacesAlive)
            {
                foreach (var ws in workspaces)
                {
                    ws.Dispose();
                }
            }
        }
    }

    public async Task<ArchitectureSnapshot> ExtractIncrementalAsync(
        string targetPath,
        ArchitectureSnapshot previousSnapshot,
        IReadOnlySet<string> changedFilePaths,
        ExtractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new ExtractionOptions();
        if (changedFilePaths.Count == 0)
        {
            return previousSnapshot;
        }

        MsBuildBootstrapper.EnsureRegistered();
        var resolvedTarget = ResolveTargetFile(targetPath);
        var rootDir = previousSnapshot.Graph.RootPath;
        var warmKey = NormalizeWarmKey(resolvedTarget, options);

        if (HasStructuralChange(changedFilePaths))
        {
            EvictWarmSession(warmKey);
            return await ExtractAsync(targetPath, options, cancellationToken);
        }

        var (dirtyProjectNames, hasUnmappedProductionChange) =
            FindDirtyProjectsWithDependents(previousSnapshot.Graph, rootDir, changedFilePaths, options.IncludeTestProjects);

        if (hasUnmappedProductionChange)
        {
            EvictWarmSession(warmKey);
            return await ExtractAsync(targetPath, options, cancellationToken);
        }

        if (dirtyProjectNames.Count == 0)
        {
            // All changes belonged exclusively to excluded test projects — snapshot is unchanged.
            return previousSnapshot;
        }

        // Fast path: update changed Documents directly inside the warm Roslyn Solution (ZERO MSBuild reload!)
        if (_warmSessions.TryGetValue(warmKey, out var warmSession) && warmSession.TryAcquire())
        {
            try
            {
                var warmResult = await ExtractFromWarmWorkspaceAsync(
                    resolvedTarget, rootDir, previousSnapshot, warmSession,
                    changedFilePaths, dirtyProjectNames, options, cancellationToken);
                if (warmResult is not null)
                {
                    return warmResult;
                }
            }
            finally
            {
                warmSession.Release();
            }
        }

        if (dirtyProjectNames.Count >= previousSnapshot.Graph.Projects.Count)
        {
            return await ExtractAsync(targetPath, options, cancellationToken);
        }

        var unaffectedProjects = previousSnapshot.Graph.Projects
            .Where(p => !dirtyProjectNames.Contains(p.Name))
            .ToList();
        var unaffectedProjectSet = unaffectedProjects.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var preservedNodes = previousSnapshot.Graph.Nodes
            .Where(n => n.Kind != NodeKind.Namespace && unaffectedProjectSet.Contains(n.ProjectName))
            .ToList();
        var preservedNodeIds = preservedNodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        var preservedEdges = previousSnapshot.Graph.Edges
            .Where(e => preservedNodeIds.Contains(e.FromId) && preservedNodeIds.Contains(e.ToId))
            .ToList();

        var dirtyCsprojPaths = previousSnapshot.Graph.Projects
            .Where(p => dirtyProjectNames.Contains(p.Name))
            .Select(p => Path.GetFullPath(Path.Combine(rootDir, p.FilePath)))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (dirtyCsprojPaths.Count == 0)
        {
            return await ExtractAsync(targetPath, options, cancellationToken);
        }

        var diagnostics = new List<ExtractionDiagnostic>
        {
            new(ExtractionDiagnosticSeverity.Info,
                $"Incremental re-extraction of {dirtyProjectNames.Count}/{previousSnapshot.Graph.Projects.Count} project(s): {string.Join(", ", dirtyProjectNames.OrderBy(x => x, StringComparer.Ordinal))}.",
                "IncrementalExtractor")
        };

        var workspaces = new List<MSBuildWorkspace>();
        try
        {
            var (loadedDirty, allWorkspaceProjects) = await OpenCsprojFilesAsync(
                dirtyCsprojPaths, options.TargetFramework, diagnostics, workspaces, cancellationToken);

            var projectMetaByPath = ReadAllCsprojMetadata(allWorkspaceProjects);
            var (selectedAllDirty, _, dirtyTestProjects, dirtyTestSupportProjects) =
                SelectProjects(loadedDirty, projectMetaByPath, options, diagnostics);
            var selectedDirty = selectedAllDirty
                .Where(p => dirtyProjectNames.Contains(p.NormalizedName))
                .ToList();

            var knownTypeIds = preservedNodes
                .Where(n => n.Kind == NodeKind.Type)
                .Select(n => n.Id)
                .ToHashSet(StringComparer.Ordinal);
            var knownMethodIds = preservedNodes
                .Where(n => n.Kind == NodeKind.Method)
                .Select(n => n.Id)
                .ToHashSet(StringComparer.Ordinal);

            var compiledDirty = await CompileAndCollectSymbolsAsync(
                selectedDirty, options, diagnostics, knownTypeIds, knownMethodIds, cancellationToken);

            var preservedDi = previousSnapshot.Graph.DiRegistrations
                .Where(r => r.ProjectName is not null
                    ? unaffectedProjectSet.Contains(r.ProjectName)
                    : r.RegisteredByTypeId is not null && preservedNodeIds.Contains(r.RegisteredByTypeId))
                .ToList();
            var preservedAnalyzerDiags = previousSnapshot.Graph.AnalyzerDiagnostics
                .Where(d => d.ProjectName is not null && unaffectedProjectSet.Contains(d.ProjectName))
                .ToList();

            var newNodes = new List<CodeNode>(preservedNodes);
            var edgeAggregator = new Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)>();
            var projectInfos = new List<ModelProjectInfo>(unaffectedProjects);
            var namespacesSeen = new HashSet<string>(StringComparer.Ordinal);
            var diRegistrations = new List<DiRegistration>(preservedDi);
            var analyzerDiagnostics = new List<AnalyzerDiagnostic>(preservedAnalyzerDiags);

            foreach (var typeNode in preservedNodes.Where(n => n.Kind == NodeKind.Type))
            {
                var ns = typeNode.Namespace ?? "(global)";
                if (namespacesSeen.Add(ns))
                {
                    newNodes.Add(CreateNamespaceNode(ns, typeNode.ProjectName));
                }
            }

            var (_, dirtyAnalyzersLoaded) = await ExtractGraphElementsAsync(
                compiledDirty, projectMetaByPath, dirtyTestProjects, dirtyTestSupportProjects, options, rootDir,
                knownTypeIds, knownMethodIds, newNodes, edgeAggregator, projectInfos, namespacesSeen,
                diRegistrations, analyzerDiagnostics, diagnostics, cancellationToken);

            var mergedEdges = new List<DependencyEdge>(preservedEdges.Count + edgeAggregator.Count);
            mergedEdges.AddRange(preservedEdges);
            foreach (var kvp in edgeAggregator)
            {
                mergedEdges.Add(new DependencyEdge(kvp.Key.From, kvp.Key.To, kvp.Key.Kind, kvp.Value.Location, kvp.Value.Weight));
            }

            var graph = new CodeGraph(
                rootDir, newNodes, mergedEdges, projectInfos, diagnostics, diRegistrations, analyzerDiagnostics,
                loadStatus: previousSnapshot.Graph.LoadStatus,
                excludedTestProjectCount: previousSnapshot.Graph.ExcludedTestProjectCount,
                methodsIncluded: options.IncludeMethods,
                analyzersLoadedCount: previousSnapshot.Graph.AnalyzersLoadedCount ?? dirtyAnalyzersLoaded);
            return CreateSnapshot(resolvedTarget, graph);
        }
        finally
        {
            foreach (var ws in workspaces)
            {
                ws.Dispose();
            }
        }
    }

    private static async Task<(IReadOnlyList<Project> Projects, IReadOnlyList<Project> AllWorkspaceProjects)> LoadTargetProjectsAsync(
        string resolvedTarget,
        ExtractionOptions options,
        List<ExtractionDiagnostic> diagnostics,
        List<MSBuildWorkspace> workspaces,
        CancellationToken ct)
    {
        var ext = Path.GetExtension(resolvedTarget);

        if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            return await OpenCsprojFilesAsync([resolvedTarget], options.TargetFramework, diagnostics, workspaces, ct);
        }

        var defaultWs = CreateWorkspace(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["DesignTimeBuild"] = "true" }, diagnostics);
        workspaces.Add(defaultWs);

        if (ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var solution = await defaultWs.OpenSolutionAsync(resolvedTarget, cancellationToken: ct);
                if (solution.Projects.Any())
                {
                    var list = solution.Projects.ToList();
                    return (list, list);
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionDiagnosticSeverity.Info,
                    $"Direct .slnx open fell back to XML project discovery ({ex.Message}).",
                    resolvedTarget));
            }

            var csprojPaths = ParseSlnxProjectPaths(resolvedTarget, diagnostics);
            return await OpenCsprojFilesAsync(csprojPaths, options.TargetFramework, diagnostics, workspaces, ct);
        }

        var sln = await defaultWs.OpenSolutionAsync(resolvedTarget, cancellationToken: ct);
        var slnProjects = sln.Projects.ToList();
        return (slnProjects, slnProjects);
    }

    private static async Task<(IReadOnlyList<Project> Opened, IReadOnlyList<Project> AllWorkspaceProjects)> OpenCsprojFilesAsync(
        IReadOnlyList<string> csprojPaths,
        string? requestedTfm,
        List<ExtractionDiagnostic> diagnostics,
        List<MSBuildWorkspace> workspaces,
        CancellationToken ct)
    {
        var tfmMatchingPaths = new List<string>();
        var defaultPaths = new List<string>();

        foreach (var path in csprojPaths)
        {
            if (!string.IsNullOrWhiteSpace(requestedTfm))
            {
                var meta = CsprojMetadataReader.Read(path);
                if (meta.TargetFrameworks.Contains(requestedTfm, StringComparer.OrdinalIgnoreCase))
                {
                    tfmMatchingPaths.Add(path);
                    continue;
                }
            }
            defaultPaths.Add(path);
        }

        var opened = new List<Project>();
        var allProjects = new List<Project>();

        if (tfmMatchingPaths.Count > 0)
        {
            var tfmWs = CreateWorkspace(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DesignTimeBuild"] = "true",
                ["TargetFramework"] = requestedTfm!
            }, diagnostics);
            workspaces.Add(tfmWs);

            foreach (var path in tfmMatchingPaths)
            {
                ct.ThrowIfCancellationRequested();
                opened.Add(await tfmWs.OpenProjectAsync(path, cancellationToken: ct));
            }
            allProjects.AddRange(tfmWs.CurrentSolution.Projects);
        }

        if (defaultPaths.Count > 0)
        {
            var defaultWs = CreateWorkspace(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DesignTimeBuild"] = "true"
            }, diagnostics);
            workspaces.Add(defaultWs);

            foreach (var path in defaultPaths)
            {
                ct.ThrowIfCancellationRequested();
                opened.Add(await defaultWs.OpenProjectAsync(path, cancellationToken: ct));
            }
            allProjects.AddRange(defaultWs.CurrentSolution.Projects);
        }

        return (opened, allProjects);
    }

    private static MSBuildWorkspace CreateWorkspace(
        Dictionary<string, string> properties,
        List<ExtractionDiagnostic> diagnostics)
    {
        if (!properties.ContainsKey("PreferredUILang"))
        {
            properties["PreferredUILang"] = "en-US";
        }
        var workspace = MSBuildWorkspace.Create(properties);
        var weakDiagList = new WeakReference<List<ExtractionDiagnostic>>(diagnostics);
        var diagLock = new object();
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            if (weakDiagList.TryGetTarget(out var targetList))
            {
                var sev = e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure
                    ? ExtractionDiagnosticSeverity.Warning
                    : ExtractionDiagnosticSeverity.Info;
                lock (diagLock)
                {
                    targetList.Add(new ExtractionDiagnostic(sev, e.Diagnostic.Message, "MSBuildWorkspace"));
                }
            }
        });
        return workspace;
    }

    private static Dictionary<string, CsprojMetadata> ReadAllCsprojMetadata(IReadOnlyList<Project> projects) =>
        projects
            .Where(p => !string.IsNullOrWhiteSpace(p.FilePath))
            .GroupBy(p => p.FilePath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => CsprojMetadataReader.Read(g.Key), StringComparer.OrdinalIgnoreCase);

    private static (List<SelectedProject> Selected, int ExcludedTestCount, HashSet<string> DirectTestProjects, HashSet<string> TestSupportProjects) SelectProjects(
        IReadOnlyList<Project> projects,
        Dictionary<string, CsprojMetadata> projectMetaByPath,
        ExtractionOptions options,
        List<ExtractionDiagnostic> diagnostics)
    {
        var chosenAll = new List<SelectedProject>();

        foreach (var group in projects.GroupBy(p => p.FilePath ?? p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var meta = group.First().FilePath is { } fp && projectMetaByPath.TryGetValue(fp, out var m)
                ? m
                : null;

            var candidates = group
                .Select(p =>
                {
                    var tfm = CsprojMetadataReader.DetectProjectTargetFramework(p, meta);
                    var normalizedName = CsprojMetadataReader.NormalizeProjectName(p.Name, tfm);
                    return new SelectedProject(p, normalizedName, tfm);
                })
                .ToList();

            SelectedProject chosen;
            if (!string.IsNullOrWhiteSpace(options.TargetFramework))
            {
                var match = candidates.FirstOrDefault(c =>
                    string.Equals(c.TargetFramework, options.TargetFramework, StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                {
                    chosen = match;
                }
                else
                {
                    chosen = candidates[0];
                    if (meta is { TargetFrameworks.Count: > 1 })
                    {
                        diagnostics.Add(new ExtractionDiagnostic(
                            ExtractionDiagnosticSeverity.Info,
                            $"Requested TargetFramework '{options.TargetFramework}' was not loaded for '{chosen.NormalizedName}'; fell back to '{chosen.TargetFramework ?? "default"}'.",
                            chosen.NormalizedName));
                    }
                }
            }
            else
            {
                chosen = candidates[0];
            }

            chosenAll.Add(chosen);
        }

        var directTestProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var projectRefDescriptors = new List<(string NormalizedName, string? FilePath, IReadOnlyList<string> ProjectReferences)>(chosenAll.Count);

        foreach (var chosen in chosenAll)
        {
            if (CsprojMetadataReader.IsTestProject(chosen.NormalizedName, chosen.Project.FilePath, projectMetaByPath))
            {
                directTestProjects.Add(chosen.NormalizedName);
            }

            var refNames = chosen.Project.ProjectReferences
                .Select(r => chosen.Project.Solution.GetProject(r.ProjectId))
                .Where(p => p is not null)
                .Select(p =>
                {
                    var refMeta = p!.FilePath is not null && projectMetaByPath.TryGetValue(p.FilePath, out var rm) ? rm : null;
                    var refTfm = CsprojMetadataReader.DetectProjectTargetFramework(p, refMeta);
                    return CsprojMetadataReader.NormalizeProjectName(p.Name, refTfm);
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Also fall back to <ProjectReference> from csproj metadata in case workspace didn't link all refs
            if (chosen.Project.FilePath is { } fp && projectMetaByPath.TryGetValue(fp, out var cmeta))
            {
                foreach (var relRef in cmeta.ProjectReferences)
                {
                    var refName = Path.GetFileNameWithoutExtension(relRef.Replace('\\', '/'));
                    if (!string.IsNullOrWhiteSpace(refName) && !refNames.Contains(refName, StringComparer.OrdinalIgnoreCase))
                    {
                        refNames.Add(refName);
                    }
                }
            }

            projectRefDescriptors.Add((chosen.NormalizedName, chosen.Project.FilePath, refNames));
        }

        var testSupportProjects = CsprojMetadataReader.DetectTestSupportProjects(projectRefDescriptors, directTestProjects);

        var selected = new List<SelectedProject>();
        var excludedTestCount = 0;

        foreach (var chosen in chosenAll)
        {
            var isTestSide = directTestProjects.Contains(chosen.NormalizedName) ||
                             testSupportProjects.Contains(chosen.NormalizedName);

            if (options.IncludeTestProjects || !isTestSide)
            {
                selected.Add(chosen);
            }
            else
            {
                excludedTestCount++;
            }
        }

        return (selected, excludedTestCount, directTestProjects, testSupportProjects);
    }

    private static GraphLoadStatus BuildRoslynLoadStatus(
        List<SelectedProject> selectedProjects,
        List<CompiledProject> compiledProjects,
        List<ExtractionDiagnostic> diagnostics,
        int razorComponentCount = 0)
    {
        var compiledNames = compiledProjects.Select(c => c.Selected.NormalizedName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var degradedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var degradedDetails = new List<DegradedProjectInfo>();

        foreach (var sel in selectedProjects)
        {
            if (!compiledNames.Contains(sel.NormalizedName))
            {
                degradedSet.Add(sel.NormalizedName);
                degradedDetails.Add(new DegradedProjectInfo(sel.NormalizedName, "COMPILATION_FAILED", $"Could not obtain Roslyn compilation for '{sel.NormalizedName}'."));
                continue;
            }

            var projPath = sel.Project.FilePath;
            var projFileName = string.IsNullOrWhiteSpace(projPath) ? null : Path.GetFileName(projPath);
            var workspaceFailure = diagnostics.FirstOrDefault(d =>
                d.Source == "MSBuildWorkspace" &&
                d.Severity >= ExtractionDiagnosticSeverity.Warning &&
                ((!string.IsNullOrWhiteSpace(projPath) && d.Message.Contains(projPath, StringComparison.OrdinalIgnoreCase)) ||
                 (!string.IsNullOrWhiteSpace(projFileName) && d.Message.Contains(projFileName, StringComparison.OrdinalIgnoreCase))));

            if (workspaceFailure is not null)
            {
                degradedSet.Add(sel.NormalizedName);
                degradedDetails.Add(ParseDegradedProjectInfo(sel.NormalizedName, workspaceFailure.Message));
            }
        }

        // Also catch general MSBuild workspace failures that did not match a specific loaded project name
        var hasUnattributedFailure = degradedSet.Count == 0 && diagnostics.Any(d =>
            d.Source == "MSBuildWorkspace" && d.Severity >= ExtractionDiagnosticSeverity.Warning);

        var expected = selectedProjects.Count;
        var degradedCount = degradedSet.Count;
        var loadedClean = Math.Max(0, compiledProjects.Count - degradedSet.Count(compiledNames.Contains));
        var isDegraded = degradedCount > 0 || loadedClean < expected || hasUnattributedFailure;
        var degradedList = degradedSet.Count > 0 ? degradedSet.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() : null;

        string? note = null;
        if (isDegraded)
        {
            note = degradedList is { Count: > 0 }
                ? $"{degradedList.Count} of {expected} project(s) had MSBuild load failures ({string.Join(", ", degradedList)}); type and edge counts may be incomplete. Run 'dotnet restore' to resolve missing packages."
                : "MSBuild reported workspace load warnings; type and edge counts may be incomplete.";
        }

        return new GraphLoadStatus(
            Backend: "roslyn",
            ProjectsExpected: expected,
            ProjectsLoaded: loadedClean,
            ProjectsDegraded: degradedCount,
            Degraded: isDegraded,
            Approximate: false,
            Note: note,
            DegradedProjects: degradedList,
            DegradedDetails: degradedDetails.Count > 0 ? degradedDetails : null,
            RazorComponentCount: razorComponentCount > 0 ? razorComponentCount : null);
    }

    private static DegradedProjectInfo ParseDegradedProjectInfo(string projectName, string message)
    {
        var firstLine = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? message;
        if (firstLine.Length > 240)
        {
            firstLine = firstLine[..237] + "...";
        }

        string? errorCode = null;
        var match = System.Text.RegularExpressions.Regex.Match(firstLine, @"\b((?:MSB|NU|CS|NETSDK|RZ)\d{4})\b");
        if (match.Success)
        {
            errorCode = match.Groups[1].Value;
        }
        else if (firstLine.Contains("project.assets.json", StringComparison.OrdinalIgnoreCase) ||
                 firstLine.Contains("restore", StringComparison.OrdinalIgnoreCase))
        {
            errorCode = "RESTORE_REQUIRED";
        }

        return new DegradedProjectInfo(projectName, errorCode, firstLine);
    }

    private static async Task<List<CompiledProject>> CompileAndCollectSymbolsAsync(
        List<SelectedProject> selectedProjects,
        ExtractionOptions options,
        List<ExtractionDiagnostic> diagnostics,
        HashSet<string> knownTypeIds,
        HashSet<string> knownMethodIds,
        CancellationToken cancellationToken)
    {
        var result = new List<CompiledProject>();

        foreach (var item in selectedProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await item.Project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionDiagnosticSeverity.Warning,
                    $"Could not obtain Roslyn compilation for project '{item.NormalizedName}'.",
                    item.NormalizedName));
                continue;
            }

            var types = CollectDeclaredTypes(compilation.Assembly.GlobalNamespace, options.IncludeGeneratedCode);
            var entryPointMethod = compilation.GetEntryPoint(cancellationToken);
            if (entryPointMethod?.ContainingType is { } entryPointType &&
                IsSynthesizedTopLevelProgram(entryPointType) &&
                entryPointType.DeclaringSyntaxReferences.Length > 0 &&
                !types.Contains(entryPointType, SymbolEqualityComparer.Default))
            {
                types.Add(entryPointType);
            }

            foreach (var type in types)
            {
                knownTypeIds.Add(GetCanonicalTypeNodeId(type, item.NormalizedName));
                if (options.IncludeMethods)
                {
                    foreach (var method in CollectProjectMethods(type, entryPointMethod))
                    {
                        knownMethodIds.Add(GetCanonicalMethodNodeId(method, item.NormalizedName));
                    }
                }
            }

            result.Add(new CompiledProject(item, compilation, types, entryPointMethod));
        }

        return result;
    }

    private static async Task<(int RazorComponentCount, int? AnalyzersLoadedCount)> ExtractGraphElementsAsync(
        List<CompiledProject> compiledProjects,
        Dictionary<string, CsprojMetadata> projectMetaByPath,
        HashSet<string> directTestProjects,
        HashSet<string> testSupportProjects,
        ExtractionOptions options,
        string rootDir,
        HashSet<string> knownTypeIds,
        HashSet<string> knownMethodIds,
        List<CodeNode> nodes,
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> edgeAggregator,
        List<ModelProjectInfo> projectInfos,
        HashSet<string> namespacesSeen,
        List<DiRegistration> diRegistrations,
        List<AnalyzerDiagnostic> analyzerDiagnostics,
        List<ExtractionDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        // Pass 2a: Extract DI registrations, Minimal API handlers, XAML UI metadata, and Razor (.razor) components across ALL projects first
        // so cross-project handlers, XAML references, and Blazor components are registered in knownTypeIds before edge extraction.
        var semanticModelsByProject = new List<Func<SyntaxTree, SemanticModel>>(compiledProjects.Count);
        var allMinimalApiHandlers = new HashSet<string>(StringComparer.Ordinal);
        var allUiEntryPoints = new HashSet<string>(StringComparer.Ordinal);
        var allXamlClasses = new HashSet<string>(StringComparer.Ordinal);
        var allXamlTypeRefs = new List<(string FromTypeId, string ToTypeId, SourceLocation Location)>();
        var allRazorComponents = new List<RazorComponentInfo>();
        var allRazorComponentIds = new HashSet<string>(StringComparer.Ordinal);
        var razorAddedCountByProject = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var (selected, compilation, _, _) in compiledProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var semanticModels = new Dictionary<SyntaxTree, SemanticModel>();
            SemanticModel GetSemanticModel(SyntaxTree tree)
            {
                if (!semanticModels.TryGetValue(tree, out var sm))
                {
                    sm = compilation.GetSemanticModel(tree);
                    semanticModels[tree] = sm;
                }
                return sm;
            }

            semanticModelsByProject.Add(GetSemanticModel);
            var (projDiRegs, minimalApiHandlers) = DiRegistrationExtractor.Extract(
                compilation, selected.NormalizedName, rootDir, GetSemanticModel, options.IncludeGeneratedCode);
            diRegistrations.AddRange(projDiRegs);
            allMinimalApiHandlers.UnionWith(minimalApiHandlers);

            var xamlScan = XamlMetadataScanner.ScanProjectDirectory(selected.Project.FilePath, rootDir);
            allUiEntryPoints.UnionWith(xamlScan.UiEntryPointTypeIds);
            allXamlClasses.UnionWith(xamlScan.XamlClassTypeIds);
            allXamlTypeRefs.AddRange(xamlScan.TypeReferences);

            var meta = selected.Project.FilePath is not null && projectMetaByPath.TryGetValue(selected.Project.FilePath, out var pm)
                ? pm
                : null;
            var outputKind = compilation.Options.OutputKind.ToString();
            var razorScan = RazorMetadataScanner.ScanProjectDirectory(
                selected.Project.FilePath, selected.NormalizedName, meta?.RootNamespace, outputKind, rootDir);
            foreach (var comp in razorScan.Components)
            {
                allRazorComponents.Add(comp);
                allRazorComponentIds.Add(comp.TypeId);
                if (comp.IsEntryPoint)
                {
                    allUiEntryPoints.Add(comp.TypeId);
                }

                if (knownTypeIds.Add(comp.TypeId))
                {
                    razorAddedCountByProject[selected.NormalizedName] =
                        razorAddedCountByProject.GetValueOrDefault(selected.NormalizedName) + 1;
                }
            }
        }

        foreach (var (fromId, toId, loc) in allXamlTypeRefs)
        {
            if (knownTypeIds.Contains(fromId) && knownTypeIds.Contains(toId) &&
                !string.Equals(fromId, toId, StringComparison.Ordinal))
            {
                SymbolDependencyExtractor.AddEdge(edgeAggregator, fromId, toId, DependencyKind.TypeReference, loc);
            }
        }

        var emittedRoslynTypeIds = new HashSet<string>(StringComparer.Ordinal);
        var distinctAnalyzerTypes = new HashSet<Type>();

        // Pre-collect cross-type method/constructor calls made inside property/event bodies and field initializers
        // so method-scope dead-code analysis preserves reachability across types (e.g. expression-bodied properties, static field target-typed new).
        var calledFromMemberBodyByTarget = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (options.IncludeMethods)
        {
            for (var idx = 0; idx < compiledProjects.Count; idx++)
            {
                var (selected, _, types, _) = compiledProjects[idx];
                var getSemanticModel = semanticModelsByProject[idx];
                foreach (var type in types)
                {
                    var callerTypeId = GetCanonicalTypeNodeId(type, selected.NormalizedName);
                    var calledIds = SymbolDependencyExtractor.CollectMethodsCalledFromPropertiesAndInitializers(
                        type, knownMethodIds, getSemanticModel);
                    foreach (var targetMethodId in calledIds)
                    {
                        if (!calledFromMemberBodyByTarget.TryGetValue(targetMethodId, out var callers))
                        {
                            callers = new HashSet<string>(StringComparer.Ordinal);
                            calledFromMemberBodyByTarget[targetMethodId] = callers;
                        }
                        callers.Add(callerTypeId);
                    }
                }
            }
        }

        // Pass 2b: Extract project nodes, analyzer diagnostics, types, methods, and dependencies
        for (var idx = 0; idx < compiledProjects.Count; idx++)
        {
            var (selected, compilation, types, entryPointMethod) = compiledProjects[idx];
            var getSemanticModel = semanticModelsByProject[idx];
            cancellationToken.ThrowIfCancellationRequested();
            var project = selected.Project;
            var projectName = selected.NormalizedName;
            var outputKind = compilation.Options.OutputKind.ToString();
            var isTestProject = directTestProjects.Contains(projectName);
            var isTestSupportProject = testSupportProjects.Contains(projectName);
            var meta = project.FilePath is not null && projectMetaByPath.TryGetValue(project.FilePath, out var m)
                ? m
                : new CsprojMetadata(null, [], [], []);

            var projectRefNames = project.ProjectReferences
                .Select(r => project.Solution.GetProject(r.ProjectId))
                .Where(p => p is not null)
                .Select(p =>
                {
                    var refMeta = p!.FilePath is not null && projectMetaByPath.TryGetValue(p.FilePath, out var rm) ? rm : null;
                    var refTfm = CsprojMetadataReader.DetectProjectTargetFramework(p, refMeta);
                    return CsprojMetadataReader.NormalizeProjectName(p.Name, refTfm);
                })
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var extraRazorTypes = razorAddedCountByProject.GetValueOrDefault(projectName);
            var relProjectPath = SymbolDependencyExtractor.MakeRelativePath(rootDir, project.FilePath ?? projectName);
            projectInfos.Add(new ModelProjectInfo(
                projectName,
                relProjectPath,
                selected.TargetFramework ?? meta.TargetFramework,
                projectRefNames,
                meta.PackageReferences,
                types.Count + extraRazorTypes,
                project.Documents.Count()));

            var projMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["outputKind"] = outputKind
            };
            if (isTestProject) projMetadata["isTestProject"] = "true";
            if (isTestSupportProject) projMetadata["isTestSupportProject"] = "true";

            nodes.Add(new CodeNode(
                Id: $"project:{projectName}",
                Name: projectName,
                FullName: projectName,
                Kind: NodeKind.Project,
                ProjectName: projectName,
                Location: new SourceLocation(relProjectPath, 1, 1),
                Metadata: projMetadata));

            foreach (var refName in projectRefNames)
            {
                SymbolDependencyExtractor.AddEdge(edgeAggregator, $"project:{projectName}", $"project:{refName}", DependencyKind.ProjectReference, null);
            }

            if (options.IncludeAnalyzerDiagnostics)
            {
                var (diags, loadedTypes) = await RoslynAnalyzerRunner.RunAsync(
                    project, compilation, projectName, rootDir, options.AnalyzerCategories, diagnostics, cancellationToken,
                    includeGenerated: options.IncludeGeneratedCode,
                    elevateSeverity: options.ElevateAnalyzerSeverity);
                distinctAnalyzerTypes.UnionWith(loadedTypes);
                analyzerDiagnostics.AddRange(diags);
            }

            foreach (var type in types)
            {
                emittedRoslynTypeIds.Add(GetCanonicalTypeNodeId(type, projectName));
                ExtractSingleType(
                    type, projectName, outputKind, isTestProject, isTestSupportProject,
                    entryPointMethod, allMinimalApiHandlers, allUiEntryPoints, allXamlClasses, allRazorComponentIds,
                    calledFromMemberBodyByTarget,
                    options, rootDir, knownTypeIds, knownMethodIds,
                    getSemanticModel, nodes, edgeAggregator, namespacesSeen);
            }
        }

        // Pass 2c: Emit markup-only Razor (.razor) component nodes not already represented by a .razor.cs code-behind
        foreach (var comp in allRazorComponents)
        {
            if (emittedRoslynTypeIds.Contains(comp.TypeId))
            {
                continue;
            }

            if (namespacesSeen.Add(comp.Namespace))
            {
                nodes.Add(CreateNamespaceNode(comp.Namespace, comp.ProjectName));
            }

            var compMeta = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["outputKind"] = comp.OutputKind,
                ["isRazorComponent"] = "true",
                ["baseTypes"] = "Microsoft.AspNetCore.Components.ComponentBase"
            };
            if (comp.IsEntryPoint)
            {
                compMeta["isUiEntryPoint"] = "true";
            }
            if (directTestProjects.Contains(comp.ProjectName)) compMeta["isTestProject"] = "true";
            if (testSupportProjects.Contains(comp.ProjectName)) compMeta["isTestSupportProject"] = "true";

            nodes.Add(new CodeNode(
                Id: comp.TypeId,
                Name: comp.SimpleName,
                FullName: comp.TypeId,
                Kind: NodeKind.Type,
                ProjectName: comp.ProjectName,
                Namespace: comp.Namespace,
                TypeKind: ModelTypeKind.Class,
                Accessibility: AccessibilityKind.Public,
                IsAbstract: false,
                IsStatic: false,
                Location: new SourceLocation(comp.RelativePath, 1, 1),
                LineCount: comp.LineCount,
                Metadata: compMeta));
        }

        // Pass 2d: Record Razor markup / @inject / @code type references for ALL .razor components
        RazorMetadataScanner.ResolveAndRecordReferences(allRazorComponents, knownTypeIds, edgeAggregator);

        return (allRazorComponents.Count, options.IncludeAnalyzerDiagnostics ? distinctAnalyzerTypes.Count : null);
    }

    private static void ExtractSingleType(
        INamedTypeSymbol type,
        string projectName,
        string outputKind,
        bool isTestProject,
        bool isTestSupportProject,
        IMethodSymbol? entryPointMethod,
        HashSet<string> minimalApiHandlers,
        HashSet<string> uiEntryPoints,
        HashSet<string> xamlClasses,
        HashSet<string> razorComponents,
        Dictionary<string, HashSet<string>> calledFromMemberBodyByTarget,
        ExtractionOptions options,
        string rootDir,
        HashSet<string> knownTypeIds,
        HashSet<string> knownMethodIds,
        Func<SyntaxTree, SemanticModel> getSemanticModel,
        List<CodeNode> nodes,
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> edgeAggregator,
        HashSet<string> namespacesSeen)
    {
        var isSyntheticTopLevel = IsSynthesizedTopLevelProgram(type);
        var typeId = GetCanonicalTypeNodeId(type, projectName);
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false }
            ? type.ContainingNamespace.ToDisplayString()
            : (isSyntheticTopLevel ? projectName : "(global)");

        if (namespacesSeen.Add(ns))
        {
            nodes.Add(CreateNamespaceNode(ns, projectName));
        }

        var primaryLocation = SymbolDependencyExtractor.GetPrimaryLocation(type, rootDir, options.IncludeGeneratedCode);
        var (lineCount, totalComplexity, maxMethodComplexity, totalCognitive, maxMethodCognitive) =
            SymbolDependencyExtractor.ComputeTypeSyntaxStats(type, options.IncludeGeneratedCode);
        var lcom4 = Lcom4Calculator.Calculate(type, getSemanticModel, options.IncludeGeneratedCode);

        var maxCtorParams = type.InstanceConstructors
            .Where(c => !c.IsImplicitlyDeclared)
            .Select(c => c.Parameters.Length)
            .DefaultIfEmpty(0)
            .Max();

        var memberCount = type.GetMembers()
            .Count(m => !m.IsImplicitlyDeclared && m.Kind is SymbolKind.Method or SymbolKind.Property or SymbolKind.Field or SymbolKind.Event);

        var typeMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["maxMethodComplexity"] = maxMethodComplexity.ToString(),
            ["maxMethodCognitiveComplexity"] = maxMethodCognitive.ToString(),
            ["outputKind"] = outputKind
        };

        var attrs = FormatAttributes(type);
        if (!string.IsNullOrEmpty(attrs)) typeMetadata["attributes"] = attrs;

        var baseTypes = FormatBaseTypesAndInterfaces(type);
        if (!string.IsNullOrEmpty(baseTypes)) typeMetadata["baseTypes"] = baseTypes;

        // Always record ctorParamTypes (even empty "") for concrete classes/structs so DI analysis
        // does not fall back to regular method Parameter edges on parameterless Singletons!
        if (!type.IsAbstract && !type.IsStatic &&
            type.TypeKind is Microsoft.CodeAnalysis.TypeKind.Class or Microsoft.CodeAnalysis.TypeKind.Struct)
        {
            typeMetadata["ctorParamTypes"] = FormatDiConstructorParamTypes(type);
        }

        if (HasMethodWithAnyAttribute(type, TestMethodAttrNames))
        {
            typeMetadata["hasTestMethods"] = "true";
        }

        if (HasMethodWithAnyAttribute(type, EndpointMethodAttrNames))
        {
            typeMetadata["hasEndpointMethods"] = "true";
        }

        if (entryPointMethod is not null && SymbolEqualityComparer.Default.Equals(entryPointMethod.ContainingType, type))
        {
            typeMetadata["isEntryPointMain"] = "true";
        }

        if (minimalApiHandlers.Contains(typeId) || minimalApiHandlers.Contains(SymbolDependencyExtractor.GetTypeId(type)))
        {
            typeMetadata["isMinimalApiHandler"] = "true";
        }

        if (uiEntryPoints.Contains(typeId) || uiEntryPoints.Contains(SymbolDependencyExtractor.GetTypeId(type)))
        {
            typeMetadata["isUiEntryPoint"] = "true";
        }

        if (xamlClasses.Contains(typeId) || xamlClasses.Contains(SymbolDependencyExtractor.GetTypeId(type)))
        {
            typeMetadata["isXamlClass"] = "true";
        }

        if (razorComponents.Contains(typeId) || razorComponents.Contains(SymbolDependencyExtractor.GetTypeId(type)))
        {
            typeMetadata["isRazorComponent"] = "true";
        }

        if (isTestProject) typeMetadata["isTestProject"] = "true";
        if (isTestSupportProject) typeMetadata["isTestSupportProject"] = "true";
        if (GeneratedCodeClassifier.IsMigrationOrSnapshotType(type)) typeMetadata["isMigration"] = "true";
        if (GeneratedCodeClassifier.IsDbContextType(type)) typeMetadata["isDbContext"] = "true";
        if (GeneratedCodeClassifier.IsJsonSerializerContextType(type)) typeMetadata["isJsonSerializerContext"] = "true";
        if (GeneratedCodeClassifier.HasAnyGeneratedPart(type)) typeMetadata["hasGeneratedParts"] = "true";

        if (isSyntheticTopLevel)
        {
            typeMetadata["isSynthetic"] = "true";
            typeMetadata["syntheticKind"] = "compilationUnit";
        }

        var containingTypeId = type.ContainingType is not null
            ? GetCanonicalTypeNodeId(type.ContainingType, projectName)
            : null;

        nodes.Add(new CodeNode(
            Id: typeId,
            Name: type.Name,
            FullName: typeId,
            Kind: NodeKind.Type,
            ProjectName: projectName,
            Namespace: ns,
            TypeKind: MapTypeKind(type),
            Accessibility: MapAccessibility(type.DeclaredAccessibility),
            IsAbstract: type.IsAbstract || type.TypeKind == Microsoft.CodeAnalysis.TypeKind.Interface,
            IsStatic: type.IsStatic,
            Location: primaryLocation,
            LineCount: lineCount,
            CyclomaticComplexity: totalComplexity,
            ConstructorParameterCount: maxCtorParams,
            MemberCount: memberCount,
            Metadata: typeMetadata,
            CognitiveComplexity: totalCognitive,
            Lcom4: lcom4,
            ContainingTypeId: containingTypeId));

        SymbolDependencyExtractor.ExtractTypeDependencies(
            type, typeId, knownTypeIds, rootDir, getSemanticModel, edgeAggregator, options.IncludeGeneratedCode);

        if (options.IncludeMethods)
        {
            var interfaceImplMethods = CollectInterfaceImplementationMethods(type);

            foreach (var method in CollectProjectMethods(type, entryPointMethod))
            {
                var impl = SymbolDependencyExtractor.ResolveImplementation(method);
                if (!options.IncludeGeneratedCode &&
                    impl.DeclaringSyntaxReferences.Length > 0 &&
                    impl.DeclaringSyntaxReferences.All(r => GeneratedCodeClassifier.IsGeneratedSyntaxTree(r.SyntaxTree)))
                {
                    continue;
                }

                var methodId = GetCanonicalMethodNodeId(impl, projectName);
                var (mLines, mCc, mCog) = SymbolDependencyExtractor.ComputeMethodSyntaxStats(impl, options.IncludeGeneratedCode);
                var methodMeta = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["containingType"] = typeId,
                    ["methodKind"] = impl.MethodKind.ToString(),
                    ["outputKind"] = outputKind
                };

                var mAttrs = FormatAttributes(impl);
                if (!string.IsNullOrEmpty(mAttrs)) methodMeta["attributes"] = mAttrs;

                if (entryPointMethod is not null && SymbolEqualityComparer.Default.Equals(entryPointMethod, impl))
                {
                    methodMeta["isEntryPointMain"] = "true";
                }

                if (impl.IsOverride)
                {
                    methodMeta["isOverride"] = "true";
                }

                if (impl.ExplicitInterfaceImplementations.Length > 0 || interfaceImplMethods.Contains(impl))
                {
                    methodMeta["implementsInterface"] = "true";
                }

                if (minimalApiHandlers.Contains(methodId) || minimalApiHandlers.Contains(SymbolDependencyExtractor.GetMethodId(impl)))
                {
                    methodMeta["isMinimalApiHandler"] = "true";
                }

                if (calledFromMemberBodyByTarget.TryGetValue(methodId, out var callerTypes) ||
                    calledFromMemberBodyByTarget.TryGetValue(SymbolDependencyExtractor.GetMethodId(impl), out callerTypes))
                {
                    methodMeta["calledFromMemberBodyBy"] = string.Join(";", callerTypes);
                    if (callerTypes.Contains(typeId))
                    {
                        methodMeta["calledFromMemberBody"] = "true";
                    }
                }

                nodes.Add(new CodeNode(
                    Id: methodId,
                    Name: impl.MethodKind == MethodKind.StaticConstructor ? ".cctor" : impl.Name,
                    FullName: methodId,
                    Kind: NodeKind.Method,
                    ProjectName: projectName,
                    Namespace: ns,
                    Accessibility: MapAccessibility(impl.DeclaredAccessibility),
                    IsAbstract: impl.IsAbstract,
                    IsStatic: impl.IsStatic,
                    Location: SymbolDependencyExtractor.GetPrimaryLocation(impl, rootDir) ?? primaryLocation,
                    LineCount: mLines,
                    CyclomaticComplexity: mCc,
                    ConstructorParameterCount: impl.MethodKind == MethodKind.Constructor ? impl.Parameters.Length : 0,
                    Metadata: methodMeta,
                    CognitiveComplexity: mCog,
                    ContainingTypeId: typeId));

                SymbolDependencyExtractor.ExtractMethodDependencies(
                    impl, methodId, knownMethodIds, rootDir, getSemanticModel, edgeAggregator);
            }
        }
    }

    private static IEnumerable<IMethodSymbol> CollectProjectMethods(INamedTypeSymbol type, IMethodSymbol? entryPointMethod)
    {
        var list = type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(SymbolDependencyExtractor.IsTrackableMethod)
            .ToList();

        if (entryPointMethod is not null &&
            SymbolEqualityComparer.Default.Equals(entryPointMethod.ContainingType, type) &&
            entryPointMethod.DeclaringSyntaxReferences.Length > 0 &&
            !list.Contains(entryPointMethod, SymbolEqualityComparer.Default))
        {
            list.Add(entryPointMethod);
        }

        return list;
    }

    private static HashSet<ISymbol> CollectInterfaceImplementationMethods(INamedTypeSymbol type)
    {
        var impls = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var iface in type.AllInterfaces)
        {
            foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
            {
                var impl = type.FindImplementationForInterfaceMember(member);
                if (impl is not null)
                {
                    impls.Add(impl);
                }
            }
        }
        return impls;
    }

    private static string FormatAttributes(ISymbol symbol)
    {
        var attrs = symbol.GetAttributes()
            .Select(a => a.AttributeClass?.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.Ordinal);
        return string.Join(",", attrs);
    }

    private static readonly HashSet<string> TestMethodAttrNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "FactAttribute", "TheoryAttribute", "TestAttribute", "TestCaseAttribute",
        "TestCaseSourceAttribute", "TestMethodAttribute", "DataTestMethodAttribute"
    };

    private static readonly HashSet<string> EndpointMethodAttrNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "HttpGetAttribute", "HttpPostAttribute", "HttpPutAttribute", "HttpDeleteAttribute",
        "HttpPatchAttribute", "HttpHeadAttribute", "HttpOptionsAttribute", "AcceptVerbsAttribute",
        "RouteAttribute", "McpServerToolAttribute", "McpServerPromptAttribute", "McpServerResourceAttribute"
    };

    private static bool IsSynthesizedTopLevelProgram(INamedTypeSymbol type) =>
        type.ContainingNamespace.IsGlobalNamespace &&
        type.Name == "Program" &&
        (type.IsImplicitlyDeclared ||
         type.GetMembers("<Main>$").Length > 0 ||
         type.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.CompilationUnitSyntax));

    private static string GetCanonicalTypeNodeId(INamedTypeSymbol type, string projectName)
    {
        if (IsSynthesizedTopLevelProgram(type))
        {
            return $"{projectName}.Program";
        }
        return SymbolDependencyExtractor.GetTypeId(type);
    }

    private static string GetCanonicalMethodNodeId(IMethodSymbol method, string projectName)
    {
        var raw = SymbolDependencyExtractor.GetMethodId(method);
        if (method.ContainingType is not null && IsSynthesizedTopLevelProgram(method.ContainingType))
        {
            return $"{projectName}.{raw}";
        }
        return raw;
    }

    private static bool HasMethodWithAnyAttribute(INamedTypeSymbol type, HashSet<string> attrNames) =>
        type.GetMembers().OfType<IMethodSymbol>().Any(m =>
            m.GetAttributes().Any(a => a.AttributeClass?.Name is { } name && attrNames.Contains(name)));

    private static string FormatBaseTypesAndInterfaces(INamedTypeSymbol type)
    {
        var list = new List<string>();
        for (var b = type.BaseType; b is not null && b.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType); b = b.BaseType)
        {
            list.Add(SymbolDependencyExtractor.GetTypeId(b));
        }

        foreach (var iface in type.AllInterfaces)
        {
            list.Add(SymbolDependencyExtractor.GetTypeId(iface));
        }

        // Semicolon delimiter avoids splitting multi-parameter generic types like IHandler<TReq, TRes>
        return string.Join(";", list.Distinct(StringComparer.Ordinal));
    }

    private static string FormatDiConstructorParamTypes(INamedTypeSymbol type)
    {
        if (type.IsAbstract || type.IsStatic ||
            type.TypeKind is Microsoft.CodeAnalysis.TypeKind.Interface or Microsoft.CodeAnalysis.TypeKind.Enum or Microsoft.CodeAnalysis.TypeKind.Delegate)
        {
            return string.Empty;
        }

        var ctors = type.InstanceConstructors
            .Where(c => c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal)
            .ToList();
        if (ctors.Count == 0)
        {
            return string.Empty;
        }

        var chosen = ctors.FirstOrDefault(c => c.GetAttributes().Any(a => a.AttributeClass?.Name == "ActivatorUtilitiesConstructorAttribute"))
            ?? ctors.Where(c => c.DeclaredAccessibility == Accessibility.Public).OrderByDescending(c => c.Parameters.Length).FirstOrDefault()
            ?? ctors.OrderByDescending(c => c.Parameters.Length).First();

        var paramTypeIds = new List<string>();
        foreach (var param in chosen.Parameters)
        {
            CollectInjectedTypeIds(param.Type, paramTypeIds, isCollection: false);
        }

        return string.Join(";", paramTypeIds.Distinct(StringComparer.Ordinal));
    }

    private static void CollectInjectedTypeIds(ITypeSymbol type, List<string> target, bool isCollection)
    {
        if (type is not INamedTypeSymbol named)
        {
            return;
        }

        if (named.IsGenericType && named.TypeArguments.Length == 1)
        {
            if (named.Name is "IEnumerable" or "IReadOnlyList" or "IReadOnlyCollection")
            {
                CollectInjectedTypeIds(named.TypeArguments[0], target, isCollection: true);
                return;
            }
            if (named.Name is "Lazy" or "Func")
            {
                CollectInjectedTypeIds(named.TypeArguments[0], target, isCollection);
                return;
            }
        }

        var id = SymbolDependencyExtractor.GetConstructedTypeId(named);
        target.Add(isCollection ? $"many:{id}" : id);
    }

    private static CodeNode CreateNamespaceNode(string ns, string projectName) =>
        new(
            Id: $"ns:{ns}",
            Name: ns.Contains('.') ? ns[(ns.LastIndexOf('.') + 1)..] : ns,
            FullName: ns,
            Kind: NodeKind.Namespace,
            ProjectName: projectName,
            Namespace: ns);

    private static bool HasStructuralChange(IReadOnlySet<string> changedFilePaths) =>
        changedFilePaths.Any(path =>
        {
            var ext = Path.GetExtension(path);
            var fileName = Path.GetFileName(path);
            return ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".targets", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".razor", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".xaml", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".axaml", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals("global.json", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase) ||
                   fileName.Equals(".globalconfig", StringComparison.OrdinalIgnoreCase);
        });

    private static (HashSet<string> DirtyProjects, bool HasUnmappedProductionChange) FindDirtyProjectsWithDependents(
        CodeGraph previousGraph,
        string rootDir,
        IReadOnlySet<string> changedFilePaths,
        bool includeTestProjects)
    {
        var projectDirs = previousGraph.Projects
            .Select(p =>
            {
                var fullCsproj = Path.GetFullPath(Path.Combine(rootDir, p.FilePath));
                var dir = Path.GetDirectoryName(fullCsproj) ?? rootDir;
                if (!dir.EndsWith(Path.DirectorySeparatorChar))
                {
                    dir += Path.DirectorySeparatorChar;
                }
                return (p.Name, Dir: dir);
            })
            .OrderByDescending(x => x.Dir.Length)
            .ToList();

        var dirty = new HashSet<string>(StringComparer.Ordinal);
        foreach (var changed in changedFilePaths)
        {
            var full = Path.GetFullPath(changed);
            var owner = projectDirs.FirstOrDefault(p => full.StartsWith(p.Dir, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrEmpty(owner.Name))
            {
                // If test projects are excluded and this file lives inside a test project directory, ignore it
                if (!includeTestProjects && IsInsideExcludedTestProject(full, rootDir))
                {
                    continue;
                }
                return (dirty, true);
            }
            dirty.Add(owner.Name);
        }

        var reverseRefs = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var proj in previousGraph.Projects)
        {
            foreach (var r in proj.ProjectReferences)
            {
                if (!reverseRefs.TryGetValue(r, out var dependents))
                {
                    dependents = [];
                    reverseRefs[r] = dependents;
                }
                dependents.Add(proj.Name);
            }
        }

        var queue = new Queue<string>(dirty);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!reverseRefs.TryGetValue(current, out var dependents))
            {
                continue;
            }
            foreach (var dep in dependents)
            {
                if (dirty.Add(dep))
                {
                    queue.Enqueue(dep);
                }
            }
        }

        return (dirty, false);
    }

    private static bool IsInsideExcludedTestProject(string filePath, string rootDir)
    {
        var normRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDir));
        var dir = Path.GetDirectoryName(filePath);
        while (!string.IsNullOrWhiteSpace(dir))
        {
            if (Directory.Exists(dir))
            {
                var csproj = Directory.GetFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (csproj is not null)
                {
                    var name = Path.GetFileNameWithoutExtension(csproj);
                    var meta = new Dictionary<string, CsprojMetadata>(StringComparer.OrdinalIgnoreCase)
                    {
                        [csproj] = CsprojMetadataReader.Read(csproj)
                    };
                    return CsprojMetadataReader.IsTestProject(name, csproj, meta);
                }
            }

            if (Path.TrimEndingDirectorySeparator(dir).Equals(normRoot, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            dir = Path.GetDirectoryName(dir);
        }

        return false;
    }

    private static List<string> ParseSlnxProjectPaths(string slnxPath, List<ExtractionDiagnostic> diagnostics)
    {
        var baseDir = Path.GetDirectoryName(slnxPath) ?? Directory.GetCurrentDirectory();
        var doc = XDocument.Load(slnxPath);
        var projectPaths = doc.Descendants()
            .Where(e => e.Name.LocalName.Equals("Project", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Attribute("Path")?.Value)
            .Where(p => !string.IsNullOrWhiteSpace(p) && p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            .Select(p => Path.GetFullPath(Path.Combine(baseDir, p!.Replace('\\', Path.DirectorySeparatorChar))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var existing = new List<string>();
        foreach (var path in projectPaths)
        {
            if (!File.Exists(path))
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionDiagnosticSeverity.Warning,
                    $"Project listed in .slnx not found: {path}",
                    slnxPath));
                continue;
            }
            existing.Add(path);
        }

        return existing;
    }

    private static ArchitectureSnapshot CreateSnapshot(string resolvedTarget, CodeGraph graph) =>
        new(
            SnapshotId: $"snap-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            TargetPath: resolvedTarget,
            CapturedAt: DateTimeOffset.UtcNow,
            Graph: graph);

    public static string ResolveTargetFile(string targetPath)
    {
        var fullPath = Path.GetFullPath(targetPath);
        if (File.Exists(fullPath))
        {
            return fullPath;
        }

        if (!Directory.Exists(fullPath))
        {
            throw new FileNotFoundException($"Target path does not exist: '{fullPath}'.", fullPath);
        }

        var slnx = Directory.GetFiles(fullPath, "*.slnx", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (slnx is not null) return slnx;

        var sln = Directory.GetFiles(fullPath, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (sln is not null) return sln;

        var csproj = Directory.GetFiles(fullPath, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (csproj is not null) return csproj;

        throw new FileNotFoundException($"No .slnx, .sln, or .csproj file found in directory '{fullPath}'.", fullPath);
    }

    private static List<INamedTypeSymbol> CollectDeclaredTypes(INamespaceSymbol root, bool includeGenerated)
    {
        var result = new List<INamedTypeSymbol>();

        void VisitNamespace(INamespaceSymbol ns)
        {
            foreach (var type in ns.GetTypeMembers())
            {
                VisitType(type);
            }

            foreach (var childNs in ns.GetNamespaceMembers())
            {
                VisitNamespace(childNs);
            }
        }

        void VisitType(INamedTypeSymbol type)
        {
            if (type.IsImplicitlyDeclared || type.Name.StartsWith("<", StringComparison.Ordinal))
            {
                return;
            }

            if (!includeGenerated && IsGeneratedOnly(type))
            {
                return;
            }

            result.Add(type);

            foreach (var nested in type.GetTypeMembers())
            {
                VisitType(nested);
            }
        }

        VisitNamespace(root);
        return result;
    }

    private static bool IsGeneratedOnly(INamedTypeSymbol type) =>
        GeneratedCodeClassifier.IsGeneratedOnlyType(type);

    private static ModelTypeKind MapTypeKind(INamedTypeSymbol type)
    {
        if (type.IsRecord)
        {
            return ModelTypeKind.Record;
        }

        return type.TypeKind switch
        {
            Microsoft.CodeAnalysis.TypeKind.Class => ModelTypeKind.Class,
            Microsoft.CodeAnalysis.TypeKind.Interface => ModelTypeKind.Interface,
            Microsoft.CodeAnalysis.TypeKind.Struct => ModelTypeKind.Struct,
            Microsoft.CodeAnalysis.TypeKind.Enum => ModelTypeKind.Enum,
            Microsoft.CodeAnalysis.TypeKind.Delegate => ModelTypeKind.Delegate,
            _ => ModelTypeKind.Unknown
        };
    }

    private static AccessibilityKind MapAccessibility(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => AccessibilityKind.Public,
        Accessibility.Internal => AccessibilityKind.Internal,
        Accessibility.Protected => AccessibilityKind.Protected,
        Accessibility.ProtectedOrInternal => AccessibilityKind.ProtectedInternal,
        Accessibility.ProtectedAndInternal => AccessibilityKind.PrivateProtected,
        _ => AccessibilityKind.Private
    };

    private async Task<ArchitectureSnapshot?> ExtractFromWarmWorkspaceAsync(
        string resolvedTarget,
        string rootDir,
        ArchitectureSnapshot previousSnapshot,
        WarmWorkspaceSession warm,
        IReadOnlySet<string> changedFilePaths,
        HashSet<string> dirtyProjectNames,
        ExtractionOptions options,
        CancellationToken cancellationToken)
    {
        // Pre-read all existing changed .cs files with FileShare.ReadWrite outside lock so IO failures leave warm state untouched
        var fileContents = new Dictionary<string, SourceText?>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawPath in changedFilePaths)
        {
            if (!rawPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(rawPath);
            if (File.Exists(fullPath))
            {
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                fileContents[fullPath] = SourceText.From(stream, Encoding.UTF8);
            }
            else
            {
                fileContents[fullPath] = null;
            }
        }

        var updatedDocs = 0;
        var addedDocs = 0;
        var removedDocs = 0;
        List<Project> updatedAllProjects;

        lock (warm.SyncLock)
        {
            if (warm.Solutions.Count == 0)
            {
                return null;
            }

            var stagedSolutions = new List<Solution>(warm.Solutions);
            for (var sIdx = 0; sIdx < stagedSolutions.Count; sIdx++)
            {
                var solution = stagedSolutions[sIdx];
                foreach (var (fullPath, text) in fileContents)
                {
                    var matchingDocIds = solution.Projects
                        .SelectMany(p => p.Documents)
                        .Where(d => d.FilePath is not null &&
                                    string.Equals(Path.GetFullPath(d.FilePath), fullPath, StringComparison.OrdinalIgnoreCase))
                        .Select(d => d.Id)
                        .ToList();

                    if (text is not null)
                    {
                        if (matchingDocIds.Count > 0)
                        {
                            foreach (var docId in matchingDocIds)
                            {
                                solution = solution.WithDocumentText(docId, text);
                                updatedDocs++;
                            }
                        }
                        else
                        {
                            // Match ONLY deepest owning project directory (so nested Sub/Sub.csproj doesn't duplicate into Parent.csproj)
                            var fileName = Path.GetFileName(fullPath);
                            var matchingProjects = solution.Projects
                                .Where(p => p.FilePath is not null)
                                .Select(p =>
                                {
                                    var projDir = Path.GetDirectoryName(Path.GetFullPath(p.FilePath!)) ?? string.Empty;
                                    var prefix = projDir.EndsWith(Path.DirectorySeparatorChar) ? projDir : projDir + Path.DirectorySeparatorChar;
                                    return (Project: p, ProjDir: projDir, Prefix: prefix);
                                })
                                .Where(x => !string.IsNullOrEmpty(x.ProjDir) && fullPath.StartsWith(x.Prefix, StringComparison.OrdinalIgnoreCase))
                                .ToList();

                            if (matchingProjects.Count > 0)
                            {
                                var maxLen = matchingProjects.Max(x => x.Prefix.Length);
                                foreach (var match in matchingProjects.Where(x => x.Prefix.Length == maxLen))
                                {
                                    var relFolder = Path.GetDirectoryName(Path.GetRelativePath(match.ProjDir, fullPath));
                                    var folders = string.IsNullOrEmpty(relFolder)
                                        ? null
                                        : relFolder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
                                    var newDocId = DocumentId.CreateNewId(match.Project.Id, fileName);
                                    solution = solution.AddDocument(newDocId, fileName, text, folders, filePath: fullPath);
                                    addedDocs++;
                                }
                            }
                        }
                    }
                    else
                    {
                        foreach (var docId in matchingDocIds)
                        {
                            solution = solution.RemoveDocument(docId);
                            removedDocs++;
                        }
                    }
                }

                stagedSolutions[sIdx] = solution;
            }

            // Commit staged solutions atomically
            for (var sIdx = 0; sIdx < stagedSolutions.Count; sIdx++)
            {
                warm.Solutions[sIdx] = stagedSolutions[sIdx];
            }

            updatedAllProjects = warm.Solutions.SelectMany(s => s.Projects).ToList();
        }

        var unaffectedProjects = previousSnapshot.Graph.Projects
            .Where(p => !dirtyProjectNames.Contains(p.Name))
            .ToList();
        var unaffectedProjectSet = unaffectedProjects.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var preservedNodes = previousSnapshot.Graph.Nodes
            .Where(n => n.Kind != NodeKind.Namespace && unaffectedProjectSet.Contains(n.ProjectName))
            .ToList();
        var preservedNodeIds = preservedNodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

        var preservedEdges = previousSnapshot.Graph.Edges
            .Where(e => preservedNodeIds.Contains(e.FromId) && preservedNodeIds.Contains(e.ToId))
            .ToList();

        var preservedDi = previousSnapshot.Graph.DiRegistrations
            .Where(r => r.ProjectName is not null
                ? unaffectedProjectSet.Contains(r.ProjectName)
                : r.RegisteredByTypeId is not null && preservedNodeIds.Contains(r.RegisteredByTypeId))
            .ToList();
        var preservedAnalyzerDiags = previousSnapshot.Graph.AnalyzerDiagnostics
            .Where(d => d.ProjectName is not null && unaffectedProjectSet.Contains(d.ProjectName))
            .ToList();

        var diagnostics = new List<ExtractionDiagnostic>
        {
            new(ExtractionDiagnosticSeverity.Info,
                $"Incremental Roslyn Document update ({updatedDocs} updated, {addedDocs} added, {removedDocs} removed) recompiled {dirtyProjectNames.Count}/{previousSnapshot.Graph.Projects.Count} project(s): {string.Join(", ", dirtyProjectNames.OrderBy(x => x, StringComparer.Ordinal))}.",
                "IncrementalRoslynDocumentUpdate")
        };

        var (selectedWarmAll, _, warmTestProjects, warmTestSupportProjects) =
            SelectProjects(updatedAllProjects, warm.ProjectMetaByPath, options, diagnostics);
        var selectedDirty = selectedWarmAll
            .Where(p => dirtyProjectNames.Contains(p.NormalizedName))
            .ToList();

        if (selectedDirty.Count < dirtyProjectNames.Count)
        {
            // Safety fallback if any expected dirty project was missing from warm solution
            return null;
        }

        var knownTypeIds = preservedNodes
            .Where(n => n.Kind == NodeKind.Type)
            .Select(n => n.Id)
            .ToHashSet(StringComparer.Ordinal);
        var knownMethodIds = preservedNodes
            .Where(n => n.Kind == NodeKind.Method)
            .Select(n => n.Id)
            .ToHashSet(StringComparer.Ordinal);

        var compiledDirty = await CompileAndCollectSymbolsAsync(
            selectedDirty, options, diagnostics, knownTypeIds, knownMethodIds, cancellationToken);

        var newNodes = new List<CodeNode>(preservedNodes);
        var edgeAggregator = new Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)>();
        var projectInfos = new List<ModelProjectInfo>(unaffectedProjects);
        var namespacesSeen = new HashSet<string>(StringComparer.Ordinal);
        var diRegistrations = new List<DiRegistration>(preservedDi);
        var analyzerDiagnostics = new List<AnalyzerDiagnostic>(preservedAnalyzerDiags);

        foreach (var typeNode in preservedNodes.Where(n => n.Kind == NodeKind.Type))
        {
            var ns = typeNode.Namespace ?? "(global)";
            if (namespacesSeen.Add(ns))
            {
                newNodes.Add(CreateNamespaceNode(ns, typeNode.ProjectName));
            }
        }

        var (_, dirtyAnalyzersLoaded) = await ExtractGraphElementsAsync(
            compiledDirty, warm.ProjectMetaByPath, warmTestProjects, warmTestSupportProjects, options, rootDir,
            knownTypeIds, knownMethodIds, newNodes, edgeAggregator, projectInfos, namespacesSeen,
            diRegistrations, analyzerDiagnostics, diagnostics, cancellationToken);

        var mergedEdges = new List<DependencyEdge>(preservedEdges.Count + edgeAggregator.Count);
        mergedEdges.AddRange(preservedEdges);
        foreach (var kvp in edgeAggregator)
        {
            mergedEdges.Add(new DependencyEdge(kvp.Key.From, kvp.Key.To, kvp.Key.Kind, kvp.Value.Location, kvp.Value.Weight));
        }

        var graph = new CodeGraph(
            rootDir, newNodes, mergedEdges, projectInfos, diagnostics, diRegistrations, analyzerDiagnostics,
            loadStatus: previousSnapshot.Graph.LoadStatus,
            excludedTestProjectCount: previousSnapshot.Graph.ExcludedTestProjectCount,
            methodsIncluded: options.IncludeMethods,
            analyzersLoadedCount: previousSnapshot.Graph.AnalyzersLoadedCount ?? dirtyAnalyzersLoaded);
        return CreateSnapshot(resolvedTarget, graph);
    }

    private void EvictWarmSession(string warmKey)
    {
        if (_warmSessions.TryRemove(warmKey, out var old))
        {
            old.Release();
        }
    }

    private static string NormalizeWarmKey(string resolvedTarget, ExtractionOptions options)
    {
        var cats = options.AnalyzerCategories is { Count: > 0 }
            ? string.Join(",", options.AnalyzerCategories.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
            : string.Empty;
        return $"{resolvedTarget}|tests={options.IncludeTestProjects}|gen={options.IncludeGeneratedCode}|methods={options.IncludeMethods}|tfm={options.TargetFramework ?? string.Empty}|analyzers={options.IncludeAnalyzerDiagnostics}|cats={cats}";
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var kvp in _warmSessions.ToArray())
        {
            if (_warmSessions.TryRemove(kvp.Key, out var session))
            {
                session.Release();
            }
        }
    }

    /// <summary>
    /// Ref-counted warm MSBuildWorkspace holder: never disposes workspaces while active incremental readers hold a lease.
    /// </summary>
    private sealed class WarmWorkspaceSession(
        List<MSBuildWorkspace> workspaces,
        List<Solution> solutions,
        Dictionary<string, CsprojMetadata> projectMetaByPath)
    {
        private int _refCount = 1; // Initial reference owned by _warmSessions dictionary

        public object SyncLock { get; } = new();
        public List<Solution> Solutions { get; } = solutions;
        public Dictionary<string, CsprojMetadata> ProjectMetaByPath { get; } = projectMetaByPath;

        public bool TryAcquire()
        {
            while (true)
            {
                var current = Volatile.Read(ref _refCount);
                if (current <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _refCount) == 0)
            {
                lock (SyncLock)
                {
                    foreach (var ws in workspaces)
                    {
                        try { ws.Dispose(); } catch { }
                    }
                    workspaces.Clear();
                    Solutions.Clear();
                }
            }
        }
    }

    private sealed record SelectedProject(Project Project, string NormalizedName, string? TargetFramework);
    private sealed record CompiledProject(SelectedProject Selected, Compilation Compilation, List<INamedTypeSymbol> Types, IMethodSymbol? EntryPointMethod);
}
