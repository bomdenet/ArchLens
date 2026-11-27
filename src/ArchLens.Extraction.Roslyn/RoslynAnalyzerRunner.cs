using System.Collections.Immutable;
using System.Globalization;
using ArchLens.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace ArchLens.Extraction.Roslyn;

/// <summary>
/// Runs Roslyn DiagnosticAnalyzers (such as Microsoft.CodeAnalysis.NetAnalyzers and Roslynator)
/// attached to a project and aggregates diagnostics filtered by architectural categories
/// (Design, Maintainability, Performance by default).
/// </summary>
internal static class RoslynAnalyzerRunner
{
    public static readonly IReadOnlyList<string> DefaultCategories = ["Design", "Maintainability", "Performance"];

    public static void ValidateRequestedCategories(
        IEnumerable<Project> projects,
        IReadOnlyList<string>? requestedCategories)
    {
        var categoryFilter = BuildCategoryFilter(requestedCategories);
        if (categoryFilter is null)
        {
            return;
        }

        var availableCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var loadedCount = 0;
        foreach (var project in projects)
        {
            var (_, types, cats) = CollectMatchingAnalyzers(project, categoryFilter: null);
            loadedCount += types.Count;
            availableCategories.UnionWith(cats);
        }

        if (loadedCount == 0 || availableCategories.Count == 0)
        {
            return;
        }

        var unknown = requestedCategories!
            .Where(c => !string.IsNullOrWhiteSpace(c) && !availableCategories.Contains(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unknown.Count > 0)
        {
            var label = unknown.Count == 1 ? "category" : "categories";
            var sortedAvailable = availableCategories.OrderBy(c => c, StringComparer.OrdinalIgnoreCase);
            throw new ArgumentException(
                $"Unknown analyzer {label}: {string.Join(", ", unknown.Select(c => $"'{c}'"))}. " +
                $"Available categories from loaded analyzers: {string.Join(", ", sortedAvailable)} (or 'all').");
        }
    }

    public static async Task<(List<AnalyzerDiagnostic> Diagnostics, IReadOnlyCollection<Type> LoadedAnalyzerTypes, IReadOnlyCollection<string> AvailableCategories)> RunAsync(
        Project project,
        Compilation compilation,
        string projectName,
        string rootDir,
        IReadOnlyList<string>? requestedCategories,
        List<ExtractionDiagnostic> extractionDiagnostics,
        CancellationToken cancellationToken,
        bool includeGenerated = false,
        bool elevateSeverity = false)
    {
        var categoryFilter = BuildCategoryFilter(requestedCategories);
        var (analyzers, allLoadedTypes, availableCategories) = CollectMatchingAnalyzers(project, categoryFilter);

        if (analyzers.IsEmpty)
        {
            return ([], allLoadedTypes, availableCategories);
        }

        try
        {
            var effectiveCompilation = compilation;
            var treeOptionsProvider = compilation.Options.SyntaxTreeOptionsProvider;
            if (elevateSeverity)
            {
                var configProvider = project.AnalyzerOptions.AnalyzerConfigOptionsProvider;
                var updatedOptions = new Dictionary<string, ReportDiagnostic>(compilation.Options.SpecificDiagnosticOptions, StringComparer.OrdinalIgnoreCase);
                var elevatedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var analyzer in analyzers)
                {
                    foreach (var descriptor in analyzer.SupportedDiagnostics)
                    {
                        if (descriptor.DefaultSeverity == DiagnosticSeverity.Error)
                        {
                            continue;
                        }

                        var cat = descriptor.Category ?? string.Empty;
                        if (categoryFilter is null || categoryFilter.Contains(cat))
                        {
                            if (IsConfiguredAsError(treeOptionsProvider, configProvider, compilation.SyntaxTrees, descriptor.Id, cat, cancellationToken))
                            {
                                continue;
                            }

                            if (descriptor.IsEnabledByDefault)
                            {
                                if (!updatedOptions.TryGetValue(descriptor.Id, out var existing) ||
                                    existing is ReportDiagnostic.Suppress or ReportDiagnostic.Hidden or ReportDiagnostic.Info or ReportDiagnostic.Default)
                                {
                                    updatedOptions[descriptor.Id] = ReportDiagnostic.Warn;
                                    elevatedIds.Add(descriptor.Id);
                                }
                            }
                            else
                            {
                                // Rules disabled by default (e.g. CA1823) may be explicitly configured to `suggestion`/Info
                                // in .editorconfig — elevate them when configured as non-suppressed Info/Hidden.
                                if (IsConfiguredAsInfoOrHidden(treeOptionsProvider, configProvider, compilation.SyntaxTrees, descriptor.Id, cat, cancellationToken))
                                {
                                    updatedOptions[descriptor.Id] = ReportDiagnostic.Warn;
                                    elevatedIds.Add(descriptor.Id);
                                }
                            }
                        }
                    }
                }
                var newCompOptions = compilation.Options.WithSpecificDiagnosticOptions(updatedOptions);
                if (treeOptionsProvider is not null && elevatedIds.Count > 0)
                {
                    newCompOptions = newCompOptions.WithSyntaxTreeOptionsProvider(
                        new ElevatingSyntaxTreeOptionsProvider(treeOptionsProvider, elevatedIds));
                }
                effectiveCompilation = compilation.WithOptions(newCompOptions);
            }

            var compilationWithAnalyzers = effectiveCompilation.WithAnalyzers(analyzers, project.AnalyzerOptions);
            var rawDiagnostics = await compilationWithAnalyzers.GetAnalyzerDiagnosticsAsync(cancellationToken);

            var results = new List<AnalyzerDiagnostic>();
            foreach (var diag in rawDiagnostics)
            {
                if (diag.IsSuppressed || diag.Severity == DiagnosticSeverity.Hidden)
                {
                    continue;
                }

                var category = diag.Descriptor.Category ?? string.Empty;
                if (categoryFilter is not null && !categoryFilter.Contains(category))
                {
                    continue;
                }

                if (!includeGenerated && diag.Location.IsInSource)
                {
                    var filePath = diag.Location.SourceTree?.FilePath;
                    if (GeneratedCodeClassifier.IsGeneratedSyntaxTree(diag.Location.SourceTree) ||
                        GeneratedCodeClassifier.IsMigrationFilePath(filePath))
                    {
                        continue;
                    }
                }

                var reportedSeverity = diag.Severity;
                if (elevateSeverity && reportedSeverity < DiagnosticSeverity.Error &&
                    IsDiagnosticTreeConfiguredAsError(treeOptionsProvider, diag.Location.SourceTree, diag.Id, cancellationToken))
                {
                    reportedSeverity = DiagnosticSeverity.Error;
                }

                results.Add(new AnalyzerDiagnostic(
                    Id: diag.Id,
                    Title: diag.Descriptor.Title.ToString(CultureInfo.InvariantCulture),
                    Category: category,
                    Severity: MapSeverity(reportedSeverity),
                    Message: diag.GetMessage(CultureInfo.InvariantCulture),
                    Location: SymbolDependencyExtractor.ToSourceLocation(diag.Location, rootDir),
                    ProjectName: projectName));
            }

            return (results, allLoadedTypes, availableCategories);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            extractionDiagnostics.Add(new ExtractionDiagnostic(
                ExtractionDiagnosticSeverity.Warning,
                $"Roslyn analyzer execution failed for project '{projectName}': {ex.Message}",
                "RoslynAnalyzerRunner"));
            return ([], allLoadedTypes, availableCategories);
        }
    }

