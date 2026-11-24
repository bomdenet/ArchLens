using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace ArchLens.Extraction.Roslyn;

internal sealed record CsprojMetadata(
    string? TargetFramework,
    IReadOnlyList<string> TargetFrameworks,
    IReadOnlyList<string> PackageReferences,
    IReadOnlyList<string> ProjectReferences);

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

            return new CsprojMetadata(tfms.FirstOrDefault(), tfms, packages, projRefs);
        }
        catch
        {
            return new CsprojMetadata(null, [], [], []);
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
        if (normalizedProjectName.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
            normalizedProjectName.EndsWith(".Test", StringComparison.OrdinalIgnoreCase) ||
            normalizedProjectName.EndsWith(".UnitTests", StringComparison.OrdinalIgnoreCase) ||
            normalizedProjectName.EndsWith(".IntegrationTests", StringComparison.OrdinalIgnoreCase))
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
}
