using System.Xml.Linq;
using ArchLens.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using ModelProjectInfo = ArchLens.Model.ProjectInfo;
using ModelTypeKind = ArchLens.Model.TypeKind;

namespace ArchLens.Extraction.Roslyn;

/// <summary>
/// Extracts a canonical CodeGraph from a C# .slnx, .sln, .csproj, or directory using Roslyn semantic analysis.
/// </summary>
public sealed class RoslynCodeGraphExtractor : ICodeGraphExtractor
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
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DesignTimeBuild"] = "true"
        };

        using var workspace = MSBuildWorkspace.Create(properties);
        workspace.RegisterWorkspaceFailedHandler(e =>
        {
            var sev = e.Diagnostic.Kind == WorkspaceDiagnosticKind.Failure
                ? ExtractionDiagnosticSeverity.Warning
                : ExtractionDiagnosticSeverity.Info;
            diagnostics.Add(new ExtractionDiagnostic(sev, e.Diagnostic.Message, "MSBuildWorkspace"));
        });

        var projects = await LoadProjectsAsync(workspace, resolvedTarget, diagnostics, cancellationToken);

        // Build map of project metadata from .csproj XML (fast & reliable for PackageReferences / TargetFramework)
        var projectMetaByPath = projects
            .Where(p => !string.IsNullOrWhiteSpace(p.FilePath))
            .GroupBy(p => p.FilePath!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => ReadCsprojMetadata(g.Key), StringComparer.OrdinalIgnoreCase);

        // Deduplicate multi-targeted projects by file path (keep first target framework)
        var uniqueProjects = projects
            .GroupBy(p => p.FilePath ?? p.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        // Filter test projects unless explicitly requested
        var selectedProjects = uniqueProjects
            .Where(p => options.IncludeTestProjects || !IsTestProject(p, projectMetaByPath))
            .ToList();

        // Pass 1: Compile selected projects and collect all internal types
        var compilations = new List<(Project Project, Compilation Compilation)>();
        var knownTypeIds = new HashSet<string>(StringComparer.Ordinal);
        var typeSymbolsByProject = new List<(Project Project, Compilation Compilation, List<INamedTypeSymbol> Types)>();

        foreach (var project in selectedProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(cancellationToken);
            if (compilation is null)
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionDiagnosticSeverity.Warning,
                    $"Could not obtain Roslyn compilation for project '{project.Name}'.",
                    project.Name));
                continue;
            }

            compilations.Add((project, compilation));
            var types = CollectDeclaredTypes(compilation.Assembly.GlobalNamespace, options.IncludeGeneratedCode);
            foreach (var type in types)
            {
                knownTypeIds.Add(GetTypeId(type));
            }
            typeSymbolsByProject.Add((project, compilation, types));
        }

        // Pass 2: Build nodes and edges
        var nodes = new List<CodeNode>();
        var edgeAggregator = new Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)>();
        var projectInfos = new List<ModelProjectInfo>();
        var namespacesSeen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (project, compilation, types) in typeSymbolsByProject)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var meta = project.FilePath is not null && projectMetaByPath.TryGetValue(project.FilePath, out var m)
                ? m
                : new CsprojMetadata(null, [], []);

            var projectRefNames = project.ProjectReferences
                .Select(r => workspace.CurrentSolution.GetProject(r.ProjectId)?.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var relProjectPath = MakeRelativePath(rootDir, project.FilePath ?? project.Name);
            projectInfos.Add(new ModelProjectInfo(
                project.Name,
                relProjectPath,
                meta.TargetFramework,
                projectRefNames,
                meta.PackageReferences,
                types.Count,
                project.Documents.Count()));

            nodes.Add(new CodeNode(
                Id: $"project:{project.Name}",
                Name: project.Name,
                FullName: project.Name,
                Kind: NodeKind.Project,
                ProjectName: project.Name,
                Location: new SourceLocation(relProjectPath, 1, 1)));

            foreach (var refName in projectRefNames)
            {
                AddEdge(edgeAggregator, $"project:{project.Name}", $"project:{refName}", DependencyKind.ProjectReference, null);
            }

            // Map syntax trees to semantic models lazily
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
                var typeId = GetTypeId(type);
                var ns = type.ContainingNamespace is { IsGlobalNamespace: false }
                    ? type.ContainingNamespace.ToDisplayString()
                    : "(global)";

                if (namespacesSeen.Add(ns))
                {
                    nodes.Add(new CodeNode(
                        Id: $"ns:{ns}",
                        Name: ns.Contains('.') ? ns[(ns.LastIndexOf('.') + 1)..] : ns,
                        FullName: ns,
                        Kind: NodeKind.Namespace,
                        ProjectName: project.Name,
                        Namespace: ns));
                }

                var primaryLocation = GetPrimaryLocation(type, rootDir);
                var (lineCount, totalComplexity, maxMethodComplexity) = ComputeSyntaxStats(type);
                var maxCtorParams = type.InstanceConstructors
                    .Where(c => !c.IsImplicitlyDeclared)
                    .Select(c => c.Parameters.Length)
                    .DefaultIfEmpty(0)
                    .Max();

                var memberCount = type.GetMembers()
                    .Count(m => !m.IsImplicitlyDeclared && m.Kind is SymbolKind.Method or SymbolKind.Property or SymbolKind.Field or SymbolKind.Event);

                var typeMetadata = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["maxMethodComplexity"] = maxMethodComplexity.ToString()
                };

                nodes.Add(new CodeNode(
                    Id: typeId,
                    Name: type.Name,
                    FullName: typeId,
                    Kind: NodeKind.Type,
                    ProjectName: project.Name,
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
                    Metadata: typeMetadata));

                ExtractTypeDependencies(type, typeId, knownTypeIds, rootDir, GetSemanticModel, edgeAggregator);
            }
        }

        var edges = edgeAggregator
            .Select(kvp => new DependencyEdge(kvp.Key.From, kvp.Key.To, kvp.Key.Kind, kvp.Value.Location, kvp.Value.Weight))
            .ToList();

        var graph = new CodeGraph(rootDir, nodes, edges, projectInfos, diagnostics);
        return new ArchitectureSnapshot(
            SnapshotId: $"snap-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}",
            TargetPath: resolvedTarget,
            CapturedAt: DateTimeOffset.UtcNow,
            Graph: graph);
    }

    private static string ResolveTargetFile(string targetPath)
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
        if (slnx is not null)
        {
            return slnx;
        }

        var sln = Directory.GetFiles(fullPath, "*.sln", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (sln is not null)
        {
            return sln;
        }

        var csproj = Directory.GetFiles(fullPath, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (csproj is not null)
        {
            return csproj;
        }

        throw new FileNotFoundException($"No .slnx, .sln, or .csproj file found in directory '{fullPath}'.", fullPath);
    }

    private static async Task<IReadOnlyList<Project>> LoadProjectsAsync(
        MSBuildWorkspace workspace,
        string resolvedTarget,
        List<ExtractionDiagnostic> diagnostics,
        CancellationToken ct)
    {
        var ext = Path.GetExtension(resolvedTarget);

        if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            var proj = await workspace.OpenProjectAsync(resolvedTarget, cancellationToken: ct);
            return [proj];
        }

        if (ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var solution = await workspace.OpenSolutionAsync(resolvedTarget, cancellationToken: ct);
                if (solution.Projects.Any())
                {
                    return solution.Projects.ToList();
                }
            }
            catch (Exception ex)
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionDiagnosticSeverity.Info,
                    $"Direct .slnx open fell back to XML project discovery ({ex.Message}).",
                    resolvedTarget));
            }

            return await LoadProjectsFromSlnxFallbackAsync(workspace, resolvedTarget, diagnostics, ct);
        }

        var sln = await workspace.OpenSolutionAsync(resolvedTarget, cancellationToken: ct);
        return sln.Projects.ToList();
    }

    private static async Task<IReadOnlyList<Project>> LoadProjectsFromSlnxFallbackAsync(
        MSBuildWorkspace workspace,
        string slnxPath,
        List<ExtractionDiagnostic> diagnostics,
        CancellationToken ct)
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

        var loaded = new List<Project>();
        foreach (var path in projectPaths)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path))
            {
                diagnostics.Add(new ExtractionDiagnostic(
                    ExtractionDiagnosticSeverity.Warning,
                    $"Project listed in .slnx not found: {path}",
                    slnxPath));
                continue;
            }

            var proj = await workspace.OpenProjectAsync(path, cancellationToken: ct);
            loaded.Add(proj);
        }

        return loaded;
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

    private static void ExtractTypeDependencies(
        INamedTypeSymbol sourceType,
        string sourceId,
        HashSet<string> knownTypeIds,
        string rootDir,
        Func<SyntaxTree, SemanticModel> getSemanticModel,
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> aggregator)
    {
        var primaryLoc = GetPrimaryLocation(sourceType, rootDir);

        // 1. Inheritance & Interfaces
        if (sourceType.BaseType is not null)
        {
            RecordReferencedType(sourceId, sourceType.BaseType, DependencyKind.Inherits, primaryLoc, knownTypeIds, aggregator);
        }

        foreach (var iface in sourceType.Interfaces)
        {
            RecordReferencedType(sourceId, iface, DependencyKind.Implements, primaryLoc, knownTypeIds, aggregator);
        }

        // 2. Attributes on the type
        foreach (var attr in sourceType.GetAttributes())
        {
            if (attr.AttributeClass is not null)
            {
                RecordReferencedType(sourceId, attr.AttributeClass, DependencyKind.Attribute, primaryLoc, knownTypeIds, aggregator);
            }
        }

        // 3. Members (fields, properties, methods, constructors)
        foreach (var member in sourceType.GetMembers())
        {
            if (member.IsImplicitlyDeclared)
            {
                continue;
            }

            var memberLoc = GetPrimaryLocation(member, rootDir) ?? primaryLoc;

            switch (member)
            {
                case IFieldSymbol field:
                    RecordReferencedType(sourceId, field.Type, DependencyKind.FieldOrProperty, memberLoc, knownTypeIds, aggregator);
                    break;
                case IPropertySymbol prop:
                    RecordReferencedType(sourceId, prop.Type, DependencyKind.FieldOrProperty, memberLoc, knownTypeIds, aggregator);
                    break;
                case IEventSymbol ev:
                    RecordReferencedType(sourceId, ev.Type, DependencyKind.FieldOrProperty, memberLoc, knownTypeIds, aggregator);
                    break;
                case IMethodSymbol method:
                    if (!method.ReturnsVoid)
                    {
                        RecordReferencedType(sourceId, method.ReturnType, DependencyKind.ReturnType, memberLoc, knownTypeIds, aggregator);
                    }
                    foreach (var param in method.Parameters)
                    {
                        RecordReferencedType(sourceId, param.Type, DependencyKind.Parameter, memberLoc, knownTypeIds, aggregator);
                    }
                    break;
            }
        }

        // 4. Semantic body inspection (calls, object creations, type references inside method/property bodies)
        foreach (var syntaxRef in sourceType.DeclaringSyntaxReferences)
        {
            var declSyntax = syntaxRef.GetSyntax();
            var semanticModel = getSemanticModel(syntaxRef.SyntaxTree);

            foreach (var node in declSyntax.DescendantNodes())
            {
                switch (node)
                {
                    case ObjectCreationExpressionSyntax creation:
                        var createdType = semanticModel.GetTypeInfo(creation).Type;
                        if (createdType is not null)
                        {
                            RecordReferencedType(sourceId, createdType, DependencyKind.ObjectCreation, ToSourceLocation(creation.GetLocation(), rootDir), knownTypeIds, aggregator);
                        }
                        break;

                    case ImplicitObjectCreationExpressionSyntax implicitCreation:
                        var implicitType = semanticModel.GetTypeInfo(implicitCreation).Type;
                        if (implicitType is not null)
                        {
                            RecordReferencedType(sourceId, implicitType, DependencyKind.ObjectCreation, ToSourceLocation(implicitCreation.GetLocation(), rootDir), knownTypeIds, aggregator);
                        }
                        break;

                    case InvocationExpressionSyntax invocation:
                        var symbolInfo = semanticModel.GetSymbolInfo(invocation);
                        var methodSymbol = symbolInfo.Symbol as IMethodSymbol
                            ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                        if (methodSymbol?.ContainingType is not null)
                        {
                            RecordReferencedType(sourceId, methodSymbol.ContainingType, DependencyKind.MethodCall, ToSourceLocation(invocation.GetLocation(), rootDir), knownTypeIds, aggregator);
                        }
                        break;

                    case IdentifierNameSyntax identifier when node.Parent is not QualifiedNameSyntax and not MemberAccessExpressionSyntax:
                        var idSymbol = semanticModel.GetSymbolInfo(identifier).Symbol;
                        if (idSymbol is INamedTypeSymbol referencedType)
                        {
                            RecordReferencedType(sourceId, referencedType, DependencyKind.TypeReference, ToSourceLocation(identifier.GetLocation(), rootDir), knownTypeIds, aggregator);
                        }
                        break;
                }
            }
        }
    }

    private static void RecordReferencedType(
        string sourceId,
        ITypeSymbol targetType,
        DependencyKind kind,
        SourceLocation? location,
        HashSet<string> knownTypeIds,
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> aggregator)
    {
        if (targetType is IArrayTypeSymbol arrayType)
        {
            RecordReferencedType(sourceId, arrayType.ElementType, kind, location, knownTypeIds, aggregator);
            return;
        }

        if (targetType is INamedTypeSymbol named)
        {
            var original = named.OriginalDefinition;
            var targetId = GetTypeId(original);
            if (!string.Equals(sourceId, targetId, StringComparison.Ordinal) && knownTypeIds.Contains(targetId))
            {
                AddEdge(aggregator, sourceId, targetId, kind, location);
            }

            foreach (var typeArg in named.TypeArguments)
            {
                RecordReferencedType(sourceId, typeArg, DependencyKind.GenericArgument, location, knownTypeIds, aggregator);
            }
        }
    }

    private static void AddEdge(
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> aggregator,
        string fromId,
        string toId,
        DependencyKind kind,
        SourceLocation? location)
    {
        var key = (fromId, toId, kind);
        if (aggregator.TryGetValue(key, out var existing))
        {
            aggregator[key] = (existing.Weight + 1, existing.Location ?? location);
        }
        else
        {
            aggregator[key] = (1, location);
        }
    }

    private static (int LineCount, int TotalComplexity, int MaxMethodComplexity) ComputeSyntaxStats(INamedTypeSymbol type)
    {
        var totalLines = 0;
        var totalComplexity = 0;
        var maxMethodComplexity = 0;

        foreach (var syntaxRef in type.DeclaringSyntaxReferences)
        {
            var syntax = syntaxRef.GetSyntax();
            var span = syntax.GetLocation().GetLineSpan();
            totalLines += Math.Max(1, span.EndLinePosition.Line - span.StartLinePosition.Line + 1);

            var methodLikes = syntax.DescendantNodes()
                .Where(n => n is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax)
                .ToList();

            foreach (var methodNode in methodLikes)
            {
                var c = CyclomaticComplexityWalker.Calculate(methodNode);
                totalComplexity += c;
                if (c > maxMethodComplexity)
                {
                    maxMethodComplexity = c;
                }
            }
        }

        return (totalLines, totalComplexity, maxMethodComplexity);
    }

    private static string GetTypeId(INamedTypeSymbol type)
    {
        var original = type.OriginalDefinition;
        var format = new SymbolDisplayFormat(
            globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Omitted,
            typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters);
        return original.ToDisplayString(format);
    }

    private static SourceLocation? GetPrimaryLocation(ISymbol symbol, string rootDir)
    {
        var loc = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        return loc is null ? null : ToSourceLocation(loc, rootDir);
    }

    private static SourceLocation? ToSourceLocation(Location location, string rootDir)
    {
        if (!location.IsInSource)
        {
            return null;
        }

        var lineSpan = location.GetLineSpan();
        var relPath = MakeRelativePath(rootDir, lineSpan.Path);
        return new SourceLocation(
            relPath,
            lineSpan.StartLinePosition.Line + 1,
            lineSpan.StartLinePosition.Character + 1);
    }

    private static string MakeRelativePath(string rootDir, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        try
        {
            return Path.GetRelativePath(rootDir, path).Replace('\\', '/');
        }
        catch
        {
            return path.Replace('\\', '/');
        }
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

    private static bool IsTestProject(Project project, Dictionary<string, CsprojMetadata> metadataByPath)
    {
        if (project.Name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
            project.Name.EndsWith(".Test", StringComparison.OrdinalIgnoreCase) ||
            project.Name.EndsWith(".UnitTests", StringComparison.OrdinalIgnoreCase) ||
            project.Name.EndsWith(".IntegrationTests", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (project.FilePath is not null && metadataByPath.TryGetValue(project.FilePath, out var meta))
        {
            return meta.PackageReferences.Any(pkg =>
                pkg.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) ||
                pkg.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase) ||
                pkg.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase) ||
                pkg.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static CsprojMetadata ReadCsprojMetadata(string csprojPath)
    {
        try
        {
            var doc = XDocument.Load(csprojPath);
            var tfm = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "TargetFramework")?.Value
                ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "TargetFrameworks")?.Value;

            var packages = doc.Descendants()
                .Where(e => e.Name.LocalName == "PackageReference")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var projRefs = doc.Descendants()
                .Where(e => e.Name.LocalName == "ProjectReference")
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Cast<string>()
                .ToList();

            return new CsprojMetadata(tfm, packages, projRefs);
        }
        catch
        {
            return new CsprojMetadata(null, [], []);
        }
    }

    private sealed record CsprojMetadata(
        string? TargetFramework,
        IReadOnlyList<string> PackageReferences,
        IReadOnlyList<string> ProjectReferences);
}
