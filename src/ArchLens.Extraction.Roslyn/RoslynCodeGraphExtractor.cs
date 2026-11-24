using System.Xml.Linq;
using ArchLens.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using ModelProjectInfo = ArchLens.Model.ProjectInfo;
using ModelTypeKind = ArchLens.Model.TypeKind;

namespace ArchLens.Extraction.Roslyn;

/// <summary>
/// Extracts a canonical CodeGraph from a C# .slnx, .sln, .csproj, or directory using Roslyn semantic analysis.
/// Supports multi-targeted TFM selection, optional method-level call graphs, cognitive complexity, LCOM4,
/// and selective per-project incremental re-extraction.
/// </summary>
public sealed class RoslynCodeGraphExtractor : IIncrementalCodeGraphExtractor
{
    public string Name => "Roslyn";

    public bool CanHandle(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return false;
        }

        if (Directory.Exists(targetPath))
        {
            return true;
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

        try
        {
            var (projects, allWorkspaceProjects) = await LoadTargetProjectsAsync(
                resolvedTarget, options, diagnostics, workspaces, cancellationToken);

            var projectMetaByPath = ReadAllCsprojMetadata(allWorkspaceProjects);
            var selectedProjects = SelectProjects(projects, projectMetaByPath, options, diagnostics);

            var knownTypeIds = new HashSet<string>(StringComparer.Ordinal);
            var knownMethodIds = new HashSet<string>(StringComparer.Ordinal);

            var compiled = await CompileAndCollectSymbolsAsync(
                selectedProjects, options, diagnostics, knownTypeIds, knownMethodIds, cancellationToken);

            var nodes = new List<CodeNode>();
            var edgeAggregator = new Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)>();
            var projectInfos = new List<ModelProjectInfo>();
            var namespacesSeen = new HashSet<string>(StringComparer.Ordinal);

            ExtractGraphElements(
                compiled, projectMetaByPath, options, rootDir,
                knownTypeIds, knownMethodIds, nodes, edgeAggregator, projectInfos, namespacesSeen, cancellationToken);

            var edges = edgeAggregator
                .Select(kvp => new DependencyEdge(kvp.Key.From, kvp.Key.To, kvp.Key.Kind, kvp.Value.Location, kvp.Value.Weight))
                .ToList();

            var graph = new CodeGraph(rootDir, nodes, edges, projectInfos, diagnostics);
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

        if (HasStructuralChange(changedFilePaths))
        {
            return await ExtractAsync(targetPath, options, cancellationToken);
        }

        var (dirtyProjectNames, hasUnmappedProductionChange) =
            FindDirtyProjectsWithDependents(previousSnapshot.Graph, rootDir, changedFilePaths, options.IncludeTestProjects);

        if (hasUnmappedProductionChange)
        {
            return await ExtractAsync(targetPath, options, cancellationToken);
        }

        if (dirtyProjectNames.Count == 0)
        {
            // All changes belonged exclusively to excluded test projects — snapshot is unchanged.
            return previousSnapshot;
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
            var selectedDirty = SelectProjects(loadedDirty, projectMetaByPath, options, diagnostics)
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

            var newNodes = new List<CodeNode>(preservedNodes);
            var edgeAggregator = new Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)>();
            var projectInfos = new List<ModelProjectInfo>(unaffectedProjects);
            var namespacesSeen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var typeNode in preservedNodes.Where(n => n.Kind == NodeKind.Type))
            {
                var ns = typeNode.Namespace ?? "(global)";
                if (namespacesSeen.Add(ns))
                {
                    newNodes.Add(CreateNamespaceNode(ns, typeNode.ProjectName));
                }
            }

            ExtractGraphElements(
                compiledDirty, projectMetaByPath, options, rootDir,
                knownTypeIds, knownMethodIds, newNodes, edgeAggregator, projectInfos, namespacesSeen, cancellationToken);

            var mergedEdges = new List<DependencyEdge>(preservedEdges.Count + edgeAggregator.Count);
            mergedEdges.AddRange(preservedEdges);
            foreach (var kvp in edgeAggregator)
            {
                mergedEdges.Add(new DependencyEdge(kvp.Key.From, kvp.Key.To, kvp.Key.Kind, kvp.Value.Location, kvp.Value.Weight));
            }

