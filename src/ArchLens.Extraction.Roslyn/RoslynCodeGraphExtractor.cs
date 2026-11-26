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
            // Handle directories containing source solution/project files (top-level or recursively under e.g. src/)
            return HasSourceTargetInDirectory(targetPath);
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
        var rootDir = Directory.Exists(resolvedTarget)
            ? resolvedTarget
            : Path.GetDirectoryName(resolvedTarget) ?? Directory.GetCurrentDirectory();
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

            if (options.IncludeAnalyzerDiagnostics && options.AnalyzerCategories is { Count: > 0 })
            {
                // Validate requested categories against project AnalyzerReferences BEFORE compiling or running analyzers
                RoslynAnalyzerRunner.ValidateRequestedCategories(
                    selectedProjects.Select(s => s.Project),
                    options.AnalyzerCategories);
            }

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

            var (razorComponentCount, analyzersLoadedCount, availableAnalyzerCategories) = await ExtractGraphElementsAsync(
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
                analyzersLoadedCount: analyzersLoadedCount,
                availableAnalyzerCategories: availableAnalyzerCategories);
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

            var (_, dirtyAnalyzersLoaded, dirtyAnalyzerCategories) = await ExtractGraphElementsAsync(
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
                analyzersLoadedCount: previousSnapshot.Graph.AnalyzersLoadedCount ?? dirtyAnalyzersLoaded,
                availableAnalyzerCategories: previousSnapshot.Graph.AvailableAnalyzerCategories ?? dirtyAnalyzerCategories);
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
        if (Directory.Exists(resolvedTarget))
        {
            var recursiveCsprojs = FindRecursiveSourceFiles(resolvedTarget, "*.csproj");
            if (recursiveCsprojs.Count == 0)
            {
                throw new FileNotFoundException($"No .slnx, .sln, or .csproj file found in directory '{resolvedTarget}'.", resolvedTarget);
            }
            return await OpenCsprojFilesAsync(recursiveCsprojs, options.TargetFramework, diagnostics, workspaces, ct);
        }

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
                var fullCsproj = Path.GetFullPath(path);
                var existing = tfmWs.CurrentSolution.Projects.FirstOrDefault(p =>
                    p.FilePath is not null && string.Equals(Path.GetFullPath(p.FilePath), fullCsproj, StringComparison.OrdinalIgnoreCase));
                opened.Add(existing ?? await tfmWs.OpenProjectAsync(fullCsproj, cancellationToken: ct));
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
                var fullCsproj = Path.GetFullPath(path);
                var existing = defaultWs.CurrentSolution.Projects.FirstOrDefault(p =>
                    p.FilePath is not null && string.Equals(Path.GetFullPath(p.FilePath), fullCsproj, StringComparison.OrdinalIgnoreCase));
                opened.Add(existing ?? await defaultWs.OpenProjectAsync(fullCsproj, cancellationToken: ct));
            }
            allProjects.AddRange(defaultWs.CurrentSolution.Projects);
        }

        // Include transitively referenced <ProjectReference> projects loaded by MSBuildWorkspace so single-.csproj
        // checks see cross-project dependency edges consistently with solution (.sln/.slnx) checks.
        var effectiveProjects = allProjects.Count > 0 ? allProjects : opened;
        return (effectiveProjects, allProjects);
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
        var matchedRequestedTfmCount = 0;
        var availableTfms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in projects.GroupBy(p => p.FilePath ?? p.Name, StringComparer.OrdinalIgnoreCase))
        {
            var meta = group.First().FilePath is { } fp && projectMetaByPath.TryGetValue(fp, out var m)
                ? m
                : null;
            if (meta is not null)
            {
                foreach (var tf in meta.TargetFrameworks)
                {
                    if (!string.IsNullOrWhiteSpace(tf)) availableTfms.Add(tf);
                }
            }

            var candidates = group
                .Select(p =>
                {
                    var tfm = CsprojMetadataReader.DetectProjectTargetFramework(p, meta);
                    if (!string.IsNullOrWhiteSpace(tfm)) availableTfms.Add(tfm);
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
                    matchedRequestedTfmCount++;
                }
                else
                {
                    chosen = candidates[0];
                    diagnostics.Add(new ExtractionDiagnostic(
                        ExtractionDiagnosticSeverity.Warning,
                        $"Requested TargetFramework '{options.TargetFramework}' did not match '{chosen.NormalizedName}' (loaded '{chosen.TargetFramework ?? "default"}'); analyzed '{chosen.TargetFramework ?? "default"}' instead.",
                        chosen.NormalizedName));
                }
            }
            else
            {
                chosen = candidates[0];
            }

            chosenAll.Add(chosen);
        }

        if (!string.IsNullOrWhiteSpace(options.TargetFramework) &&
            chosenAll.Count > 0 &&
            matchedRequestedTfmCount == 0)
        {
            var availText = availableTfms.Count > 0
                ? $" Available target frameworks: {string.Join(", ", availableTfms.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))}."
                : string.Empty;
            throw new ArgumentException(
                $"Requested targetFramework '{options.TargetFramework}' did not match any project in the target.{availText}",
                nameof(options.TargetFramework));
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
            if (!string.IsNullOrWhiteSpace(projPath) && TryFindMissingUnconditionalImport(projPath, out var missingImport))
            {
                var importMsg = $"MSB4019: The imported project \"{missingImport}\" was not found. Confirm that the expression in the Import declaration is correct, and that the file exists on disk. [{projPath}]";
                diagnostics.Add(new ExtractionDiagnostic(ExtractionDiagnosticSeverity.Warning, importMsg, "MSBuildWorkspace"));
            }

            var workspaceFailure = diagnostics.FirstOrDefault(d =>
                (d.Source == "MSBuildWorkspace" &&
                 d.Severity >= ExtractionDiagnosticSeverity.Warning &&
                 ((!string.IsNullOrWhiteSpace(projPath) && d.Message.Contains(projPath, StringComparison.OrdinalIgnoreCase)) ||
                  (!string.IsNullOrWhiteSpace(projFileName) && d.Message.Contains(projFileName, StringComparison.OrdinalIgnoreCase)))) ||
                ((d.Source == "RoslynSyntax" || d.Source == "RoslynCompiler") &&
                 d.Severity >= ExtractionDiagnosticSeverity.Error &&
                 d.Message.StartsWith($"[{sel.NormalizedName}]", StringComparison.OrdinalIgnoreCase)));

            if (workspaceFailure is not null)
            {
                degradedSet.Add(sel.NormalizedName);
                degradedDetails.Add(workspaceFailure.Source switch
                {
                    "RoslynSyntax" => new DegradedProjectInfo(sel.NormalizedName, "SYNTAX_ERROR", workspaceFailure.Message),
                    "RoslynCompiler" => new DegradedProjectInfo(sel.NormalizedName, "COMPILE_ERROR", workspaceFailure.Message),
                    _ => ParseDegradedProjectInfo(sel.NormalizedName, workspaceFailure.Message)
                });
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
            if (degradedList is { Count: > 0 })
            {
                var allCompileOrSyntax = degradedDetails.Count > 0 &&
                                         degradedDetails.All(d => d.ErrorCode is "SYNTAX_ERROR" or "COMPILE_ERROR");
                var hasRestoreOrMissingRef = degradedDetails.Any(d => d.ErrorCode is "RESTORE_REQUIRED" or "MISSING_ASSEMBLY");
                note = allCompileOrSyntax
                    ? $"{degradedList.Count} of {expected} project(s) had C# syntax/compile errors ({string.Join(", ", degradedList)}); type and edge counts may be incomplete. Fix the compilation errors shown in diagnostics."
                    : hasRestoreOrMissingRef
                        ? $"{degradedList.Count} of {expected} project(s) had MSBuild load failures ({string.Join(", ", degradedList)}); type and edge counts may be incomplete. Run 'dotnet restore' to resolve missing packages."
                        : $"{degradedList.Count} of {expected} project(s) had MSBuild load/evaluation errors ({string.Join(", ", degradedList)}); type and edge counts may be incomplete. Inspect the MSBuild diagnostics and fix project evaluation errors.";
            }
            else
            {
                note = "MSBuild reported workspace load warnings; type and edge counts may be incomplete.";
            }
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

    private static bool TryFindMissingUnconditionalImport(string csprojPath, out string missingImportPath)
    {
        missingImportPath = string.Empty;
        try
        {
            if (!File.Exists(csprojPath)) return false;
            var projDir = Path.GetDirectoryName(Path.GetFullPath(csprojPath)) ?? string.Empty;
            var doc = System.Xml.Linq.XDocument.Load(csprojPath);
            if (doc.Root is null) return false;
            foreach (var imp in doc.Root.Descendants().Where(e => e.Name.LocalName == "Import"))
            {
                if (imp.Attribute("Condition") is not null || imp.Attribute("Sdk") is not null)
                {
                    continue;
                }
                var projAttr = imp.Attribute("Project")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(projAttr) ||
                    projAttr.Contains('$', StringComparison.Ordinal) ||
                    projAttr.Contains('*', StringComparison.Ordinal))
                {
                    continue;
                }
                var resolved = Path.IsPathRooted(projAttr)
                    ? projAttr
                    : Path.GetFullPath(Path.Combine(projDir, projAttr));
                if (!File.Exists(resolved))
                {
                    missingImportPath = resolved;
                    return true;
                }
            }
        }
        catch
        {
            // Ignore malformed XML; MSBuildWorkspace handles syntax errors separately
        }
        return false;
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

            // Record C# parse & semantic compile errors so uncompilable projects are never silently reported as healthy
            var syntaxErrorsRecorded = 0;
            var syntaxErrorsTotal = 0;
            foreach (var parseDiag in compilation.GetParseDiagnostics(cancellationToken))
            {
                if (parseDiag.Severity != DiagnosticSeverity.Error)
                {
                    continue;
                }
                if (!options.IncludeGeneratedCode &&
                    parseDiag.Location.SourceTree is { } errTree &&
                    GeneratedCodeClassifier.IsGeneratedSyntaxTree(errTree))
                {
                    continue;
                }
                syntaxErrorsTotal++;
                if (syntaxErrorsRecorded >= 15)
                {
                    continue;
                }

                var span = parseDiag.Location.GetLineSpan();
                var locPart = span.IsValid && !string.IsNullOrWhiteSpace(span.Path)
                    ? $" at {Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1}"
                    : string.Empty;
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionDiagnosticSeverity.Error,
                    $"[{item.NormalizedName}] {parseDiag.Id}: {parseDiag.GetMessage()}{locPart}",
                    "RoslynSyntax"));
                syntaxErrorsRecorded++;
            }
            if (syntaxErrorsTotal > syntaxErrorsRecorded)
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionDiagnosticSeverity.Error,
                    $"[{item.NormalizedName}] ... ({syntaxErrorsTotal - syntaxErrorsRecorded} additional syntax error(s) omitted; total {syntaxErrorsTotal} in project)",
                    "RoslynSyntax"));
            }

            if (syntaxErrorsTotal == 0)
            {
                var compileErrorsRecorded = 0;
                var compileErrorsTotal = 0;
                foreach (var compDiag in compilation.GetDeclarationDiagnostics(cancellationToken)
                             .Concat(compilation.GetMethodBodyDiagnostics(cancellationToken)))
                {
                    if (compDiag.Severity != DiagnosticSeverity.Error ||
                        !compDiag.Location.IsInSource ||
                        !compDiag.Id.StartsWith("CS", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (!options.IncludeGeneratedCode &&
                        compDiag.Location.SourceTree is { } errTree &&
                        GeneratedCodeClassifier.IsGeneratedSyntaxTree(errTree))
                    {
                        continue;
                    }
                    var msg = compDiag.GetMessage();
                    // Ignore WPF/XAML design-time missing InitializeComponent when obj/*.g.cs is absent
                    if (compDiag.Id == "CS0103" && msg.Contains("InitializeComponent", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    compileErrorsTotal++;
                    if (compileErrorsRecorded >= 15)
                    {
                        continue;
                    }

                    var span = compDiag.Location.GetLineSpan();
                    var locPart = span.IsValid && !string.IsNullOrWhiteSpace(span.Path)
                        ? $" at {Path.GetFileName(span.Path)}:{span.StartLinePosition.Line + 1}"
                        : string.Empty;
                    diagnostics.Add(new ExtractionDiagnostic(
                        ExtractionDiagnosticSeverity.Error,
                        $"[{item.NormalizedName}] {compDiag.Id}: {msg}{locPart}",
                        "RoslynCompiler"));
                    compileErrorsRecorded++;
                }
                if (compileErrorsTotal > compileErrorsRecorded)
                {
                    diagnostics.Add(new ExtractionDiagnostic(
                        ExtractionDiagnosticSeverity.Error,
                        $"[{item.NormalizedName}] ... ({compileErrorsTotal - compileErrorsRecorded} additional compiler error(s) omitted; total {compileErrorsTotal} in project)",
                        "RoslynCompiler"));
                }
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

    private static async Task<(int RazorComponentCount, int? AnalyzersLoadedCount, IReadOnlyList<string>? AvailableAnalyzerCategories)> ExtractGraphElementsAsync(
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
        var allXamlEventHandlers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
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
            foreach (var (xamlTypeId, handlers) in xamlScan.EventHandlerNamesByTypeId)
            {
                if (!allXamlEventHandlers.TryGetValue(xamlTypeId, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    allXamlEventHandlers[xamlTypeId] = set;
                }
                set.UnionWith(handlers);
            }

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
        var distinctAnalyzerCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Pre-collect cross-type method/constructor calls made inside property/event bodies and field initializers
        // plus Roslyn method calls targeting .razor component methods (e.g. `_chatPanel.ScrollToBottomAsync()` from `.razor.cs`).
        var calledFromMemberBodyByTarget = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var razorMethodCallersFromRoslyn = new Dictionary<(string TargetTypeId, string MethodName), HashSet<string>>();
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

                    if (allRazorComponentIds.Count > 0)
                    {
                        CollectRoslynCallsToRazorComponents(type, callerTypeId, allRazorComponentIds, getSemanticModel, razorMethodCallersFromRoslyn);
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
                var (diags, loadedTypes, availableCats) = await RoslynAnalyzerRunner.RunAsync(
                    project, compilation, projectName, rootDir, options.AnalyzerCategories, diagnostics, cancellationToken,
                    includeGenerated: options.IncludeGeneratedCode,
                    elevateSeverity: options.ElevateAnalyzerSeverity);
                distinctAnalyzerTypes.UnionWith(loadedTypes);
                distinctAnalyzerCategories.UnionWith(availableCats);
                analyzerDiagnostics.AddRange(diags);
            }

            foreach (var type in types)
            {
                emittedRoslynTypeIds.Add(GetCanonicalTypeNodeId(type, projectName));
                ExtractSingleType(
                    type, projectName, outputKind, isTestProject, isTestSupportProject,
                    entryPointMethod, allMinimalApiHandlers, allUiEntryPoints, allXamlClasses, allXamlEventHandlers, allRazorComponentIds,
                    calledFromMemberBodyByTarget,
                    options, rootDir, knownTypeIds, knownMethodIds,
                    getSemanticModel, nodes, edgeAggregator, namespacesSeen);
            }
        }

        // Pass 2c: Emit markup-only Razor (.razor) component nodes AND merge inline @code metrics/methods into hybrid (.razor + .razor.cs) partial types
        var existingTypeIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        if (allRazorComponents.Count > 0)
        {
            for (var i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].Kind == NodeKind.Type)
                {
                    existingTypeIndexById[nodes[i].Id] = i;
                }
            }
        }

        foreach (var comp in allRazorComponents)
        {
            var codeStats = RazorMetadataScanner.AnalyzeCodeBlocks(comp);
            var isHybrid = emittedRoslynTypeIds.Contains(comp.TypeId);

            if (isHybrid)
            {
                if (existingTypeIndexById.TryGetValue(comp.TypeId, out var typeIdx))
                {
                    var existing = nodes[typeIdx];
                    var mergedMeta = existing.Metadata is not null
                        ? new Dictionary<string, string>(existing.Metadata, StringComparer.Ordinal)
                        : new Dictionary<string, string>(StringComparer.Ordinal);
                    mergedMeta["isRazorComponent"] = "true";
                    if (comp.IsEntryPoint)
                    {
                        mergedMeta["isUiEntryPoint"] = "true";
                    }

                    var prevMaxCc = mergedMeta.TryGetValue("maxMethodComplexity", out var rawMaxCc) && int.TryParse(rawMaxCc, out var pmc) ? pmc : 0;
                    var prevMaxCog = mergedMeta.TryGetValue("maxMethodCognitiveComplexity", out var rawMaxCog) && int.TryParse(rawMaxCog, out var pmcog) ? pmcog : 0;
                    mergedMeta["maxMethodComplexity"] = Math.Max(prevMaxCc, codeStats.MaxMethodCyclomatic).ToString();
                    mergedMeta["maxMethodCognitiveComplexity"] = Math.Max(prevMaxCog, codeStats.MaxMethodCognitive).ToString();

                    nodes[typeIdx] = existing with
                    {
                        LineCount = existing.LineCount + comp.LineCount,
                        CyclomaticComplexity = existing.CyclomaticComplexity + codeStats.TotalCyclomatic,
                        CognitiveComplexity = existing.CognitiveComplexity + codeStats.TotalCognitive,
                        MemberCount = existing.MemberCount + codeStats.MemberCount,
                        Lcom4 = existing.Lcom4 == 0 && codeStats.Lcom4 > 0 ? codeStats.Lcom4 : existing.Lcom4,
                        Metadata = mergedMeta
                    };
                }
            }
            else
            {
                if (namespacesSeen.Add(comp.Namespace))
                {
                    nodes.Add(CreateNamespaceNode(comp.Namespace, comp.ProjectName));
                }

                var compMeta = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["outputKind"] = comp.OutputKind,
                    ["isRazorComponent"] = "true",
                    ["baseTypes"] = "Microsoft.AspNetCore.Components.ComponentBase",
                    ["maxMethodComplexity"] = codeStats.MaxMethodCyclomatic.ToString(),
                    ["maxMethodCognitiveComplexity"] = codeStats.MaxMethodCognitive.ToString()
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
                    CyclomaticComplexity: codeStats.TotalCyclomatic,
                    MemberCount: codeStats.MemberCount,
                    Metadata: compMeta,
                    CognitiveComplexity: codeStats.TotalCognitive,
                    Lcom4: codeStats.Lcom4));
            }

            if (options.IncludeMethods && codeStats.Methods.Count > 0)
            {
                var emittedMethodIds = new HashSet<string>(StringComparer.Ordinal);
                var razorMethodIdsByName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var existingNode in nodes)
                {
                    if (existingNode.Kind == NodeKind.Method &&
                        string.Equals(existingNode.ContainingTypeId, comp.TypeId, StringComparison.Ordinal))
                    {
                        emittedMethodIds.Add(existingNode.Id);
                        if (!razorMethodIdsByName.TryGetValue(existingNode.Name, out var existingList))
                        {
                            existingList = [];
                            razorMethodIdsByName[existingNode.Name] = existingList;
                        }
                        existingList.Add(existingNode.Id);
                    }
                }

                var newlyAddedMethods = new List<RazorCodeMethodInfo>();
                foreach (var rm in codeStats.Methods)
                {
                    if (!emittedMethodIds.Add(rm.MethodId))
                    {
                        continue;
                    }
                    knownMethodIds.Add(rm.MethodId);
                    newlyAddedMethods.Add(rm);
                    if (!razorMethodIdsByName.TryGetValue(rm.Name, out var list))
                    {
                        list = [];
                        razorMethodIdsByName[rm.Name] = list;
                    }
                    list.Add(rm.MethodId);

                    var mMeta = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["containingType"] = comp.TypeId,
                        ["methodKind"] = "Ordinary",
                        ["outputKind"] = comp.OutputKind
                    };
                    if (rm.IsOverride || rm.IsBlazorLifecycle)
                    {
                        mMeta["isOverride"] = "true";
                    }

                    var extraCallers = new HashSet<string>(StringComparer.Ordinal);
                    if (codeStats.CalledFromPropertiesAndFields.Contains(rm.Name))
                    {
                        extraCallers.Add(comp.TypeId);
                        mMeta["calledFromMemberBody"] = "true";
                    }
                    if (razorMethodCallersFromRoslyn.TryGetValue((comp.TypeId, rm.Name), out var roslynCallers))
                    {
                        extraCallers.UnionWith(roslynCallers);
                    }
                    if (extraCallers.Count > 0)
                    {
                        mMeta["calledFromMemberBodyBy"] = string.Join(";", extraCallers);
                    }

                    nodes.Add(new CodeNode(
                        Id: rm.MethodId,
                        Name: rm.Name,
                        FullName: rm.MethodId,
                        Kind: NodeKind.Method,
                        ProjectName: comp.ProjectName,
                        Namespace: comp.Namespace,
                        Accessibility: rm.Accessibility,
                        IsAbstract: false,
                        IsStatic: rm.IsStatic,
                        Location: new SourceLocation(comp.RelativePath, rm.StartLine, 1),
                        LineCount: rm.LineCount,
                        CyclomaticComplexity: rm.CyclomaticComplexity,
                        Metadata: mMeta,
                        CognitiveComplexity: rm.CognitiveComplexity,
                        ContainingTypeId: comp.TypeId));
                }

                // Wire intra-@code calls (and @code -> .razor.cs calls) so helpers called by live @code methods stay reachable
                foreach (var rm in newlyAddedMethods)
                {
                    foreach (var callee in rm.CalledMethodNames)
                    {
                        if (razorMethodIdsByName.TryGetValue(callee, out var targets))
                        {
                            foreach (var targetId in targets)
                            {
                                if (!string.Equals(targetId, rm.MethodId, StringComparison.Ordinal))
                                {
                                    SymbolDependencyExtractor.AddEdge(edgeAggregator, rm.MethodId, targetId, DependencyKind.MethodCall, new SourceLocation(comp.RelativePath, rm.StartLine, 1));
                                }
                            }
                        }
                    }
                }
            }
        }

        // Build project-reference visibility map so Razor components never resolve types from unreferenced projects
        var projectByTypeId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in nodes)
        {
            if (n.Kind == NodeKind.Type && !string.IsNullOrEmpty(n.ProjectName))
            {
                projectByTypeId[n.Id] = n.ProjectName;
            }
        }
        var allowedProjectsByProject = BuildTransitiveProjectVisibility(projectInfos);

        // Pass 2d: Record Razor markup / @inject / @layout / @code type references for ALL .razor components
        RazorMetadataScanner.ResolveAndRecordReferences(
            allRazorComponents, knownTypeIds, edgeAggregator, projectByTypeId, allowedProjectsByProject);
        if (options.IncludeMethods)
        {
            RazorMetadataScanner.ResolveMethodReferences(allRazorComponents, nodes, edgeAggregator);
        }

        return (
            allRazorComponents.Count,
            options.IncludeAnalyzerDiagnostics ? distinctAnalyzerTypes.Count : null,
            options.IncludeAnalyzerDiagnostics
                ? distinctAnalyzerCategories.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList()
                : null);
    }

    private static Dictionary<string, HashSet<string>> BuildTransitiveProjectVisibility(List<ModelProjectInfo> projectInfos)
    {
        var direct = projectInfos.ToDictionary(
            p => p.Name,
            p => p.ProjectReferences,
            StringComparer.OrdinalIgnoreCase);

        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in projectInfos)
        {
            var visible = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { p.Name };
            var queue = new Queue<string>();
            queue.Enqueue(p.Name);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (direct.TryGetValue(cur, out var refs))
                {
                    foreach (var r in refs)
                    {
                        if (visible.Add(r)) queue.Enqueue(r);
                    }
                }
            }
            result[p.Name] = visible;
        }
        return result;
    }

    private static void CollectRoslynCallsToRazorComponents(
        INamedTypeSymbol sourceType,
        string callerTypeId,
        HashSet<string> razorComponentIds,
        Func<SyntaxTree, SemanticModel> getSemanticModel,
        Dictionary<(string TargetTypeId, string MethodName), HashSet<string>> targetCallers)
    {
        foreach (var syntaxRef in sourceType.DeclaringSyntaxReferences)
        {
            if (GeneratedCodeClassifier.IsGeneratedSyntaxTree(syntaxRef.SyntaxTree))
            {
                continue;
            }

            var root = syntaxRef.GetSyntax();
            var semanticModel = getSemanticModel(syntaxRef.SyntaxTree);
            foreach (var inv in root.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>())
            {
                var info = semanticModel.GetSymbolInfo(inv);
                var sym = info.Symbol as IMethodSymbol
                    ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                if (sym?.ContainingType is null)
                {
                    continue;
                }

                var targetTypeId = SymbolDependencyExtractor.GetTypeId(sym.ContainingType);
                if (!razorComponentIds.Contains(targetTypeId))
                {
                    continue;
                }

                var key = (targetTypeId, sym.Name);
                if (!targetCallers.TryGetValue(key, out var callers))
                {
                    callers = new HashSet<string>(StringComparer.Ordinal);
                    targetCallers[key] = callers;
                }
                callers.Add(callerTypeId);
            }
        }
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
        Dictionary<string, HashSet<string>> xamlEventHandlers,
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
            var (chosenCtorParams, allCtorParamSets) = FormatDiConstructorParamTypes(type);
            typeMetadata["ctorParamTypes"] = chosenCtorParams;
            if (allCtorParamSets is not null)
            {
                typeMetadata["ctorParamSets"] = allCtorParamSets;
            }
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

        if (IsDataContainerType(type))
        {
            typeMetadata["isDataType"] = "true";
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
                    // Link override -> base virtual/abstract method so live overrides keep base virtual methods reachable in method-scope dead code
                    for (var baseMethod = impl.OverriddenMethod; baseMethod is not null; baseMethod = baseMethod.OverriddenMethod)
                    {
                        var baseMethodId = SymbolDependencyExtractor.GetMethodId(baseMethod);
                        if (!string.Equals(baseMethodId, methodId, StringComparison.Ordinal) && knownMethodIds.Contains(baseMethodId))
                        {
                            SymbolDependencyExtractor.AddEdge(edgeAggregator, methodId, baseMethodId, DependencyKind.MethodCall, null);
                        }
                    }
                }

                if (impl.ExplicitInterfaceImplementations.Length > 0 || interfaceImplMethods.Contains(impl))
                {
                    methodMeta["implementsInterface"] = "true";
                }

                if (minimalApiHandlers.Contains(methodId) || minimalApiHandlers.Contains(SymbolDependencyExtractor.GetMethodId(impl)))
                {
                    methodMeta["isMinimalApiHandler"] = "true";
                }

                if (!impl.IsStatic &&
                    impl.MethodKind == MethodKind.Ordinary &&
                    ((xamlEventHandlers.TryGetValue(typeId, out var xamlHandlers) && xamlHandlers.Contains(impl.Name)) ||
                     (xamlEventHandlers.TryGetValue(SymbolDependencyExtractor.GetTypeId(type), out var rawXamlHandlers) && rawXamlHandlers.Contains(impl.Name))))
                {
                    methodMeta["isXamlEventHandler"] = "true";
                }

                if (impl.MethodKind == MethodKind.Constructor)
                {
                    if (impl.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax))
                    {
                        methodMeta["isPrimaryConstructor"] = "true";
                    }
                    else if (impl.Parameters.Length == 0 &&
                             impl.DeclaringSyntaxReferences.Any(r =>
                                 r.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.ConstructorDeclarationSyntax { Body.Statements.Count: 0, ExpressionBody: null }))
                    {
                        methodMeta["isEmptyConstructor"] = "true";
                    }
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

    private static bool IsDataContainerType(INamedTypeSymbol type)
    {
        if (type.IsRecord)
        {
            return true;
        }

        if (type.IsStatic || type.IsAbstract ||
            type.TypeKind is not (Microsoft.CodeAnalysis.TypeKind.Class or Microsoft.CodeAnalysis.TypeKind.Struct))
        {
            return false;
        }

        var hasDataMembers = false;
        foreach (var member in type.GetMembers())
        {
            if (member.IsImplicitlyDeclared || member.IsStatic)
            {
                continue;
            }

            if (member is IPropertySymbol or IFieldSymbol)
            {
                hasDataMembers = true;
            }
            else if (member is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation })
            {
                return false;
            }
        }

        return hasDataMembers;
    }

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

    private static (string ChosenParams, string? AllCtorParamSets) FormatDiConstructorParamTypes(INamedTypeSymbol type)
    {
        if (type.IsAbstract || type.IsStatic ||
            type.TypeKind is Microsoft.CodeAnalysis.TypeKind.Interface or Microsoft.CodeAnalysis.TypeKind.Enum or Microsoft.CodeAnalysis.TypeKind.Delegate)
        {
            return (string.Empty, null);
        }

        var ctors = type.InstanceConstructors
            .Where(c => c.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal)
            .ToList();
        if (ctors.Count == 0)
        {
            return (string.Empty, null);
        }

        static string FormatSingleCtor(IMethodSymbol ctor)
        {
            var paramTypeIds = new List<string>();
            foreach (var param in ctor.Parameters)
            {
                var serviceKey = TryGetFromKeyedServicesKey(param);
                CollectInjectedTypeIds(param.Type, paramTypeIds, isCollection: false, serviceKey, isOptional: param.HasExplicitDefaultValue);
            }
            return string.Join(";", paramTypeIds.Distinct(StringComparer.Ordinal));
        }

        var activatorCtor = ctors
            .Where(c => c.GetAttributes().Any(a => a.AttributeClass?.Name == "ActivatorUtilitiesConstructorAttribute"))
            .OrderByDescending(c => c.Parameters.Length)
            .FirstOrDefault();
        if (activatorCtor is not null)
        {
            return (FormatSingleCtor(activatorCtor), null);
        }

        var publicCtors = ctors
            .Where(c => c.DeclaredAccessibility == Accessibility.Public)
            .OrderByDescending(c => c.Parameters.Length)
            .ToList();
        var orderedCandidates = publicCtors.Count > 0
            ? publicCtors
            : ctors.OrderByDescending(c => c.Parameters.Length).ToList();

        var formattedSets = orderedCandidates.Select(FormatSingleCtor).ToList();
        return (formattedSets[0], formattedSets.Count > 1 ? string.Join("||", formattedSets) : null);
    }

    private static string? TryGetFromKeyedServicesKey(IParameterSymbol param)
    {
        foreach (var attr in param.GetAttributes())
        {
            var attrName = attr.AttributeClass?.Name;
            if (!string.Equals(attrName, "FromKeyedServicesAttribute", StringComparison.Ordinal) &&
                !string.Equals(attrName, "FromKeyedServices", StringComparison.Ordinal))
            {
                continue;
            }

            if (attr.ConstructorArguments.Length > 0)
            {
                var arg = attr.ConstructorArguments[0];
                if (arg.Kind != TypedConstantKind.Error)
                {
                    if (arg.IsNull)
                    {
                        return null;
                    }
                    return DiRegistrationExtractor.FormatServiceKeyConstant(arg.Value, arg.Type);
                }
            }

            if (attr.ApplicationSyntaxReference?.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.AttributeSyntax attrSyntax &&
                attrSyntax.ArgumentList?.Arguments.Count > 0)
            {
                var rawExpr = attrSyntax.ArgumentList.Arguments[0].Expression.ToString().Trim();
                if (rawExpr.Equals("null", StringComparison.Ordinal))
                {
                    return null;
                }
                if (rawExpr.Equals("KeyedService.AnyKey", StringComparison.Ordinal) ||
                    rawExpr.EndsWith(".KeyedService.AnyKey", StringComparison.Ordinal))
                {
                    return "<any-key>";
                }
                return rawExpr.Trim('"');
            }

            return "<keyed>";
        }

        return null;
    }

    private static string EscapeServiceKey(string key) =>
        key.Replace("%", "%25", StringComparison.Ordinal)
           .Replace(";", "%3B", StringComparison.Ordinal)
           .Replace("|", "%7C", StringComparison.Ordinal);

    private static void CollectInjectedTypeIds(ITypeSymbol type, List<string> target, bool isCollection, string? serviceKey, bool isOptional = false)
    {
        if (type is not INamedTypeSymbol named)
        {
            return;
        }

        if (named.IsGenericType && named.TypeArguments.Length == 1 && named.Name == "IEnumerable")
        {
            CollectInjectedTypeIds(named.TypeArguments[0], target, isCollection: true, serviceKey, isOptional);
            return;
        }

        var id = SymbolDependencyExtractor.GetConstructedTypeId(named);
        var core = serviceKey is not null ? $"key:{EscapeServiceKey(serviceKey)}|{id}" : id;
        var withCollection = isCollection ? $"many:{core}" : core;
        target.Add(isOptional ? $"opt:{withCollection}" : withCollection);
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
            var ext = Path.GetExtension(fullPath);
            if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    $"The Roslyn backend requires C# source files (.slnx, .sln, .csproj, or a source directory), not a compiled binary ('{fullPath}'). Use backend 'auto' or 'il' instead.");
            }
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

        var csprojs = Directory.GetFiles(fullPath, "*.csproj", SearchOption.TopDirectoryOnly);
        if (csprojs.Length == 1) return csprojs[0];
        if (csprojs.Length > 1) return fullPath;

        // Fall back to recursive discovery (e.g., passing 'src/' subdirectory)
        var nestedSlnx = FindRecursiveSourceFiles(fullPath, "*.slnx");
        if (nestedSlnx.Count == 1) return nestedSlnx[0];

        var nestedSln = FindRecursiveSourceFiles(fullPath, "*.sln");
        if (nestedSln.Count == 1) return nestedSln[0];

        var nestedCsprojs = FindRecursiveSourceFiles(fullPath, "*.csproj");
        if (nestedCsprojs.Count == 1) return nestedCsprojs[0];
        if (nestedCsprojs.Count > 1) return fullPath;

        throw new FileNotFoundException($"No .slnx, .sln, or .csproj file found in directory '{fullPath}'.", fullPath);
    }

    private static bool HasSourceTargetInDirectory(string dir)
    {
        if (Directory.GetFiles(dir, "*.slnx", SearchOption.TopDirectoryOnly).Length > 0 ||
            Directory.GetFiles(dir, "*.sln", SearchOption.TopDirectoryOnly).Length > 0 ||
            Directory.GetFiles(dir, "*.csproj", SearchOption.TopDirectoryOnly).Length > 0)
        {
            return true;
        }

        // If top-level has compiled .dll or .exe binaries and no top-level source files, let IL extractor handle it
        if (Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly).Length > 0 ||
            Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Length > 0)
        {
            return false;
        }

        return FindRecursiveSourceFiles(dir, "*.slnx").Count > 0 ||
               FindRecursiveSourceFiles(dir, "*.sln").Count > 0 ||
               FindRecursiveSourceFiles(dir, "*.csproj").Count > 0;
    }

    private static List<string> FindRecursiveSourceFiles(string rootDir, string pattern)
    {
        var results = new List<string>();
        var stack = new Stack<string>();
        stack.Push(rootDir);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            try
            {
                results.AddRange(Directory.GetFiles(current, pattern, SearchOption.TopDirectoryOnly));
                foreach (var sub in Directory.GetDirectories(current))
                {
                    var name = Path.GetFileName(sub);
                    if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("packages", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    stack.Push(sub);
                }
            }
            catch
            {
                // Skip inaccessible directories during recursive discovery
            }
        }
        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
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

        var (_, dirtyAnalyzersLoaded, dirtyAnalyzerCategories) = await ExtractGraphElementsAsync(
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
            analyzersLoadedCount: previousSnapshot.Graph.AnalyzersLoadedCount ?? dirtyAnalyzersLoaded,
            availableAnalyzerCategories: previousSnapshot.Graph.AvailableAnalyzerCategories ?? dirtyAnalyzerCategories);
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
