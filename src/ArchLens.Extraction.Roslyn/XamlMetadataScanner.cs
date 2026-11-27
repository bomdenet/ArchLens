using System.Text.RegularExpressions;
using System.Xml.Linq;
using ArchLens.Model;

namespace ArchLens.Extraction.Roslyn;

internal sealed record XamlProjectScanResult(
    HashSet<string> UiEntryPointTypeIds,
    HashSet<string> XamlClassTypeIds,
    List<(string FromTypeId, string ToTypeId, SourceLocation Location)> TypeReferences,
    Dictionary<string, HashSet<string>> EventHandlerNamesByTypeId);

/// <summary>
/// Scans WPF / Avalonia / WinUI / MAUI XAML files (*.xaml, *.axaml) in a project directory to discover:
/// - Application x:Class and StartupUri targets (entry points for dead-code reachability)
/// - Cross-type references declared in XAML via clr-namespace prefixes and {x:Type ...} markup extensions.
/// </summary>
internal static partial class XamlMetadataScanner
{
    private static readonly XNamespace Xaml2006Ns = "http://schemas.microsoft.com/winfx/2006/xaml";

    [GeneratedRegex(@"\{x:Type\s+(?:TypeName\s*=\s*)?([A-Za-z_][A-Za-z0-9_]*):([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex XTypeMarkupRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9_:])([A-Za-z_][A-Za-z0-9_]*):([A-Za-z_][A-Za-z0-9_]*)\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex AttachedPropertyRefRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SimpleIdentifierRegex();

    private static readonly HashSet<string> NonEventAttributeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Class", "Name", "Key", "Uid", "FieldModifier", "Subclass", "TypeArguments",
        "Title", "Content", "Text", "Header", "ToolTip", "Tag", "Style", "Template",
        "Background", "Foreground", "BorderBrush", "Fill", "Stroke", "Cursor", "Visibility",
        "Orientation", "HorizontalAlignment", "VerticalAlignment", "HorizontalContentAlignment",
        "VerticalContentAlignment", "WindowStartupLocation", "ResizeMode", "WindowStyle",
        "Stretch", "FontFamily", "FontWeight", "FontStyle", "TextWrapping", "TextAlignment",
        "Dock", "StartupUri", "Source", "UriSource", "DataType", "TargetType", "Property",
        "Value", "Mode", "UpdateSourceTrigger", "SelectionMode", "ScrollBarVisibility",
        "AutomationId", "HelpText", "ItemStatus", "ItemType", "AcceleratorKey", "AccessKey",
        "LabeledBy", "PlaceholderText", "Description", "Label", "GroupName", "Category",
        "Format", "StringFormat", "DisplayMemberPath", "SelectedValuePath", "ValueMemberPath",
        "Path", "ElementName", "CommandParameter", "Icon", "Glyph", "ResourceKey", "TargetName",
        "StateName", "ItemTemplate", "HeaderTemplate", "ContentTemplate", "DataContext"
    };

    private static readonly HashSet<string> KnownRoutedEventOwners = new(StringComparer.OrdinalIgnoreCase)
    {
        "ButtonBase", "UIElement", "FrameworkElement", "Keyboard", "Mouse", "Stylus", "Touch",
        "DragDrop", "FocusManager", "TextBoxBase", "Selector", "MenuItem", "TreeViewItem",
        "Hyperlink", "NavigationService", "CommandManager", "Validation", "ScrollViewer", "GridViewColumnHeader"
    };

    public static XamlProjectScanResult ScanProjectDirectory(string? projectFilePath, string rootDir)
    {
        var entryPoints = new HashSet<string>(StringComparer.Ordinal);
        var xamlClasses = new HashSet<string>(StringComparer.Ordinal);
        var typeRefs = new List<(string FromTypeId, string ToTypeId, SourceLocation Location)>();
        var eventHandlersByType = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(projectFilePath))
        {
            return new XamlProjectScanResult(entryPoints, xamlClasses, typeRefs, eventHandlersByType);
        }

        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
        {
            return new XamlProjectScanResult(entryPoints, xamlClasses, typeRefs, eventHandlersByType);
        }

        var xamlFiles = EnumerateXamlFiles(projectDir);
        if (xamlFiles.Count == 0)
        {
            return new XamlProjectScanResult(entryPoints, xamlClasses, typeRefs, eventHandlersByType);
        }

        var classByRelPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parsedDocs = new List<(string FullPath, string RelToProj, XDocument Doc, string? XClass)>();

        foreach (var file in xamlFiles)
        {
            try
            {
                var doc = XDocument.Load(file, LoadOptions.SetLineInfo);
                var root = doc.Root;
                if (root is null)
                {
                    continue;
                }

                var xClass = root.Attribute(Xaml2006Ns + "Class")?.Value?.Trim();
                var relToProj = Path.GetRelativePath(projectDir, file).Replace('\\', '/');
                parsedDocs.Add((file, relToProj, doc, xClass));

                if (!string.IsNullOrWhiteSpace(xClass))
                {
                    xamlClasses.Add(xClass);
                    classByRelPath[relToProj] = xClass;
                }
            }
            catch
            {
                // Ignore malformed or non-standard XAML files
            }
        }

        foreach (var (fullPath, relToProj, doc, xClass) in parsedDocs)
        {
            var root = doc.Root!;
            var rootLocalName = root.Name.LocalName;
            var relFromRoot = SymbolDependencyExtractor.MakeRelativePath(rootDir, fullPath);

            // <Application x:Class="..." StartupUri="MainWindow.xaml">
            if (rootLocalName.Equals("Application", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(xClass))
                {
                    entryPoints.Add(xClass);
                }

                var startupUri = root.Attribute("StartupUri")?.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(startupUri))
                {
                    var resolvedTarget = ResolveStartupUriClass(startupUri, relToProj, classByRelPath);
                    if (!string.IsNullOrWhiteSpace(resolvedTarget))
                    {
                        entryPoints.Add(resolvedTarget);
                        if (!string.IsNullOrWhiteSpace(xClass) && !string.Equals(xClass, resolvedTarget, StringComparison.Ordinal))
                        {
                            typeRefs.Add((xClass, resolvedTarget, new SourceLocation(relFromRoot, 1, 1)));
                        }
                    }
                }
            }

            // Connect merged <ResourceDictionary Source="..." /> references so referenced dictionaries with x:Class stay reachable
            foreach (var el in doc.Descendants())
            {
                if (!el.Name.LocalName.Equals("ResourceDictionary", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var srcUri = el.Attribute("Source")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(srcUri))
                {
                    continue;
                }

                var targetDictClass = ResolveStartupUriClass(srcUri, relToProj, classByRelPath);
                if (!string.IsNullOrWhiteSpace(targetDictClass))
                {
                    var line = el is System.Xml.IXmlLineInfo li && li.HasLineInfo() ? li.LineNumber : 1;
                    if (rootLocalName.Equals("Application", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(xClass))
                    {
                        entryPoints.Add(targetDictClass);
                    }
                    if (!string.IsNullOrWhiteSpace(xClass) && !string.Equals(xClass, targetDictClass, StringComparison.Ordinal))
                    {
                        typeRefs.Add((xClass, targetDictClass, new SourceLocation(relFromRoot, line, 1)));
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(xClass))
            {
                // Collect candidate event-handler method names wired from XAML attributes (e.g. Click="OnSaveClick",
                // MouseLeftButtonDown="OnHeaderMouseDown", EventSetter Handler="OnRowClick").
                if (!eventHandlersByType.TryGetValue(xClass, out var handlerNames))
                {
                    handlerNames = new HashSet<string>(StringComparer.Ordinal);
                    eventHandlersByType[xClass] = handlerNames;
                }
                foreach (var el in doc.Descendants())
                {
                    foreach (var attr in el.Attributes())
                    {
                        if (attr.IsNamespaceDeclaration)
                        {
                            continue;
                        }

                        var attrLocal = attr.Name.LocalName;
                        var dotPos = attrLocal.LastIndexOf('.');
                        var leafAttr = dotPos >= 0 ? attrLocal[(dotPos + 1)..] : attrLocal;
                        if (NonEventAttributeNames.Contains(leafAttr))
                        {
                            continue;
                        }
                        if (dotPos >= 0)
                        {
                            var ownerType = attrLocal[..dotPos];
                            if (!KnownRoutedEventOwners.Contains(ownerType))
                            {
                                continue;
                            }
                        }

                        var val = attr.Value.Trim();
                        if (val.Length >= 2 && SimpleIdentifierRegex().IsMatch(val))
                        {
                            handlerNames.Add(val);
                        }
                    }
                }
            }

            // Map xmlns:prefix="clr-namespace:Foo.Bar..." or "using:Foo.Bar"
            var prefixToClrNamespace = new Dictionary<string, string>(StringComparer.Ordinal);
            var xmlNsToClrNs = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var el in doc.Descendants())
            {
                foreach (var attr in el.Attributes())
                {
                    if (!attr.IsNamespaceDeclaration)
                    {
                        continue;
                    }

                    var clrNs = TryParseClrNamespace(attr.Value);
                    if (!string.IsNullOrWhiteSpace(clrNs))
                    {
                        var prefix = attr.Name.NamespaceName.Length > 0 ? attr.Name.LocalName : string.Empty;
                        prefixToClrNamespace[prefix] = clrNs;
                        xmlNsToClrNs[attr.Value] = clrNs;
                    }
                }
            }

            if (prefixToClrNamespace.Count == 0)
            {
                continue;
            }

            var seenTargets = new HashSet<string>(StringComparer.Ordinal);
            void RecordTarget(string targetId, int line)
            {
                if (string.IsNullOrWhiteSpace(xClass))
                {
                    // ResourceDictionary / code-behindless XAML templates and styles instantiate/use these types at runtime
                    entryPoints.Add(targetId);
                    return;
                }

                if (!string.Equals(xClass, targetId, StringComparison.Ordinal) && seenTargets.Add(targetId))
                {
                    typeRefs.Add((xClass, targetId, new SourceLocation(relFromRoot, line, 1)));
                }
            }

            void RecordAttachedPropertyAccessors(string ownerTypeId, string propName)
            {
                if (string.IsNullOrWhiteSpace(ownerTypeId) || string.IsNullOrWhiteSpace(propName))
                {
                    return;
                }
                if (!eventHandlersByType.TryGetValue(ownerTypeId, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    eventHandlersByType[ownerTypeId] = set;
                }
                set.Add("Set" + propName);
                set.Add("Get" + propName);
            }

            foreach (var el in doc.Descendants())
            {
                var line = el is System.Xml.IXmlLineInfo li && li.HasLineInfo() ? li.LineNumber : 1;

                // Element tags like <views:MyControl /> or <local:MyConverter x:Key="..." />
                if (!string.IsNullOrEmpty(el.Name.NamespaceName) &&
                    xmlNsToClrNs.TryGetValue(el.Name.NamespaceName, out var targetNs))
                {
                    var localName = el.Name.LocalName;
                    var dotIdx = localName.IndexOf('.');
                    var typeName = dotIdx > 0 ? localName[..dotIdx] : localName;
                    var targetTypeId = $"{targetNs}.{typeName}";
                    RecordTarget(targetTypeId, line);
                    if (dotIdx > 0 && dotIdx + 1 < localName.Length)
                    {
                        RecordAttachedPropertyAccessors(targetTypeId, localName[(dotIdx + 1)..]);
                    }
                }

                foreach (var attr in el.Attributes())
                {
                    if (attr.IsNamespaceDeclaration)
                    {
                        continue;
                    }

                    // Attached property attributes like local:AttachedProperties.Header="..."
                    if (!string.IsNullOrEmpty(attr.Name.NamespaceName) &&
                        xmlNsToClrNs.TryGetValue(attr.Name.NamespaceName, out var attrTargetNs))
                    {
                        var attrLocal = attr.Name.LocalName;
                        var dotIdx = attrLocal.IndexOf('.');
                        if (dotIdx > 0)
                        {
                            var ownerTypeId = $"{attrTargetNs}.{attrLocal[..dotIdx]}";
                            RecordTarget(ownerTypeId, line);
                            if (dotIdx + 1 < attrLocal.Length)
                            {
                                RecordAttachedPropertyAccessors(ownerTypeId, attrLocal[(dotIdx + 1)..]);
                            }
                        }
                    }

                    var attrVal = attr.Value;
                    if (attrVal.Contains("x:Type", StringComparison.Ordinal))
                    {
                        // Attribute values like DataType="{x:Type vm:MainViewModel}" or TargetType="{x:Type local:Card}"
                        foreach (Match match in XTypeMarkupRegex().Matches(attrVal))
                        {
                            var prefix = match.Groups[1].Value;
                            var typeName = match.Groups[2].Value;
                            if (prefixToClrNamespace.TryGetValue(prefix, out var clrNs))
                            {
                                RecordTarget($"{clrNs}.{typeName}", line);
                            }
                        }
                    }

                    // Attached property or static member references in values like
                    // Property="app:AttachedProperties.IconType" or {TemplateBinding app:AttachedProperties.Header}
                    if (attrVal.Contains(':', StringComparison.Ordinal) && attrVal.Contains('.', StringComparison.Ordinal))
                    {
                        foreach (Match match in AttachedPropertyRefRegex().Matches(attrVal))
                        {
                            var prefix = match.Groups[1].Value;
                            var typeName = match.Groups[2].Value;
                            var propName = match.Groups[3].Value;
                            if (prefixToClrNamespace.TryGetValue(prefix, out var clrNs))
                            {
                                var ownerTypeId = $"{clrNs}.{typeName}";
                                RecordTarget(ownerTypeId, line);
                                RecordAttachedPropertyAccessors(ownerTypeId, propName);
                            }
                        }
                    }
                }
            }
        }

        return new XamlProjectScanResult(entryPoints, xamlClasses, typeRefs, eventHandlersByType);
    }

    private static string? ResolveStartupUriClass(
        string startupUri,
        string appRelPath,
        Dictionary<string, string> classByRelPath)
    {
        var normalized = startupUri.Replace('\\', '/');

        // Handle WPF pack URIs like "/MyApp;component/Views/MainWindow.xaml"
        var componentIdx = normalized.IndexOf(";component/", StringComparison.OrdinalIgnoreCase);
        if (componentIdx >= 0)
        {
            normalized = normalized[(componentIdx + ";component/".Length)..];
        }

        normalized = normalized.TrimStart('/');

        if (classByRelPath.TryGetValue(normalized, out var direct))
        {
            return direct;
        }

        var appDir = Path.GetDirectoryName(appRelPath)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(appDir))
        {
            var combined = $"{appDir}/{normalized}";
            if (classByRelPath.TryGetValue(combined, out var relativeMatch))
            {
                return relativeMatch;
            }
        }

        // Fallback: match by suffix (e.g. "MainWindow.xaml")
        var suffixMatch = classByRelPath.FirstOrDefault(kvp =>
            kvp.Key.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase) ||
            kvp.Key.Equals(normalized, StringComparison.OrdinalIgnoreCase));
        return suffixMatch.Value;
    }

    private static string? TryParseClrNamespace(string xmlNsValue)
    {
        if (xmlNsValue.StartsWith("clr-namespace:", StringComparison.OrdinalIgnoreCase))
        {
            var rest = xmlNsValue["clr-namespace:".Length..];
            var semi = rest.IndexOf(';');
            return (semi >= 0 ? rest[..semi] : rest).Trim();
        }

        if (xmlNsValue.StartsWith("using:", StringComparison.OrdinalIgnoreCase))
        {
            return xmlNsValue["using:".Length..].Trim();
        }

        return null;
    }

    private static List<string> EnumerateXamlFiles(string projectDir)
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
                    if (file.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
                        file.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(file);
                    }
                }

                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals(".git", StringComparison.OrdinalIgnoreCase))
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