    private static HashSet<string>? BuildCategoryFilter(IReadOnlyList<string>? requestedCategories)
    {
        var source = requestedCategories is { Count: > 0 } ? requestedCategories : DefaultCategories;
        if (source.Any(c => c.Equals("all", StringComparison.OrdinalIgnoreCase) || c == "*"))
        {
            return null;
        }

        return source.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static (ImmutableArray<DiagnosticAnalyzer> Matching, HashSet<Type> AllLoadedTypes, HashSet<string> AvailableCategories) CollectMatchingAnalyzers(
        Project project,
        HashSet<string>? categoryFilter)
    {
        var builder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        var seenTypes = new HashSet<Type>();
        var availableCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var analyzerRef in project.AnalyzerReferences)
        {
            ImmutableArray<DiagnosticAnalyzer> loaded;
            try
            {
                loaded = analyzerRef.GetAnalyzers(LanguageNames.CSharp);
            }
            catch
            {
                continue;
            }

            foreach (var analyzer in loaded)
            {
                if (!seenTypes.Add(analyzer.GetType()))
                {
                    continue;
                }

                var matched = categoryFilter is null;
                foreach (var descriptor in analyzer.SupportedDiagnostics)
                {
                    var cat = descriptor.Category;
                    if (!string.IsNullOrWhiteSpace(cat))
                    {
                        availableCategories.Add(cat);
                        if (!matched && categoryFilter!.Contains(cat))
                        {
                            matched = true;
                        }
                    }
                }

                if (matched)
                {
                    builder.Add(analyzer);
                }
            }
        }

        return (builder.ToImmutable(), seenTypes, availableCategories);
    }

