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

    public static async Task<List<AnalyzerDiagnostic>> RunAsync(
        Project project,
        Compilation compilation,
        string projectName,
        string rootDir,
        IReadOnlyList<string>? requestedCategories,
        List<ExtractionDiagnostic> extractionDiagnostics,
        CancellationToken cancellationToken)
    {
        var categoryFilter = BuildCategoryFilter(requestedCategories);
        var analyzers = CollectMatchingAnalyzers(project, categoryFilter);

        if (analyzers.IsEmpty)
        {
            return [];
        }

        try
        {
            var compilationWithAnalyzers = compilation.WithAnalyzers(analyzers, project.AnalyzerOptions);
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

                results.Add(new AnalyzerDiagnostic(
                    Id: diag.Id,
                    Title: diag.Descriptor.Title.ToString(CultureInfo.InvariantCulture),
                    Category: category,
                    Severity: MapSeverity(diag.Severity),
                    Message: diag.GetMessage(CultureInfo.InvariantCulture),
                    Location: SymbolDependencyExtractor.ToSourceLocation(diag.Location, rootDir),
                    ProjectName: projectName));
            }

            return results;
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
            return [];
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

    private static ImmutableArray<DiagnosticAnalyzer> CollectMatchingAnalyzers(
        Project project,
        HashSet<string>? categoryFilter)
    {
        var builder = ImmutableArray.CreateBuilder<DiagnosticAnalyzer>();
        var seenTypes = new HashSet<Type>();

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

                if (categoryFilter is null ||
                    analyzer.SupportedDiagnostics.Any(d => categoryFilter.Contains(d.Category ?? string.Empty)))
                {
                    builder.Add(analyzer);
                }
            }
        }

        return builder.ToImmutable();
    }

    private static RuleSeverity MapSeverity(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => RuleSeverity.Error,
        DiagnosticSeverity.Warning => RuleSeverity.Warning,
        _ => RuleSeverity.Info
    };
}
