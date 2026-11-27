using System.Text.RegularExpressions;
using ArchLens.Model;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ArchLens.Extraction.Roslyn;

internal sealed record RazorCodeMethodInfo(
    string MethodId,
    string Name,
    AccessibilityKind Accessibility,
    bool IsStatic,
    bool IsOverride,
    bool IsBlazorLifecycle,
    int StartLine,
    int LineCount,
    int CyclomaticComplexity,
    int CognitiveComplexity,
    IReadOnlyList<string> CalledMethodNames);

internal sealed record RazorCodeAnalysisResult(
    int TotalCyclomatic,
    int MaxMethodCyclomatic,
    int TotalCognitive,
    int MaxMethodCognitive,
    int MemberCount,
    int Lcom4,
    IReadOnlyList<(string TypeName, int Line)> ReferencedTypes,
    IReadOnlyList<RazorCodeMethodInfo> Methods,
    IReadOnlySet<string> CalledFromPropertiesAndFields,
    IReadOnlyList<(string Name, IReadOnlySet<string> Uses)> InstanceMethodUses,
    IReadOnlySet<string> FieldOrPropNames);

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

    [GeneratedRegex(@"^\s*@layout\s+([A-Za-z_][A-Za-z0-9_\.]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LayoutDirectiveRegex();

    [GeneratedRegex(@"<\s*([A-Z][A-Za-z0-9_]*)(?:\s|/|>)", RegexOptions.CultureInvariant)]
    private static partial Regex ComponentTagRegex();

    [GeneratedRegex(@"\b(?:typeof|nameof)\s*\(\s*([A-Za-z_][A-Za-z0-9_\.]*)", RegexOptions.CultureInvariant)]
    private static partial Regex TypeofNameofRegex();

    [GeneratedRegex(@"\bnew\s+([A-Z][A-Za-z0-9_\.]*)\s*(?:<|\(|\{)", RegexOptions.CultureInvariant)]
    private static partial Regex ObjectCreationRegex();

    [GeneratedRegex(@"@\s*\(?\s*([A-Z][A-Za-z0-9_]*)\s*\.", RegexOptions.CultureInvariant)]
    private static partial Regex RazorStaticReceiverRegex();

    [GeneratedRegex(@"@(?:code|functions)\s*\{", RegexOptions.CultureInvariant)]
    private static partial Regex CodeBlockStartRegex();

    [GeneratedRegex(@"(?:@""(?:""""|[^""])*""|""(?:\\.|[^""\\\r\n])*"")", RegexOptions.CultureInvariant)]
    private static partial Regex CSharpStringLiteralRegex();

    [GeneratedRegex(@"(?:\.|\?\.)\s*([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>\r\n]+>)?\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex MemberCallRegex();

    [GeneratedRegex(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>\r\n]+>)?\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex DirectCallRegex();

    [GeneratedRegex(@"@on[a-z]+(?::[a-z]+)?\s*=\s*""\s*@?([A-Za-z_][A-Za-z0-9_]*)\s*""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RazorEventHandlerRegex();

    [GeneratedRegex(@"@on[a-z]+(?::[a-z]+)?\s*=\s*""\s*@?(?:[A-Za-z_][A-Za-z0-9_]*\.)+([A-Za-z_][A-Za-z0-9_]*)\s*""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RazorQualifiedEventHandlerRegex();

    // Match Blazor component parameter / directive attributes (PascalCase parameters like OnClick="..." or @bind/@ref),
    // excluding plain lowercase HTML attributes such as title="Unused" or class="Foo".
    [GeneratedRegex(@"(?:@bind[A-Za-z0-9_:-]*|@ref|\b[A-Z][A-Za-z0-9_]*)\s*=\s*""\s*@?([A-Za-z_][A-Za-z0-9_]*)\s*""", RegexOptions.CultureInvariant)]
    private static partial Regex RazorAttributeIdentifierRegex();

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

    private static readonly HashSet<string> BlazorLifecycleMethodNames = new(StringComparer.Ordinal)
    {
        "OnInitialized", "OnInitializedAsync", "OnParametersSet", "OnParametersSetAsync",
        "OnAfterRender", "OnAfterRenderAsync", "ShouldRender", "SetParametersAsync",
        "Dispose", "DisposeAsync", "BuildRenderTree"
    };

    public static RazorCodeAnalysisResult AnalyzeCodeBlocks(RazorComponentInfo comp)
    {
        var stripped = StripComments(comp.Content);
        var markupOnly = BlankCodeBlocksPreservingLines(stripped);
        var (markupCc, markupCog) = ComputeMarkupComplexity(markupOnly);
        var blocks = ExtractCodeBlocks(stripped);
        if (blocks.Count == 0)
        {
            return new RazorCodeAnalysisResult(
                markupCc, markupCc, markupCog, markupCog, 0, 0, [], [],
                new HashSet<string>(StringComparer.Ordinal),
                [],
                new HashSet<string>(StringComparer.Ordinal));
        }

        var totalCc = markupCc;
        var maxCc = markupCc;
        var totalCog = markupCog;
        var maxCog = markupCog;
        var memberCount = 0;
        var refTypes = new List<(string TypeName, int Line)>();
        var methods = new List<RazorCodeMethodInfo>();
        var fieldOrPropNames = new HashSet<string>(StringComparer.Ordinal);
        var methodMemberUses = new List<(string Name, HashSet<string> Uses)>();
        var calledFromPropsAndFields = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (body, startLine) in blocks)
        {
            var wrapped = "class __RazorCode {\n" + body + "\n}";
            var tree = CSharpSyntaxTree.ParseText(wrapped);
            var root = tree.GetRoot();
            var classDecl = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault();
            if (classDecl is null)
            {
                continue;
            }

            foreach (var member in classDecl.Members)
            {
                switch (member)
                {
                    case FieldDeclarationSyntax f:
                        memberCount += f.Declaration.Variables.Count;
                        foreach (var v in f.Declaration.Variables)
                        {
                            fieldOrPropNames.Add(v.Identifier.ValueText);
                            if (v.Initializer is not null)
                            {
                                foreach (var id in v.Initializer.DescendantNodes().OfType<IdentifierNameSyntax>())
                                {
                                    calledFromPropsAndFields.Add(id.Identifier.ValueText);
                                }
                            }
                        }
                        break;
                    case PropertyDeclarationSyntax p:
                        memberCount++;
                        fieldOrPropNames.Add(p.Identifier.ValueText);
                        if (p.ExpressionBody is not null)
                        {
                            var cc = CyclomaticComplexityWalker.Calculate(p.ExpressionBody);
                            var cog = CognitiveComplexityWalker.Calculate(p.ExpressionBody);
                            totalCc += cc; totalCog += cog;
                            if (cc > maxCc) maxCc = cc;
                            if (cog > maxCog) maxCog = cog;
                        }
                        foreach (var id in p.DescendantNodes().OfType<IdentifierNameSyntax>())
                        {
                            calledFromPropsAndFields.Add(id.Identifier.ValueText);
                        }
                        break;
                    case MethodDeclarationSyntax m:
                    {
                        memberCount++;
                        var cc = CyclomaticComplexityWalker.Calculate(m);
                        var cog = CognitiveComplexityWalker.Calculate(m, m.Identifier.ValueText, m.ParameterList.Parameters.Count);
                        totalCc += cc; totalCog += cog;
                        if (cc > maxCc) maxCc = cc;
                        if (cog > maxCog) maxCog = cog;

                        var span = m.GetLocation().GetLineSpan();
                        // Subtract 1 for the synthetic "class __RazorCode {\n" wrapper line
                        var mStartLine = startLine + Math.Max(0, span.StartLinePosition.Line - 1);
                        var mLines = Math.Max(1, span.EndLinePosition.Line - span.StartLinePosition.Line + 1);
                        var mName = m.Identifier.ValueText;
                        var paramSig = string.Join(", ", m.ParameterList.Parameters.Select(p => p.Type?.ToString().Trim() ?? "object"));
                        var methodId = $"{comp.TypeId}.{mName}({paramSig})";
                        var isStatic = m.Modifiers.Any(SyntaxKind.StaticKeyword);
                        var isOverride = m.Modifiers.Any(SyntaxKind.OverrideKeyword);
                        var hasJsInvokable = m.AttributeLists.SelectMany(a => a.Attributes)
                            .Any(a => a.Name.ToString().Contains("JSInvokable", StringComparison.Ordinal));
                        var isLifecycle = isOverride || hasJsInvokable || BlazorLifecycleMethodNames.Contains(mName);
                        var acc = MapSyntaxAccessibility(m.Modifiers);

                        var calledNames = new HashSet<string>(StringComparer.Ordinal);
                        var idents = new HashSet<string>(StringComparer.Ordinal);
                        SyntaxNode? bodyRoot = (SyntaxNode?)m.Body ?? m.ExpressionBody;
                        if (bodyRoot is not null)
                        {
                            foreach (var n in bodyRoot.DescendantNodes())
                            {
                                if (n is InvocationExpressionSyntax inv)
                                {
                                    var callee = inv.Expression switch
                                    {
                                        IdentifierNameSyntax id => id.Identifier.ValueText,
                                        GenericNameSyntax gn => gn.Identifier.ValueText,
                                        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name: SimpleNameSyntax sn } => sn.Identifier.ValueText,
                                        _ => null
                                    };
                                    if (!string.IsNullOrEmpty(callee)) calledNames.Add(callee);
                                }
                                else if (n is IdentifierNameSyntax idName)
                                {
                                    // Captures both field/property accesses (for LCOM4) and method-group references
                                    // such as event subscriptions (`lobby.ChatReceived += OnChatReceived`) and delegates (`InvokeAsync(RefreshAsync)`).
                                    var text = idName.Identifier.ValueText;
                                    idents.Add(text);
                                    if (idName.Parent is not MemberAccessExpressionSyntax ma || ma.Expression is ThisExpressionSyntax)
                                    {
                                        calledNames.Add(text);
                                    }
                                }
                            }
                        }

                        if (!isStatic && !Lcom4Calculator.IsTrivialNoOpSyntax(m))
                        {
                            methodMemberUses.Add((mName, idents));
                        }

                        methods.Add(new RazorCodeMethodInfo(
                            MethodId: methodId,
                            Name: mName,
                            Accessibility: acc,
                            IsStatic: isStatic,
                            IsOverride: isOverride,
                            IsBlazorLifecycle: isLifecycle,
                            StartLine: mStartLine,
                            LineCount: mLines,
                            CyclomaticComplexity: cc,
                            CognitiveComplexity: cog,
                            CalledMethodNames: calledNames.ToList()));
                        break;
                    }
                    case EventDeclarationSyntax or EventFieldDeclarationSyntax:
                        memberCount++;
                        break;
                }
            }

            // Collect real C# AST type references (never string literals, comments, or right-hand member accesses)
            CollectAstTypeReferences(classDecl, startLine, refTypes);
        }

        var lcom4 = ComputeCodeBlockLcom4(methodMemberUses, fieldOrPropNames);
        var readonlyUses = methodMemberUses
            .Select(m => (m.Name, (IReadOnlySet<string>)m.Uses))
            .ToList();
        return new RazorCodeAnalysisResult(
            totalCc, maxCc, totalCog, maxCog, memberCount, lcom4, refTypes, methods,
            calledFromPropsAndFields, readonlyUses, fieldOrPropNames);
    }

    private static (int Cyclomatic, int Cognitive) ComputeMarkupComplexity(string markup)
    {
        if (string.IsNullOrWhiteSpace(markup))
        {
            return (0, 0);
        }

        var decisions = 0;
        var cognitive = 0;
        var braceStack = new Stack<byte>(); // 0 = plain block, 1 = control-flow nesting block, 2 = @{ ... } inline code block
        var nestingDepth = 0;
        var inlineCodeBlockDepth = 0;
        var pendingControlBrace = false;

        int i = 0;
        var len = markup.Length;

        bool MatchKeywordAt(int pos, string kw)
        {
            if (pos < 0 || pos + kw.Length > len) return false;
            if (!markup.AsSpan(pos, kw.Length).Equals(kw, StringComparison.Ordinal)) return false;
            if (pos + kw.Length < len && (char.IsLetterOrDigit(markup[pos + kw.Length]) || markup[pos + kw.Length] == '_'))
            {
                return false;
            }
            return true;
        }

        void CountConditionOperators(int openParenIdx, out int closeParenIdx)
        {
            var pDepth = 0;
            var inStr = false;
            var inChar = false;
            string? lastLogical = null;
            closeParenIdx = openParenIdx;

            for (var j = openParenIdx; j < len; j++)
            {
                var ch = markup[j];
                if (inStr)
                {
                    if (ch == '\\') { j++; continue; }
                    if (ch == '"') inStr = false;
                    continue;
                }
                if (inChar)
                {
                    if (ch == '\\') { j++; continue; }
                    if (ch == '\'') inChar = false;
                    continue;
                }
                if (ch == '"') { inStr = true; continue; }
                if (ch == '\'') { inChar = true; continue; }

                if (ch == '(') { pDepth++; continue; }
                if (ch == ')')
                {
                    pDepth--;
                    if (pDepth == 0)
                    {
                        closeParenIdx = j;
                        return;
                    }
                    continue;
                }

                if (j + 1 < len)
                {
                    var two = markup.AsSpan(j, 2);
                    if (two.Equals("&&", StringComparison.Ordinal) || two.Equals("||", StringComparison.Ordinal))
                    {
                        decisions++;
                        var op = two.ToString();
                        if (!string.Equals(lastLogical, op, StringComparison.Ordinal))
                        {
                            cognitive++;
                            lastLogical = op;
                        }
                        j++;
                        continue;
                    }
                    if (two.Equals("??", StringComparison.Ordinal))
                    {
                        decisions++;
                        j++;
                        continue;
                    }
                }
                if (ch == '?' && j + 1 < len && markup[j + 1] != '?' && markup[j + 1] != '.' && markup[j + 1] != '[')
                {
                    decisions++;
                    cognitive += 1 + nestingDepth;
                }
            }
        }

        while (i < len)
        {
            var c = markup[i];

            if (c == '"')
            {
                i++;
                while (i < len)
                {
                    if (markup[i] == '\\') { i += 2; continue; }
                    if (markup[i] == '"') { i++; break; }
                    i++;
                }
                continue;
            }

            if (c == '{')
            {
                braceStack.Push(pendingControlBrace ? (byte)1 : (byte)0);
                if (pendingControlBrace)
                {
                    nestingDepth++;
                    pendingControlBrace = false;
                }
                i++;
                continue;
            }

            if (c == '}')
            {
                if (braceStack.Count > 0)
                {
                    var popped = braceStack.Pop();
                    if (popped == 1)
                    {
                        nestingDepth = Math.Max(0, nestingDepth - 1);
                    }
                    else if (popped == 2)
                    {
                        inlineCodeBlockDepth = Math.Max(0, inlineCodeBlockDepth - 1);
                    }
                }
                i++;
                // Check for trailing `else if (...)` or `else {` or `catch (...)` right after `}`
                var k = i;
                while (k < len && char.IsWhiteSpace(markup[k])) k++;
                if (MatchKeywordAt(k, "else"))
                {
                    var afterElse = k + 4;
                    while (afterElse < len && char.IsWhiteSpace(markup[afterElse])) afterElse++;
                    if (MatchKeywordAt(afterElse, "if"))
                    {
                        decisions++;
                        cognitive++;
                        var p = afterElse + 2;
                        while (p < len && char.IsWhiteSpace(markup[p])) p++;
                        if (p < len && markup[p] == '(')
                        {
                            CountConditionOperators(p, out var closeP);
                            i = closeP + 1;
                        }
                        else
                        {
                            i = afterElse + 2;
                        }
                        pendingControlBrace = true;
                        continue;
                    }
                    else if (afterElse < len && markup[afterElse] == '{')
                    {
                        cognitive++;
                        i = afterElse;
                        pendingControlBrace = true;
                        continue;
                    }
                }
                else if (MatchKeywordAt(k, "catch"))
                {
                    decisions++;
                    cognitive += 1 + nestingDepth;
                    var p = k + 5;
                    while (p < len && char.IsWhiteSpace(markup[p])) p++;
                    if (p < len && markup[p] == '(')
                    {
                        CountConditionOperators(p, out var closeP);
                        i = closeP + 1;
                    }
                    else
                    {
                        i = k + 5;
                    }
                    pendingControlBrace = true;
                    continue;
                }
                continue;
            }

            if (c == '@')
            {
                if (i + 1 < len && markup[i + 1] == '@')
                {
                    i += 2;
                    continue;
                }

                var kwStart = i + 1;
                string? matchedKw = null;
                if (MatchKeywordAt(kwStart, "foreach")) matchedKw = "foreach";
                else if (MatchKeywordAt(kwStart, "switch")) matchedKw = "switch";
                else if (MatchKeywordAt(kwStart, "while")) matchedKw = "while";
                else if (MatchKeywordAt(kwStart, "for")) matchedKw = "for";
                else if (MatchKeywordAt(kwStart, "if")) matchedKw = "if";
                else if (MatchKeywordAt(kwStart, "do")) matchedKw = "do";
                else if (MatchKeywordAt(kwStart, "try")) matchedKw = "try";

                if (matchedKw is not null)
                {
                    if (matchedKw != "try")
                    {
                        if (matchedKw != "switch") decisions++;
                        cognitive += 1 + nestingDepth;
                    }
                    var p = kwStart + matchedKw.Length;
                    while (p < len && char.IsWhiteSpace(markup[p])) p++;
                    if (p < len && markup[p] == '(')
                    {
                        CountConditionOperators(p, out var closeP);
                        i = closeP + 1;
                    }
                    else
                    {
                        i = kwStart + matchedKw.Length;
                    }
                    pendingControlBrace = matchedKw != "try";
                    continue;
                }

                // Explicit Razor expression `@(a && b ? x : y)`
                if (i + 1 < len && markup[i + 1] == '(')
                {
                    CountConditionOperators(i + 1, out var closeP);
                    i = closeP + 1;
                    continue;
                }

                // Razor inline code block `@{ ... }`
                if (i + 1 < len && markup[i + 1] == '{')
                {
                    braceStack.Push(2);
                    inlineCodeBlockDepth++;
                    i += 2;
                    continue;
                }
            }

            if ((nestingDepth > 0 || inlineCodeBlockDepth > 0) && (i == 0 || !char.IsLetterOrDigit(markup[i - 1]) && markup[i - 1] != '_' && markup[i - 1] != '@'))
            {
                if (MatchKeywordAt(i, "case"))
                {
                    var afterCase = i + 4;
                    if (afterCase < len && char.IsWhiteSpace(markup[afterCase]))
                    {
                        decisions++;
                        i = afterCase;
                        continue;
                    }
                }

                // Bare (non-@-prefixed) C# control-flow statements inside @if/@foreach/@{...} code blocks
                string? bareKw = null;
                if (MatchKeywordAt(i, "foreach")) bareKw = "foreach";
                else if (MatchKeywordAt(i, "switch")) bareKw = "switch";
                else if (MatchKeywordAt(i, "while")) bareKw = "while";
                else if (MatchKeywordAt(i, "for")) bareKw = "for";
                else if (MatchKeywordAt(i, "if")) bareKw = "if";
                else if (MatchKeywordAt(i, "do")) bareKw = "do";
                else if (MatchKeywordAt(i, "try")) bareKw = "try";

                if (bareKw is not null)
                {
                    var afterKw = i + bareKw.Length;
                    var p = afterKw;
                    while (p < len && char.IsWhiteSpace(markup[p])) p++;
                    var looksLikeControlStmt = bareKw is "do" or "try"
                        ? (p < len && markup[p] == '{')
                        : (p < len && markup[p] == '(');
                    if (looksLikeControlStmt)
                    {
                        if (bareKw != "try")
                        {
                            if (bareKw != "switch") decisions++;
                            cognitive += 1 + nestingDepth;
                        }
                        if (p < len && markup[p] == '(')
                        {
                            CountConditionOperators(p, out var closeP);
                            i = closeP + 1;
                        }
                        else
                        {
                            i = afterKw;
                        }
                        pendingControlBrace = bareKw != "try";
                        continue;
                    }
                }
            }

            i++;
        }

        return decisions == 0 && cognitive == 0 ? (0, 0) : (1 + decisions, cognitive);
    }

    private static AccessibilityKind MapSyntaxAccessibility(SyntaxTokenList modifiers)
    {
        var hasPublic = modifiers.Any(SyntaxKind.PublicKeyword);
        var hasInternal = modifiers.Any(SyntaxKind.InternalKeyword);
        var hasProtected = modifiers.Any(SyntaxKind.ProtectedKeyword);
        var hasPrivate = modifiers.Any(SyntaxKind.PrivateKeyword);
        if (hasPublic) return AccessibilityKind.Public;
        if (hasProtected && hasInternal) return AccessibilityKind.ProtectedInternal;
        if (hasPrivate && hasProtected) return AccessibilityKind.PrivateProtected;
        if (hasInternal) return AccessibilityKind.Internal;
        if (hasProtected) return AccessibilityKind.Protected;
        return AccessibilityKind.Private;
    }

    private static int ComputeCodeBlockLcom4(
        List<(string Name, HashSet<string> Uses)> methods,
        HashSet<string> fieldsAndProps)
    {
        if (methods.Count <= 1) return methods.Count;
        if (fieldsAndProps.Count == 0) return 1;

        var parent = Enumerable.Range(0, methods.Count).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { var ra = Find(a); var rb = Find(b); if (ra != rb) parent[ra] = rb; }

        var nameToIdx = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < methods.Count; i++)
        {
            if (!nameToIdx.TryGetValue(methods[i].Name, out var list))
            {
                list = [];
                nameToIdx[methods[i].Name] = list;
            }
            list.Add(i);
        }

        for (var i = 0; i < methods.Count; i++)
        {
            foreach (var u in methods[i].Uses)
            {
                if (nameToIdx.TryGetValue(u, out var targets))
                {
                    foreach (var t in targets) Union(i, t);
                }
            }
            for (var j = i + 1; j < methods.Count; j++)
            {
                if (methods[i].Uses.Any(u => fieldsAndProps.Contains(u) && methods[j].Uses.Contains(u)))
                {
                    Union(i, j);
                }
            }
        }

        var roots = new HashSet<int>();
        for (var i = 0; i < methods.Count; i++) roots.Add(Find(i));
        return roots.Count;
    }

    private static void CollectAstTypeReferences(
        ClassDeclarationSyntax classDecl,
        int blockStartLine,
        List<(string TypeName, int Line)> output)
    {
        void AddTypeSyntax(TypeSyntax? ts)
        {
            if (ts is null) return;
            var line = blockStartLine + Math.Max(0, ts.GetLocation().GetLineSpan().StartLinePosition.Line - 1);
            switch (ts)
            {
                case GenericNameSyntax gn:
                    output.Add((gn.Identifier.ValueText, line));
                    foreach (var arg in gn.TypeArgumentList.Arguments) AddTypeSyntax(arg);
                    break;
                case QualifiedNameSyntax qn:
                    output.Add((qn.ToString().Trim(), line));
                    if (qn.Right is GenericNameSyntax rgn)
                    {
                        foreach (var arg in rgn.TypeArgumentList.Arguments) AddTypeSyntax(arg);
                    }
                    break;
                case IdentifierNameSyntax id when id.Identifier.ValueText.Length >= 2 && char.IsUpper(id.Identifier.ValueText[0]):
                    output.Add((id.Identifier.ValueText, line));
                    break;
                case NullableTypeSyntax nt:
                    AddTypeSyntax(nt.ElementType);
                    break;
                case ArrayTypeSyntax at:
                    AddTypeSyntax(at.ElementType);
                    break;
                case TupleTypeSyntax tt:
                    foreach (var e in tt.Elements) AddTypeSyntax(e.Type);
                    break;
            }
        }

        foreach (var node in classDecl.DescendantNodes())
        {
            switch (node)
            {
                case VariableDeclarationSyntax vd when !vd.Type.IsVar:
                    AddTypeSyntax(vd.Type);
                    break;
                case ParameterSyntax p:
                    AddTypeSyntax(p.Type);
                    break;
                case PropertyDeclarationSyntax prop:
                    AddTypeSyntax(prop.Type);
                    break;
                case MethodDeclarationSyntax m:
                    AddTypeSyntax(m.ReturnType);
                    break;
                case ObjectCreationExpressionSyntax oc:
                    AddTypeSyntax(oc.Type);
                    break;
                case TypeOfExpressionSyntax tof:
                    AddTypeSyntax(tof.Type);
                    break;
                case CastExpressionSyntax cast:
                    AddTypeSyntax(cast.Type);
                    break;
                case BinaryExpressionSyntax bin when bin.IsKind(SyntaxKind.AsExpression) || bin.IsKind(SyntaxKind.IsExpression):
                    if (bin.Right is TypeSyntax rts) AddTypeSyntax(rts);
                    break;
                case DeclarationPatternSyntax dp:
                    AddTypeSyntax(dp.Type);
                    break;
                case TypePatternSyntax tp:
                    AddTypeSyntax(tp.Type);
                    break;
                case GenericNameSyntax gn:
                    AddTypeSyntax(gn);
                    break;
                case MemberAccessExpressionSyntax ma when ma.Expression is IdentifierNameSyntax leftId &&
                    leftId.Identifier.ValueText.Length >= 2 && char.IsUpper(leftId.Identifier.ValueText[0]):
                {
                    var line = blockStartLine + Math.Max(0, leftId.GetLocation().GetLineSpan().StartLinePosition.Line - 1);
                    output.Add((leftId.Identifier.ValueText, line));
                    break;
                }
            }
        }
    }

    private static List<(string Body, int StartLine)> ExtractCodeBlocks(string stripped)
    {
        var blocks = new List<(string Body, int StartLine)>();
        foreach (Match m in CodeBlockStartRegex().Matches(stripped))
        {
            var openBrace = m.Index + m.Length - 1;
            var depth = 0;
            var inString = false;
            var inVerbatim = false;
            var inChar = false;
            var endBrace = -1;

            for (var i = openBrace; i < stripped.Length; i++)
            {
                var c = stripped[i];
                if (inVerbatim)
                {
                    if (c == '"')
                    {
                        if (i + 1 < stripped.Length && stripped[i + 1] == '"') { i++; continue; }
                        inVerbatim = false;
                    }
                    continue;
                }
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (inChar)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '\'') inChar = false;
                    continue;
                }

                if (c == '@' && i + 1 < stripped.Length && stripped[i + 1] == '"') { inVerbatim = true; i++; continue; }
                if (c == '"') { inString = true; continue; }
                if (c == '\'') { inChar = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        endBrace = i;
                        break;
                    }
                }
            }

            if (endBrace > openBrace)
            {
                var body = stripped[(openBrace + 1)..endBrace];
                var startLine = ComputeLineNumber(stripped, openBrace + 1);
                blocks.Add((body, startLine));
            }
        }
        return blocks;
    }

    private static string BlankCodeBlocksPreservingLines(string stripped)
    {
        var chars = stripped.ToCharArray();
        foreach (Match m in CodeBlockStartRegex().Matches(stripped))
        {
            var openBrace = m.Index + m.Length - 1;
            var depth = 0;
            var inString = false;
            var endBrace = -1;
            for (var i = openBrace; i < stripped.Length; i++)
            {
                var c = stripped[i];
                if (inString)
                {
                    if (c == '\\') { i++; continue; }
                    if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') { inString = true; continue; }
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0) { endBrace = i; break; }
                }
            }

            if (endBrace > openBrace)
            {
                for (var k = m.Index; k <= endBrace; k++)
                {
                    if (chars[k] != '\n' && chars[k] != '\r') chars[k] = ' ';
                }
            }
        }
        return new string(chars);
    }

    public static void ResolveAndRecordReferences(
        IReadOnlyList<RazorComponentInfo> components,
        HashSet<string> knownTypeIds,
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> edgeAggregator,
        Dictionary<string, string>? projectByTypeId = null,
        Dictionary<string, HashSet<string>>? allowedProjectsByProject = null)
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

            HashSet<string>? allowedProjects = null;
            if (allowedProjectsByProject is not null && allowedProjectsByProject.TryGetValue(comp.ProjectName, out var ap))
            {
                allowedProjects = ap;
            }

            bool IsTargetAccessible(string targetTypeId)
            {
                if (allowedProjects is null || projectByTypeId is null) return true;
                return !projectByTypeId.TryGetValue(targetTypeId, out var targetProj) || allowedProjects.Contains(targetProj);
            }

            var stripped = StripComments(comp.Content);
            var markupOnly = BlankCodeBlocksPreservingLines(stripped);
            var markupNoStrings = StripStringLiteralsPreservingLines(markupOnly);
            var seenTargets = new HashSet<(string Target, DependencyKind Kind)>();

            void TryRecordCandidate(string rawName, DependencyKind kind, int line, bool allowUniqueFallback = false)
            {
                var resolved = ResolveTypeName(
                    rawName, comp.Namespace, comp.UsingNamespaces, knownTypeIds, typesBySimpleName,
                    IsTargetAccessible, allowUniqueFallback);
                if (resolved is null || string.Equals(resolved, comp.TypeId, StringComparison.Ordinal))
                {
                    return;
                }

                if (seenTargets.Add((resolved, kind)))
                {
                    SymbolDependencyExtractor.AddEdge(edgeAggregator, comp.TypeId, resolved, kind, new SourceLocation(comp.RelativePath, line, 1));
                }
            }

            // 1. @inherits
            foreach (Match m in InheritsDirectiveRegex().Matches(stripped))
            {
                var line = ComputeLineNumber(stripped, m.Index);
                foreach (var typeTok in SplitTypeTokens(m.Groups[1].Value))
                {
                    TryRecordCandidate(typeTok, DependencyKind.Inherits, line, allowUniqueFallback: true);
                }
            }

            // 2. @implements
            foreach (Match m in ImplementsDirectiveRegex().Matches(stripped))
            {
                var line = ComputeLineNumber(stripped, m.Index);
                foreach (var typeTok in SplitTypeTokens(m.Groups[1].Value))
                {
                    TryRecordCandidate(typeTok, DependencyKind.Implements, line, allowUniqueFallback: true);
                }
            }

            // 3. @inject
            foreach (Match m in InjectDirectiveRegex().Matches(stripped))
            {
                var line = ComputeLineNumber(stripped, m.Index);
                foreach (var typeTok in SplitTypeTokens(m.Groups[1].Value))
                {
                    TryRecordCandidate(typeTok, DependencyKind.FieldOrProperty, line, allowUniqueFallback: true);
                }
            }

            // 4. @layout
            foreach (Match m in LayoutDirectiveRegex().Matches(stripped))
            {
                var line = ComputeLineNumber(stripped, m.Index);
                TryRecordCandidate(m.Groups[1].Value, DependencyKind.TypeReference, line, allowUniqueFallback: true);
            }

            // 5. Child Razor component tags <MyComponent ...> (markup only, outside @code and string literals)
            foreach (Match m in ComponentTagRegex().Matches(markupNoStrings))
            {
                var tagName = m.Groups[1].Value;
                if (CommonSystemOrHtmlIdentifiers.Contains(tagName)) continue;
                var line = ComputeLineNumber(markupNoStrings, m.Index);
                TryRecordCandidate(tagName, DependencyKind.TypeReference, line, allowUniqueFallback: true);
            }

            // 6. typeof(...) / nameof(...) in markup (outside string literals)
            foreach (Match m in TypeofNameofRegex().Matches(markupNoStrings))
            {
                var line = ComputeLineNumber(markupNoStrings, m.Index);
                TryRecordCandidate(m.Groups[1].Value, DependencyKind.TypeReference, line, allowUniqueFallback: true);
            }

            // 7. new TypeName(...) in markup (outside string literals)
            foreach (Match m in ObjectCreationRegex().Matches(markupNoStrings))
            {
                var line = ComputeLineNumber(markupNoStrings, m.Index);
                TryRecordCandidate(m.Groups[1].Value, DependencyKind.ObjectCreation, line, allowUniqueFallback: true);
            }

            // 8. Explicit static-type receiver in Razor markup expressions: @TypeName.Member or @(TypeName.Member)
            foreach (Match m in RazorStaticReceiverRegex().Matches(markupNoStrings))
            {
                var ident = m.Groups[1].Value;
                if (ident.Length < 2 || CommonSystemOrHtmlIdentifiers.Contains(ident)) continue;
                var line = ComputeLineNumber(markupNoStrings, m.Index);
                TryRecordCandidate(ident, DependencyKind.TypeReference, line, allowUniqueFallback: false);
            }

            // 9. Exact C# AST type references inside @code / @functions blocks (never HTML text, strings, or attribute/member names)
            var codeAnalysis = AnalyzeCodeBlocks(comp);
            foreach (var (typeName, line) in codeAnalysis.ReferencedTypes)
            {
                if (CommonSystemOrHtmlIdentifiers.Contains(typeName)) continue;
                TryRecordCandidate(typeName, DependencyKind.TypeReference, line, allowUniqueFallback: false);
            }
        }
    }

    private static string StripStringLiteralsPreservingLines(string text)
    {
        return CSharpStringLiteralRegex().Replace(text, m =>
        {
            var nlCount = 0;
            foreach (var ch in m.Value) if (ch == '\n') nlCount++;
            return nlCount == 0 ? "\"\"" : "\"" + new string('\n', nlCount) + "\"";
        });
    }

    /// <summary>
    /// Links methods invoked in .razor markup/@code/@on* handlers back to their calling Razor component
    /// (via `calledFromMemberBodyBy`) so method-scope dead-code analysis traces reachability through Blazor components.
    /// </summary>
    public static void ResolveMethodReferences(
        IReadOnlyList<RazorComponentInfo> components,
        List<CodeNode> nodes,
        Dictionary<(string From, string To, DependencyKind Kind), (int Weight, SourceLocation? Location)> edgeAggregator)
    {
        if (components.Count == 0)
        {
            return;
        }

        // Build outgoing type adjacency so component -> @inject state -> service interface (2 hops) is covered.
        var typeAdj = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (from, to, kind) in edgeAggregator.Keys)
        {
            if (kind == DependencyKind.ProjectReference)
            {
                continue;
            }

            if (!typeAdj.TryGetValue(from, out var targets))
            {
                targets = new HashSet<string>(StringComparer.Ordinal);
                typeAdj[from] = targets;
            }
            targets.Add(to);
        }

        // Index emitted Method nodes by (ContainingTypeId, MethodName) -> list of indices in `nodes`
        var methodsByOwnerAndName = new Dictionary<(string OwnerTypeId, string MethodName), List<int>>();
        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            if (n.Kind != NodeKind.Method || string.IsNullOrEmpty(n.ContainingTypeId))
            {
                continue;
            }

            var key = (n.ContainingTypeId, n.Name);
            if (!methodsByOwnerAndName.TryGetValue(key, out var list))
            {
                list = [];
                methodsByOwnerAndName[key] = list;
            }
            list.Add(i);
        }

        if (methodsByOwnerAndName.Count == 0)
        {
            return;
        }

        var callersByMethodIndex = new Dictionary<int, HashSet<string>>();

        foreach (var comp in components)
        {
            var stripped = RemoveHeaderDirectives(StripComments(comp.Content));
            var markupOnly = BlankCodeBlocksPreservingLines(stripped);
            var memberCalls = new HashSet<string>(StringComparer.Ordinal);
            var selfCalls = new HashSet<string>(StringComparer.Ordinal);

            foreach (Match m in MemberCallRegex().Matches(stripped))
            {
                memberCalls.Add(m.Groups[1].Value);
            }

            // Scan markup-only for selfCalls so @code method declarations (e.g. `private void NeverCalled()`) are never treated as self-calls.
            foreach (Match m in DirectCallRegex().Matches(markupOnly))
            {
                var name = m.Groups[1].Value;
                selfCalls.Add(name);
                memberCalls.Add(name);
            }

            foreach (Match m in DirectCallRegex().Matches(stripped))
            {
                memberCalls.Add(m.Groups[1].Value);
            }

            foreach (Match m in RazorEventHandlerRegex().Matches(markupOnly))
            {
                selfCalls.Add(m.Groups[1].Value);
            }

            foreach (Match m in RazorQualifiedEventHandlerRegex().Matches(markupOnly))
            {
                memberCalls.Add(m.Groups[1].Value);
            }

            foreach (Match m in RazorAttributeIdentifierRegex().Matches(markupOnly))
            {
                selfCalls.Add(m.Groups[1].Value);
            }

            if (memberCalls.Count == 0 && selfCalls.Count == 0)
            {
                continue;
            }

            // 1. Methods declared directly on this Razor component's code-behind class
            foreach (var name in selfCalls)
            {
                if (methodsByOwnerAndName.TryGetValue((comp.TypeId, name), out var indices))
                {
                    foreach (var idx in indices)
                    {
                        AddCaller(callersByMethodIndex, idx, comp.TypeId);
                    }
                }
            }

            // 2. Methods on types within 2 hops of this Razor component (e.g. Component -> SessionState -> ILobbyService)
            var neighborhood = new HashSet<string>(StringComparer.Ordinal);
            if (typeAdj.TryGetValue(comp.TypeId, out var hop1))
            {
                foreach (var t1 in hop1)
                {
                    neighborhood.Add(t1);
                    if (typeAdj.TryGetValue(t1, out var hop2))
                    {
                        foreach (var t2 in hop2)
                        {
                            neighborhood.Add(t2);
                        }
                    }
                }
            }

            foreach (var targetTypeId in neighborhood)
            {
                foreach (var name in memberCalls)
                {
                    if (methodsByOwnerAndName.TryGetValue((targetTypeId, name), out var indices))
                    {
                        foreach (var idx in indices)
                        {
                            AddCaller(callersByMethodIndex, idx, comp.TypeId);
                        }
                    }
                }
            }
        }

        foreach (var (nodeIdx, callers) in callersByMethodIndex)
        {
            var existing = nodes[nodeIdx];
            var updatedMeta = existing.Metadata is not null
                ? new Dictionary<string, string>(existing.Metadata, StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal);

            var mergedCallers = new HashSet<string>(callers, StringComparer.Ordinal);
            if (updatedMeta.TryGetValue("calledFromMemberBodyBy", out var prev) && !string.IsNullOrWhiteSpace(prev))
            {
                foreach (var item in prev.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    mergedCallers.Add(item);
                }
            }

            updatedMeta["calledFromMemberBodyBy"] = string.Join(";", mergedCallers);
            if (existing.ContainingTypeId is not null && mergedCallers.Contains(existing.ContainingTypeId))
            {
                updatedMeta["calledFromMemberBody"] = "true";
            }

            nodes[nodeIdx] = existing with { Metadata = updatedMeta };
        }
    }

    private static void AddCaller(Dictionary<int, HashSet<string>> map, int nodeIdx, string callerTypeId)
    {
        if (!map.TryGetValue(nodeIdx, out var set))
        {
            set = new HashSet<string>(StringComparer.Ordinal);
            map[nodeIdx] = set;
        }
        set.Add(callerTypeId);
    }

    private static string? ResolveTypeName(
        string rawName,
        string componentNamespace,
        IReadOnlyList<string> usings,
        HashSet<string> knownTypeIds,
        Dictionary<string, List<string>> typesBySimpleName,
        Func<string, bool> isTargetAccessible,
        bool allowUniqueFallback)
    {
        var cleaned = rawName.Trim().TrimEnd('?');
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return null;
        }

        if (knownTypeIds.Contains(cleaned) && isTargetAccessible(cleaned))
        {
            return cleaned;
        }

        var simple = ExtractSimpleName(cleaned);
        if (!typesBySimpleName.TryGetValue(simple, out var rawCandidates))
        {
            return null;
        }

        var candidates = rawCandidates.Where(isTargetAccessible).ToList();
        if (candidates.Count == 0)
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

        return ResolveFromCandidates(simple, componentNamespace, usings, candidates, allowUniqueFallback);
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
        static string PreserveNewlines(Match m)
        {
            var nlCount = 0;
            foreach (var ch in m.Value)
            {
                if (ch == '\n') nlCount++;
            }
            return nlCount == 0 ? " " : new string('\n', nlCount);
        }

        var s = RazorCommentRegex().Replace(content, PreserveNewlines);
        s = HtmlCommentRegex().Replace(s, PreserveNewlines);
        s = MultiLineCommentRegex().Replace(s, PreserveNewlines);
        s = SingleLineCommentRegex().Replace(s, " ");
        return s;
    }

    private static int ComputeLineNumber(string text, int index)
    {
        var line = 1;
        var limit = Math.Min(index, text.Length);
        for (var i = 0; i < limit; i++)
        {
            if (text[i] == '\n') line++;
        }
        return line;
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
        if (text[^1] == '\n') count--;
        return Math.Max(1, count);
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