            var graph = new CodeGraph(rootDir, newNodes, mergedEdges, projectInfos, diagnostics);
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
        var workspace = MSBuildWorkspace.Create(properties);
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            var sev = e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure
                ? ExtractionDiagnosticSeverity.Warning
                : ExtractionDiagnosticSeverity.Info;
            diagnostics.Add(new ExtractionDiagnostic(sev, e.Diagnostic.Message, "MSBuildWorkspace"));
        });
        return workspace;
    }

    private static Dictionary<string, CsprojMetadata> ReadAllCsprojMetadata(IReadOnlyList<Project> projects) =>
        projects
            .Where(p => !string.IsNullOrWhiteSpace(p.FilePath))
            .GroupBy(p => p.FilePath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => CsprojMetadataReader.Read(g.Key), StringComparer.OrdinalIgnoreCase);

    private static List<SelectedProject> SelectProjects(
        IReadOnlyList<Project> projects,
        Dictionary<string, CsprojMetadata> projectMetaByPath,
        ExtractionOptions options,
        List<ExtractionDiagnostic> diagnostics)
    {
        var selected = new List<SelectedProject>();

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

            if (options.IncludeTestProjects || !CsprojMetadataReader.IsTestProject(chosen.NormalizedName, chosen.Project.FilePath, projectMetaByPath))
            {
                selected.Add(chosen);
            }
        }

        return selected;
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
            foreach (var type in types)
            {
                knownTypeIds.Add(SymbolDependencyExtractor.GetTypeId(type));
                if (options.IncludeMethods)
                {
                    foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(SymbolDependencyExtractor.IsTrackableMethod))
                    {
                        knownMethodIds.Add(SymbolDependencyExtractor.GetMethodId(method));
                    }
                }
            }

            result.Add(new CompiledProject(item, compilation, types));
        }

        return result;
    }

    private static void ExtractGraphElements(
        List<CompiledProject> compiledProjects,
        Dictionary<string, CsprojMetadata> projectMetaByPath,
        ExtractionOptions options,
        string rootDir,
        HashSet<string> knownTypeIds,
        HashSet<string> knownMethodIds,
        List<CodeNode> nodes,
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> edgeAggregator,
        List<ModelProjectInfo> projectInfos,
        HashSet<string> namespacesSeen,
        CancellationToken cancellationToken)
    {
        foreach (var (selected, compilation, types) in compiledProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var project = selected.Project;
            var projectName = selected.NormalizedName;
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

            var relProjectPath = SymbolDependencyExtractor.MakeRelativePath(rootDir, project.FilePath ?? projectName);
            projectInfos.Add(new ModelProjectInfo(
                projectName,
                relProjectPath,
                selected.TargetFramework ?? meta.TargetFramework,
                projectRefNames,
                meta.PackageReferences,
                types.Count,
                project.Documents.Count()));

            nodes.Add(new CodeNode(
                Id: $"project:{projectName}",
                Name: projectName,
                FullName: projectName,
                Kind: NodeKind.Project,
                ProjectName: projectName,
                Location: new SourceLocation(relProjectPath, 1, 1)));

            foreach (var refName in projectRefNames)
            {
                SymbolDependencyExtractor.AddEdge(edgeAggregator, $"project:{projectName}", $"project:{refName}", DependencyKind.ProjectReference, null);
            }

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

            foreach (var type in types)
            {
                ExtractSingleType(
                    type, projectName, options, rootDir, knownTypeIds, knownMethodIds,
                    GetSemanticModel, nodes, edgeAggregator, namespacesSeen);
            }
        }
    }

    private static void ExtractSingleType(
        INamedTypeSymbol type,
        string projectName,
        ExtractionOptions options,
        string rootDir,
        HashSet<string> knownTypeIds,
        HashSet<string> knownMethodIds,
        Func<SyntaxTree, SemanticModel> getSemanticModel,
        List<CodeNode> nodes,
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> edgeAggregator,
        HashSet<string> namespacesSeen)
    {
        var typeId = SymbolDependencyExtractor.GetTypeId(type);
        var ns = type.ContainingNamespace is { IsGlobalNamespace: false }
            ? type.ContainingNamespace.ToDisplayString()
            : "(global)";

        if (namespacesSeen.Add(ns))
        {
            nodes.Add(CreateNamespaceNode(ns, projectName));
        }

        var primaryLocation = SymbolDependencyExtractor.GetPrimaryLocation(type, rootDir);
        var (lineCount, totalComplexity, maxMethodComplexity, totalCognitive, maxMethodCognitive) =
            SymbolDependencyExtractor.ComputeTypeSyntaxStats(type);
        var lcom4 = Lcom4Calculator.Calculate(type, getSemanticModel);

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
            ["maxMethodCognitiveComplexity"] = maxMethodCognitive.ToString()
        };

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
            Lcom4: lcom4));

        SymbolDependencyExtractor.ExtractTypeDependencies(type, typeId, knownTypeIds, rootDir, getSemanticModel, edgeAggregator);

        if (options.IncludeMethods)
        {
            foreach (var method in type.GetMembers().OfType<IMethodSymbol>().Where(SymbolDependencyExtractor.IsTrackableMethod))
            {
                var impl = SymbolDependencyExtractor.ResolveImplementation(method);
                var methodId = SymbolDependencyExtractor.GetMethodId(impl);
                var (mLines, mCc, mCog) = SymbolDependencyExtractor.ComputeMethodSyntaxStats(impl);
                var methodMeta = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["containingType"] = typeId,
                    ["methodKind"] = impl.MethodKind.ToString()
                };

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
                   fileName.Equals("global.json", StringComparison.OrdinalIgnoreCase);
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
            .Select(p => Path.GetFullPath(Path.Combine(baseDir, p!)))
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

    private static bool IsGeneratedOnly(INamedTypeSymbol type)
    {
        var sourceLocations = type.Locations.Where(l => l.IsInSource).ToList();
        if (sourceLocations.Count == 0)
        {
            return true;
        }

        return sourceLocations.All(loc =>
        {
            var path = loc.SourceTree?.FilePath ?? string.Empty;
            return path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) ||
                   path.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase);
        });
    }

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

    private sealed record SelectedProject(Project Project, string NormalizedName, string? TargetFramework);
    private sealed record CompiledProject(SelectedProject Selected, Compilation Compilation, List<INamedTypeSymbol> Types);
}