    private static bool IsConfiguredAsError(
        SyntaxTreeOptionsProvider? treeOptionsProvider,
        AnalyzerConfigOptionsProvider? configProvider,
        IEnumerable<SyntaxTree> syntaxTrees,
        string diagnosticId,
        string category,
        CancellationToken cancellationToken)
    {
        if (treeOptionsProvider is not null)
        {
            if (treeOptionsProvider.TryGetGlobalDiagnosticValue(diagnosticId, cancellationToken, out var globalSev) &&
                globalSev == ReportDiagnostic.Error)
            {
                return true;
            }

            foreach (var tree in syntaxTrees)
            {
                if (treeOptionsProvider.TryGetDiagnosticValue(tree, diagnosticId, cancellationToken, out var treeSev) &&
                    treeSev == ReportDiagnostic.Error)
                {
                    return true;
                }
            }
        }

        return IsConfiguredAsErrorInEditorConfig(configProvider, syntaxTrees, diagnosticId, category);
    }

    private static bool IsDiagnosticTreeConfiguredAsError(
        SyntaxTreeOptionsProvider? treeOptionsProvider,
        SyntaxTree? tree,
        string diagnosticId,
        CancellationToken cancellationToken)
    {
        if (treeOptionsProvider is null)
        {
            return false;
        }

        if (tree is not null &&
            treeOptionsProvider.TryGetDiagnosticValue(tree, diagnosticId, cancellationToken, out var treeSev) &&
            treeSev == ReportDiagnostic.Error)
        {
            return true;
        }

        return treeOptionsProvider.TryGetGlobalDiagnosticValue(diagnosticId, cancellationToken, out var globalSev) &&
               globalSev == ReportDiagnostic.Error;
    }

