using System.Text.RegularExpressions;
using ArchLens.Model;

namespace ArchLens.Extraction.Roslyn;

internal sealed record RazorComponentInfo(
    string TypeId,
    string SimpleName,
    string Namespace,
    string ProjectName,
    string OutputKind,
    string RelativePath,
    int LineCount,
    bool IsEntryPoint,
    IReadOnlyList<string> UsingNamespaces,
    string Content);

internal sealed record RazorProjectScanResult(
    List<RazorComponentInfo> Components,
    int TotalRazorFiles);

/// <summary>
/// Scans Blazor / Razor component files (*.razor) in a project directory to discover:
/// - Component type nodes (including markup-only components without .razor.cs code-behind)
/// - Entry points (@page routes, App/Routes/MainLayout, LayoutComponentBase)
/// - Type references (&lt;Component /&gt;, @inject, @inherits, @implements, @typeof, nameof, and C# type usage in markup/@code).
/// </summary>
internal static partial class RazorMetadataScanner
{
    [GeneratedRegex(@"^\s*@namespace\s+([A-Za-z_][A-Za-z0-9_\.]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex NamespaceDirectiveRegex();

    [GeneratedRegex(@"^\s*@using\s+(?:static\s+)?(?:[A-Za-z_][A-Za-z0-9_]*\s*=\s*)?([A-Za-z_][A-Za-z0-9_\.]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex UsingDirectiveRegex();

    [GeneratedRegex(@"^\s*@page\b", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex PageDirectiveRegex();

    [GeneratedRegex(@"^\s*@inherits\s+([A-Za-z_][A-Za-z0-9_\.<>,\s\?]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex InheritsDirectiveRegex();

    [GeneratedRegex(@"^\s*@implements\s+([A-Za-z_][A-Za-z0-9_\.<>,\s\?]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ImplementsDirectiveRegex();

    [GeneratedRegex(@"^\s*@inject\s+([A-Za-z_][A-Za-z0-9_\.<>,\s\?]*)\s+[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex InjectDirectiveRegex();

    [GeneratedRegex(@"<\s*([A-Z][A-Za-z0-9_]*)(?:\s|/|>)", RegexOptions.CultureInvariant)]
    private static partial Regex ComponentTagRegex();

    [GeneratedRegex(@"\b(?:typeof|nameof)\s*\(\s*([A-Za-z_][A-Za-z0-9_\.]*)", RegexOptions.CultureInvariant)]
    private static partial Regex TypeofNameofRegex();

    [GeneratedRegex(@"\bnew\s+([A-Z][A-Za-z0-9_\.]*)\s*(?:<|\(|\{)", RegexOptions.CultureInvariant)]
    private static partial Regex ObjectCreationRegex();

    [GeneratedRegex(@"\b([A-Z][A-Za-z0-9_]*)\b", RegexOptions.CultureInvariant)]
    private static partial Regex PascalIdentifierRegex();

    [GeneratedRegex(@"@\*\s*[\s\S]*?\*@", RegexOptions.CultureInvariant)]
    private static partial Regex RazorCommentRegex();

    [GeneratedRegex(@"<!--[\s\S]*?-->", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlCommentRegex();

    [GeneratedRegex(@"//[^\r\n]*", RegexOptions.CultureInvariant)]
    private static partial Regex SingleLineCommentRegex();

    [GeneratedRegex(@"/\*[\s\S]*?\*/", RegexOptions.CultureInvariant)]
    private static partial Regex MultiLineCommentRegex();

    private static readonly HashSet<string> EntryPointFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "App", "Routes", "MainLayout", "NavMenu", "HeadOutlet", "ReconnectModal"
    };

    private static readonly HashSet<string> CommonSystemOrHtmlIdentifiers = new(StringComparer.Ordinal)
    {
        "String", "Object", "Int32", "Int64", "Boolean", "Decimal", "Double", "Single", "Byte", "Char",
        "DateTime", "DateTimeOffset", "TimeSpan", "Guid", "Uri", "Type", "Exception", "Task", "ValueTask",
        "Action", "Func", "List", "Dictionary", "HashSet", "IEnumerable", "IReadOnlyList", "IReadOnlyDictionary",
        "CancellationToken", "EventCallback", "RenderFragment", "MarkupString", "ElementReference",
        "Parameter", "CascadingParameter", "Inject", "Layout", "NavigationManager", "IJSRuntime",
        "ComponentBase", "LayoutComponentBase", "OwningComponentBase", "ErrorBoundary", "CascadingValue",
        "AuthorizeView", "AuthorizeRouteView", "RouteView", "Router", "FocusOnNavigate", "PageTitle",
        "SectionOutlet", "SectionContent", "EditForm", "InputText", "InputNumber", "InputSelect",
        "InputCheckbox", "DataAnnotationsValidator", "ValidationSummary", "ValidationMessage",
        "NavLink", "Virtualize", "DynamicComponent", "Found", "NotFound", "ChildContent", "Body",
        "True", "False", "Null", "Console", "Math", "Enum", "Array", "Enumerable", "Path", "File",
        "DOCTYPE", "HTML", "HEAD", "BODY", "SVG", "DIV", "SPAN", "UTF"
    };

    public static RazorProjectScanResult ScanProjectDirectory(
        string? projectFilePath,
        string projectName,
        string? rootNamespace,
        string outputKind,
        string rootDir)
    {
        var components = new List<RazorComponentInfo>();
        if (string.IsNullOrWhiteSpace(projectFilePath))
        {
            return new RazorProjectScanResult(components, 0);
        }

        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
        {
            return new RazorProjectScanResult(components, 0);
        }

        var razorFiles = EnumerateRazorFiles(projectDir);
        if (razorFiles.Count == 0)
        {
            return new RazorProjectScanResult(components, 0);
        }

        var effectiveRootNs = string.IsNullOrWhiteSpace(rootNamespace) ? projectName : rootNamespace.Trim();

        // Collect _Imports.razor files per directory
        var importsByDir = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in razorFiles)
        {
            if (Path.GetFileName(file).Equals("_Imports.razor", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var text = File.ReadAllText(file);
                    var dir = Path.GetDirectoryName(file) ?? projectDir;
                    importsByDir[dir] = ExtractUsings(text);
                }
                catch
                {
                    // Ignore unreadable _Imports.razor
                }
            }
        }

        foreach (var file in razorFiles)
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("_Imports.razor", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string content;
            try
            {
                content = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            var simpleName = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(simpleName) || simpleName.StartsWith('_'))
            {
                continue;
            }

            var ns = ResolveComponentNamespace(content, file, projectDir, effectiveRootNs);
            var typeId = string.IsNullOrWhiteSpace(ns) ? simpleName : $"{ns}.{simpleName}";
            var relPath = SymbolDependencyExtractor.MakeRelativePath(rootDir, file);
            var lineCount = CountLines(content);

            var usings = CollectEffectiveUsings(file, projectDir, importsByDir, content);
            var isEntryPoint = PageDirectiveRegex().IsMatch(content) ||
                               EntryPointFileNames.Contains(simpleName) ||
                               content.Contains("LayoutComponentBase", StringComparison.Ordinal) ||
                               content.Contains("[Route(", StringComparison.Ordinal);

            components.Add(new RazorComponentInfo(
                TypeId: typeId,
                SimpleName: simpleName,
                Namespace: ns,
                ProjectName: projectName,
                OutputKind: outputKind,
                RelativePath: relPath,
                LineCount: lineCount,
                IsEntryPoint: isEntryPoint,
                UsingNamespaces: usings,
                Content: content));
        }

        return new RazorProjectScanResult(components, razorFiles.Count);
    }

    public static void ResolveAndRecordReferences(
        IReadOnlyList<RazorComponentInfo> components,
        HashSet<string> knownTypeIds,
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> edgeAggregator)
    {
        if (components.Count == 0 || knownTypeIds.Count == 0)
        {
            return;
        }

        // Build lookup indexes over all known solution types (Roslyn types + Razor components)
        var typesBySimpleName = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var id in knownTypeIds)
        {
            var simple = ExtractSimpleName(id);
            if (string.IsNullOrEmpty(simple))
            {
                continue;
            }

            if (!typesBySimpleName.TryGetValue(simple, out var list))
            {
                list = [];
                typesBySimpleName[simple] = list;
            }
            list.Add(id);
        }

        foreach (var comp in components)
        {
            if (!knownTypeIds.Contains(comp.TypeId))
            {
                continue;
            }

            var stripped = StripComments(comp.Content);
            var loc = new SourceLocation(comp.RelativePath, 1, 1);
            var seenTargets = new HashSet<(string Target, DependencyKind Kind)>();

            void TryRecordCandidate(string rawName, DependencyKind kind)
            {
                var resolved = ResolveTypeName(rawName, comp.Namespace, comp.UsingNamespaces, knownTypeIds, typesBySimpleName);
                if (resolved is null || string.Equals(resolved, comp.TypeId, StringComparison.Ordinal))
                {
                    return;
                }

                if (seenTargets.Add((resolved, kind)))
                {
                    SymbolDependencyExtractor.AddEdge(edgeAggregator, comp.TypeId, resolved, kind, loc);
                }
            }

            // 1. @inherits
            foreach (Match m in InheritsDirectiveRegex().Matches(stripped))
            {
                foreach (var typeTok in SplitTypeTokens(m.Groups[1].Value))
                {
                    TryRecordCandidate(typeTok, DependencyKind.Inherits);
                }
            }

            // 2. @implements
            foreach (Match m in ImplementsDirectiveRegex().Matches(stripped))
            {
                foreach (var typeTok in SplitTypeTokens(m.Groups[1].Value))
                {
                    TryRecordCandidate(typeTok, DependencyKind.Implements);
                }
            }

            // 3. @inject
            foreach (Match m in InjectDirectiveRegex().Matches(stripped))
            {
                foreach (var typeTok in SplitTypeTokens(m.Groups[1].Value))
                {
                    TryRecordCandidate(typeTok, DependencyKind.FieldOrProperty);
                }
            }

            // 4. Child Razor component tags <MyComponent ...>
            foreach (Match m in ComponentTagRegex().Matches(stripped))
            {
                TryRecordCandidate(m.Groups[1].Value, DependencyKind.TypeReference);
            }

            // 5. typeof(...) / nameof(...)
            foreach (Match m in TypeofNameofRegex().Matches(stripped))
            {
                TryRecordCandidate(m.Groups[1].Value, DependencyKind.TypeReference);
            }

            // 6. new TypeName(...)
            foreach (Match m in ObjectCreationRegex().Matches(stripped))
            {
                TryRecordCandidate(m.Groups[1].Value, DependencyKind.ObjectCreation);
            }

            // 7. General PascalCase identifiers referencing known solution types in markup expressions & @code blocks
            // Strip directive header lines (@using/@namespace/@page) so namespace segments don't accidentally match type names
            var bodyOnly = RemoveHeaderDirectives(stripped);
            var seenIdentifiers = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in PascalIdentifierRegex().Matches(bodyOnly))
            {
                var ident = m.Groups[1].Value;
                if (ident.Length < 2 || CommonSystemOrHtmlIdentifiers.Contains(ident) || !seenIdentifiers.Add(ident))
                {
                    continue;
                }

                if (!typesBySimpleName.TryGetValue(ident, out var candidates))
                {
                    continue;
                }

                // Prefer exact match in component's namespace or imported @using namespaces;
                // fall back to unique solution-wide match for unambiguous type names (e.g. BoardCellText, HistoryFormatting)
                var resolved = ResolveFromCandidates(ident, comp.Namespace, comp.UsingNamespaces, candidates, allowUniqueFallback: true);
                if (resolved is not null && !string.Equals(resolved, comp.TypeId, StringComparison.Ordinal))
                {
                    if (seenTargets.Add((resolved, DependencyKind.TypeReference)))
                    {
                        SymbolDependencyExtractor.AddEdge(edgeAggregator, comp.TypeId, resolved, DependencyKind.TypeReference, loc);
                    }
                }
            }
        }
    }

    private static string? ResolveTypeName(
        string rawName,
        string componentNamespace,
        IReadOnlyList<string> usings,
        HashSet<string> knownTypeIds,
        Dictionary<string, List<string>> typesBySimpleName)
    {
        var cleaned = rawName.Trim().TrimEnd('?');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return null;
        }

        if (knownTypeIds.Contains(cleaned))
        {
            return cleaned;
        }

        var simple = ExtractSimpleName(cleaned);
        if (!typesBySimpleName.TryGetValue(simple, out var candidates))
        {
            return null;
        }

        // If rawName was qualified (e.g. Sub.MyType), prefer suffix match
        if (cleaned.Contains('.', StringComparison.Ordinal))
        {
            var suffix = "." + cleaned;
            var suffixMatch = candidates.FirstOrDefault(c => c.EndsWith(suffix, StringComparison.Ordinal) || c.Equals(cleaned, StringComparison.Ordinal));
            if (suffixMatch is not null)
            {
                return suffixMatch;
            }
        }

        return ResolveFromCandidates(simple, componentNamespace, usings, candidates, allowUniqueFallback: true);
    }

    private static string? ResolveFromCandidates(
        string simpleName,
        string componentNamespace,
        IReadOnlyList<string> usings,
        List<string> candidates,
        bool allowUniqueFallback)
    {
        // 1. Same namespace (or ancestor namespace)
        if (!string.IsNullOrWhiteSpace(componentNamespace))
        {
            var exactSameNs = $"{componentNamespace}.{simpleName}";
            if (candidates.Contains(exactSameNs, StringComparer.Ordinal))
            {
                return exactSameNs;
            }

            // Generic type in same namespace (e.g. MyNs.MyType<T>)
            var genericSameNs = candidates.FirstOrDefault(c =>
                c.StartsWith(exactSameNs + "<", StringComparison.Ordinal));
            if (genericSameNs is not null)
            {
                return genericSameNs;
            }
        }

        // 2. Imported @using namespaces (including from _Imports.razor)
        foreach (var ns in usings)
        {
            var qualified = $"{ns}.{simpleName}";
            if (candidates.Contains(qualified, StringComparer.Ordinal))
            {
                return qualified;
            }

            var genericMatch = candidates.FirstOrDefault(c => c.StartsWith(qualified + "<", StringComparison.Ordinal));
            if (genericMatch is not null)
            {
                return genericMatch;
            }
        }

        // 3. Ancestor namespace walk (C# namespace lookup rules)
        if (!string.IsNullOrWhiteSpace(componentNamespace))
        {
            var ns = componentNamespace;
            while (ns.Contains('.', StringComparison.Ordinal))
            {
                ns = ns[..ns.LastIndexOf('.')];
                var qualified = $"{ns}.{simpleName}";
                if (candidates.Contains(qualified, StringComparer.Ordinal))
                {
                    return qualified;
                }
            }
        }

        // 4. Unambiguous unique match across the solution
        if (allowUniqueFallback && candidates.Count == 1)
        {
            return candidates[0];
        }

        return null;
    }

    private static IEnumerable<string> SplitTypeTokens(string rawTypeExpr)
    {
        // Splits generic expressions like "IRepository<Order, Guid>" into ["IRepository", "Order", "Guid"]
        foreach (var part in rawTypeExpr.Split(['<', '>', ',', ' ', '\t', '\r', '\n', '[', ']', '?'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed) && char.IsLetter(trimmed[0]))
            {
                yield return trimmed;
            }
        }
    }

    private static string ResolveComponentNamespace(string content, string filePath, string projectDir, string rootNamespace)
    {
        var nsMatch = NamespaceDirectiveRegex().Match(content);
        if (nsMatch.Success)
        {
            return nsMatch.Groups[1].Value.Trim();
        }

        var relDir = Path.GetDirectoryName(Path.GetRelativePath(projectDir, filePath));
        if (string.IsNullOrWhiteSpace(relDir) || relDir == ".")
        {
            return rootNamespace;
        }

        var segments = relDir
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Select(SanitizeNamespaceSegment)
            .Where(s => !string.IsNullOrWhiteSpace(s));

        var suffix = string.Join('.', segments);
        return string.IsNullOrWhiteSpace(suffix) ? rootNamespace : $"{rootNamespace}.{suffix}";
    }

    private static string SanitizeNamespaceSegment(string segment)
    {
        var chars = segment.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        var cleaned = new string(chars);
        return cleaned.Length > 0 && char.IsDigit(cleaned[0]) ? "_" + cleaned : cleaned;
    }

    private static List<string> CollectEffectiveUsings(
        string filePath,
        string projectDir,
        Dictionary<string, List<string>> importsByDir,
        string content)
    {
        var usings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        // Walk from projectDir down to file's directory collecting _Imports.razor
        var dirs = new List<string>();
        var current = Path.GetDirectoryName(filePath);
        var normProjectDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectDir));

        while (!string.IsNullOrWhiteSpace(current))
        {
            dirs.Add(current);
            if (Path.TrimEndingDirectorySeparator(Path.GetFullPath(current)).Equals(normProjectDir, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
            current = Path.GetDirectoryName(current);
        }

        dirs.Reverse();
        foreach (var d in dirs)
        {
            if (importsByDir.TryGetValue(d, out var list))
            {
                foreach (var u in list)
                {
                    if (seen.Add(u)) usings.Add(u);
                }
            }
        }

        foreach (var u in ExtractUsings(content))
        {
            if (seen.Add(u)) usings.Add(u);
        }

        return usings;
    }

    private static List<string> ExtractUsings(string content)
    {
        var result = new List<string>();
        foreach (Match m in UsingDirectiveRegex().Matches(content))
        {
            var ns = m.Groups[1].Value.Trim();
            if (!string.IsNullOrWhiteSpace(ns))
            {
                result.Add(ns);
            }
        }
        return result;
    }

    private static string StripComments(string content)
    {
        var s = RazorCommentRegex().Replace(content, " ");
        s = HtmlCommentRegex().Replace(s, " ");
        s = MultiLineCommentRegex().Replace(s, " ");
        s = SingleLineCommentRegex().Replace(s, " ");
        return s;
    }

    private static string RemoveHeaderDirectives(string content)
    {
        var lines = content.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("@using ", StringComparison.Ordinal) ||
                trimmed.StartsWith("@namespace ", StringComparison.Ordinal) ||
                trimmed.StartsWith("@page ", StringComparison.Ordinal))
            {
                lines[i] = string.Empty;
            }
        }
        return string.Join('\n', lines);
    }

    private static string ExtractSimpleName(string typeId)
    {
        var lt = typeId.IndexOf('<');
        var withoutGeneric = lt > 0 ? typeId[..lt] : typeId;
        var dot = withoutGeneric.LastIndexOf('.');
        return dot >= 0 ? withoutGeneric[(dot + 1)..] : withoutGeneric;
    }

    private static int CountLines(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var count = 1;
        foreach (var ch in text)
        {
            if (ch == '\n') count++;
        }
        return count;
    }

    private static List<string> EnumerateRazorFiles(string projectDir)
    {
        var results = new List<string>();
        var stack = new Stack<string>();
        stack.Push(projectDir);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(file);
                    }
                }

                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("wwwroot", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    stack.Push(sub);
                }
            }
            catch
            {
                // Ignore inaccessible directories
            }
        }

        return results;
    }
}
