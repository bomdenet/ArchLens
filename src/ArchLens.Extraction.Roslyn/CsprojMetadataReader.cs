using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace ArchLens.Extraction.Roslyn;

internal sealed record CsprojMetadata(
    string? TargetFramework,
    IReadOnlyList<string> TargetFrameworks,
    IReadOnlyList<string> PackageReferences,
    IReadOnlyList<string> ProjectReferences,
    string? RootNamespace = null);

internal static class CsprojMetadataReader
{
    public static CsprojMetadata Read(string csprojPath)
    {
        try
        {
            var doc = XDocument.Load(csprojPath);
            var tfms = new List<string>();
            CollectTargetFrameworksFromDocument(doc, tfms);

            // Inherit TargetFramework(s) from nearest ancestor Directory.Build.props if not declared locally
            if (tfms.Count == 0)
            {
                foreach (var propsPath in FindAncestorDirectoryBuildProps(csprojPath))
                {
                    try
                    {
                        var propsDoc = XDocument.Load(propsPath);
                        CollectTargetFrameworksFromDocument(propsDoc, tfms);
                        if (tfms.Count > 0)
                        {
                            break;
                        }
                    }
                    catch
                    {
                        // Ignore unreadable props file
                    }
                }
            }

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

            var rootNs = doc.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "RootNamespace")?.Value?.Trim();
            if (string.IsNullOrWhiteSpace(rootNs) || rootNs.Contains("$(", StringComparison.Ordinal))
            {
                rootNs = null;
            }

            return new CsprojMetadata(tfms.FirstOrDefault(), tfms, packages, projRefs, rootNs);
        }
        catch
        {
            return new CsprojMetadata(null, [], [], [], null);
        }
    }

    public static IReadOnlyList<string> FindAncestorDirectoryBuildProps(string startPath)
    {
        var results = new List<string>();
        var dir = Directory.Exists(startPath)
            ? new DirectoryInfo(startPath)
            : new FileInfo(startPath).Directory;

        while (dir is not null)
        {
            var props = Path.Combine(dir.FullName, "Directory.Build.props");
            if (File.Exists(props))
            {
                results.Add(props);
            }
            dir = dir.Parent;
        }

        return results;
    }

    private static void CollectTargetFrameworksFromDocument(XDocument doc, List<string> tfms)
    {
        foreach (var el in doc.Descendants())
        {
            if (el.Name.LocalName is not ("TargetFramework" or "TargetFrameworks"))
            {
                continue;
            }

            var raw = el.Value;
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Contains("$(", StringComparison.Ordinal))
                {
                    continue;
                }
                if (!tfms.Contains(part, StringComparer.OrdinalIgnoreCase))
                {
                    tfms.Add(part);
                }
            }
        }
    }

    public static string? DetectProjectTargetFramework(Project project, CsprojMetadata? meta)
    {
        var candidates = meta?.TargetFrameworks ?? [];

        // 1. Check Roslyn multi-targeted name suffix: "ProjectName (net8.0)"
        var openParen = project.Name.LastIndexOf('(');
        var closeParen = project.Name.LastIndexOf(')');
        if (openParen > 0 && closeParen > openParen)
        {
            var inside = project.Name[(openParen + 1)..closeParen].Trim();
            if (candidates.Count == 0 || candidates.Contains(inside, StringComparer.OrdinalIgnoreCase))
            {
                return inside;
            }
        }

        // 2. Check OutputFilePath path segments
        if (!string.IsNullOrWhiteSpace(project.OutputFilePath) && candidates.Count > 0)
        {
            var segments = project.OutputFilePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var tfm in candidates)
            {
                if (segments.Any(s => s.Equals(tfm, StringComparison.OrdinalIgnoreCase)))
                {
                    return tfm;
                }
            }
        }

        // 3. Check preprocessor symbols (e.g. NET10_0 -> net10.0)
        if (project.ParseOptions?.PreprocessorSymbolNames is { } symbols && candidates.Count > 0)
        {
            var symbolSet = symbols.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var tfm in candidates)
            {
                var expectedSymbol = tfm.Replace('.', '_').Replace('-', '_').ToUpperInvariant();
                if (symbolSet.Contains(expectedSymbol))
                {
                    return tfm;
                }
            }
        }

        return meta?.TargetFramework;
    }

    public static string NormalizeProjectName(string rawName, string? detectedTfm)
    {
        if (!string.IsNullOrWhiteSpace(detectedTfm))
        {
            var suffix = $" ({detectedTfm})";
            if (rawName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return rawName[..^suffix.Length];
            }
        }

        var idx = rawName.LastIndexOf(" (", StringComparison.Ordinal);
        if (idx > 0 && rawName.EndsWith(')'))
        {
            return rawName[..idx];
        }

        return rawName;
    }

    public static bool IsTestProject(string normalizedProjectName, string? projectFilePath, Dictionary<string, CsprojMetadata> metadataByPath)
    {
        if (IsTestProjectName(normalizedProjectName))
        {
            return true;
        }

        if (projectFilePath is not null && metadataByPath.TryGetValue(projectFilePath, out var meta))
        {
            return meta.PackageReferences.Any(pkg =>
                pkg.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) ||
                pkg.StartsWith("NUnit", StringComparison.OrdinalIgnoreCase) ||
                pkg.StartsWith("MSTest", StringComparison.OrdinalIgnoreCase) ||
                pkg.Equals("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    public static bool IsTestProjectName(string normalizedProjectName) =>
        normalizedProjectName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
        normalizedProjectName.EndsWith(".Test", StringComparison.OrdinalIgnoreCase) ||
        normalizedProjectName.EndsWith(".UnitTests", StringComparison.OrdinalIgnoreCase) ||
        normalizedProjectName.EndsWith(".IntegrationTests", StringComparison.OrdinalIgnoreCase) ||
        normalizedProjectName.EndsWith(".FunctionalTests", StringComparison.OrdinalIgnoreCase) ||
        normalizedProjectName.EndsWith(".EndToEndTests", StringComparison.OrdinalIgnoreCase) ||
        normalizedProjectName.EndsWith(".Specs", StringComparison.OrdinalIgnoreCase);

    public static bool IsTestSupportProjectName(string normalizedProjectName) =>
        normalizedProjectName.Contains(".Tests.", StringComparison.OrdinalIgnoreCase) ||
        normalizedProjectName.Contains(".Test.", StringComparison.OrdinalIgnoreCase) ||
        normalizedProjectName.EndsWith(".TestSupport", StringComparison.OrdinalIgnoreCase) ||
        normalizedProjectName.EndsWith(".Testing", StringComparison.OrdinalIgnoreCase) ||
        normalizedProjectName.EndsWith(".TestHelpers", StringComparison.OrdinalIgnoreCase) ||
        normalizedProjectName.EndsWith(".TestUtilities", StringComparison.OrdinalIgnoreCase) ||
        normalizedProjectName.EndsWith(".TestFixtures", StringComparison.OrdinalIgnoreCase) ||
        normalizedProjectName.EndsWith(".TestUtils", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Identifies test-support projects (e.g. Foo.Tests.Support, or helper libraries under a tests/ directory
    /// referenced exclusively by test projects). Production libraries under src/ are never misclassified.
    /// </summary>
    public static HashSet<string> DetectTestSupportProjects(
        IReadOnlyList<(string NormalizedName, string? FilePath, IReadOnlyList<string> ProjectReferences)> allProjects,
        HashSet<string> directTestProjects)
    {
        var testSupport = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var proj in allProjects)
        {
            if (!directTestProjects.Contains(proj.NormalizedName) && IsTestSupportProjectName(proj.NormalizedName))
            {
                testSupport.Add(proj.NormalizedName);
            }
        }

        // Fixed-point check: a helper library located under a tests/ or test/ folder (or referencing a test framework/helper)
        // that is referenced by >= 1 test/test-support project and 0 production projects is a test-support project.
        var changed = true;
        while (changed)
        {
            changed = false;
            var testOrSupport = new HashSet<string>(directTestProjects, StringComparer.OrdinalIgnoreCase);
            testOrSupport.UnionWith(testSupport);

            var referencedByTest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var referencedByProd = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var proj in allProjects)
            {
                var isTestSide = testOrSupport.Contains(proj.NormalizedName);
                foreach (var target in proj.ProjectReferences)
                {
                    if (isTestSide) referencedByTest.Add(target);
                    else referencedByProd.Add(target);
                }
            }

            foreach (var proj in allProjects)
            {
                if (testOrSupport.Contains(proj.NormalizedName)) continue;
                if (referencedByTest.Contains(proj.NormalizedName) &&
                    !referencedByProd.Contains(proj.NormalizedName) &&
                    IsInTestsDirectory(proj.FilePath))
                {
                    if (testSupport.Add(proj.NormalizedName))
                    {
                        changed = true;
                    }
                }
            }
        }

        return testSupport;
    }

    private static bool IsInTestsDirectory(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        var normalized = filePath.Replace('\\', '/');
        return normalized.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("/test/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("test/", StringComparison.OrdinalIgnoreCase);
    }
}