    private static bool IsConfiguredAsErrorInEditorConfig(
        AnalyzerConfigOptionsProvider? configProvider,
        IEnumerable<SyntaxTree> syntaxTrees,
        string diagnosticId,
        string category)
    {
        if (configProvider is null)
        {
            return false;
        }

        var idKey = $"dotnet_diagnostic.{diagnosticId.ToLowerInvariant()}.severity";
        var categoryKey = !string.IsNullOrWhiteSpace(category)
            ? $"dotnet_analyzer_diagnostic.category-{category.ToLowerInvariant()}.severity"
            : null;
        const string globalKey = "dotnet_analyzer_diagnostic.severity";

        static bool HasErrorSeverity(AnalyzerConfigOptions options, string idKey, string? categoryKey)
        {
            if (options.TryGetValue(idKey, out var idSev))
            {
                return idSev.Trim().Equals("error", StringComparison.OrdinalIgnoreCase);
            }
            if (categoryKey is not null && options.TryGetValue(categoryKey, out var catSev))
            {
                return catSev.Trim().Equals("error", StringComparison.OrdinalIgnoreCase);
            }
            if (options.TryGetValue(globalKey, out var allSev))
            {
                return allSev.Trim().Equals("error", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        if (HasErrorSeverity(configProvider.GlobalOptions, idKey, categoryKey))
        {
            return true;
        }

        foreach (var tree in syntaxTrees)
        {
            if (HasErrorSeverity(configProvider.GetOptions(tree), idKey, categoryKey))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsConfiguredAsInfoOrHidden(
        SyntaxTreeOptionsProvider? treeOptionsProvider,
        AnalyzerConfigOptionsProvider configProvider,
        IEnumerable<SyntaxTree> syntaxTrees,
        string diagnosticId,
        string category,
        CancellationToken cancellationToken)
    {
        if (treeOptionsProvider is not null)
        {
            if (treeOptionsProvider.TryGetGlobalDiagnosticValue(diagnosticId, cancellationToken, out var globalSev) &&
                globalSev is ReportDiagnostic.Info or ReportDiagnostic.Hidden)
            {
                return true;
            }
            foreach (var tree in syntaxTrees)
            {
                if (treeOptionsProvider.TryGetDiagnosticValue(tree, diagnosticId, cancellationToken, out var treeSev) &&
                    treeSev is ReportDiagnostic.Info or ReportDiagnostic.Hidden)
                {
                    return true;
                }
            }
        }

        var idKey = $"dotnet_diagnostic.{diagnosticId.ToLowerInvariant()}.severity";
        var categoryKey = !string.IsNullOrWhiteSpace(category)
            ? $"dotnet_analyzer_diagnostic.category-{category.ToLowerInvariant()}.severity"
            : null;

        static bool HasInfoOrSilent(AnalyzerConfigOptions options, string idKey, string? categoryKey)
        {
            if (options.TryGetValue(idKey, out var idSev))
            {
                var v = idSev.Trim();
                return v.Equals("suggestion", StringComparison.OrdinalIgnoreCase) ||
                       v.Equals("silent", StringComparison.OrdinalIgnoreCase);
            }
            if (categoryKey is not null && options.TryGetValue(categoryKey, out var catSev))
            {
                var v = catSev.Trim();
                return v.Equals("suggestion", StringComparison.OrdinalIgnoreCase) ||
                       v.Equals("silent", StringComparison.OrdinalIgnoreCase);
            }
            return false;
        }

        if (HasInfoOrSilent(configProvider.GlobalOptions, idKey, categoryKey))
        {
            return true;
        }

        foreach (var tree in syntaxTrees)
        {
            if (HasInfoOrSilent(configProvider.GetOptions(tree), idKey, categoryKey))
            {
                return true;
            }
        }

        return false;
    }

    private static RuleSeverity MapSeverity(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => RuleSeverity.Error,
        DiagnosticSeverity.Warning => RuleSeverity.Warning,
        _ => RuleSeverity.Info
    };

    private sealed class ElevatingSyntaxTreeOptionsProvider(
        SyntaxTreeOptionsProvider inner,
        HashSet<string> elevatedIds) : SyntaxTreeOptionsProvider
    {
        public override GeneratedKind IsGenerated(SyntaxTree tree, CancellationToken cancellationToken) =>
            inner.IsGenerated(tree, cancellationToken);

        public override bool TryGetDiagnosticValue(
            SyntaxTree tree,
            string diagnosticId,
            CancellationToken cancellationToken,
            out ReportDiagnostic severity)
        {
            if (inner.TryGetDiagnosticValue(tree, diagnosticId, cancellationToken, out severity))
            {
                if (elevatedIds.Contains(diagnosticId) &&
                    severity is ReportDiagnostic.Suppress or ReportDiagnostic.Hidden or ReportDiagnostic.Info or ReportDiagnostic.Default)
                {
                    severity = ReportDiagnostic.Warn;
                }
                return true;
            }
            return false;
        }

        public override bool TryGetGlobalDiagnosticValue(
            string diagnosticId,
            CancellationToken cancellationToken,
            out ReportDiagnostic severity)
        {
            if (inner.TryGetGlobalDiagnosticValue(diagnosticId, cancellationToken, out severity))
            {
                if (elevatedIds.Contains(diagnosticId) &&
                    severity is ReportDiagnostic.Suppress or ReportDiagnostic.Hidden or ReportDiagnostic.Info or ReportDiagnostic.Default)
                {
                    severity = ReportDiagnostic.Warn;
                }
                return true;
            }
            return false;
        }
    }
}
