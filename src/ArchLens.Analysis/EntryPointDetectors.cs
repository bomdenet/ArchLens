using ArchLens.Model;

namespace ArchLens.Analysis;

/// <summary>
/// Built-in entry point detectors used by dead-code reachability analysis.
/// </summary>
public static class EntryPointDetectors
{
    public static IReadOnlyList<IEntryPointDetector> CreateBuiltIn(bool treatPublicApiAsEntryPoint = true)
    {
        var detectors = new List<IEntryPointDetector>
        {
            new ProgramMainEntryPointDetector(),
            new AspNetEntryPointDetector(),
            new MessagingHandlerEntryPointDetector(),
            new TestFrameworkEntryPointDetector(),
            new DiRegistrationEntryPointDetector()
        };

        if (treatPublicApiAsEntryPoint)
        {
            detectors.Add(new PublicApiEntryPointDetector());
        }

        return detectors;
    }

    internal static bool HasMetadataFlag(CodeNode node, string key) =>
        node.Metadata is not null &&
        node.Metadata.TryGetValue(key, out var val) &&
        val.Equals("true", StringComparison.OrdinalIgnoreCase);

    internal static IEnumerable<string> SplitMetadataList(CodeNode node, string key)
    {
        if (node.Metadata is null || !node.Metadata.TryGetValue(key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        // Semicolon delimiter is used for type lists (baseTypes, ctorParamTypes) to preserve multi-param generics.
        // Fall back to comma splitting when semicolons are absent and no generic angle brackets are present.
        var delimiter = raw.Contains(';', StringComparison.Ordinal) || raw.Contains('<', StringComparison.Ordinal)
            ? ';'
            : ',';

        return raw.Split(delimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    internal static bool HasAnyAttribute(CodeNode node, IReadOnlySet<string> attributeNames) =>
        SplitMetadataList(node, "attributes").Any(a =>
            attributeNames.Contains(a) ||
            (a.EndsWith("Attribute", StringComparison.Ordinal) && attributeNames.Contains(a[..^"Attribute".Length])));

    internal static bool HasAnyBaseTypeContaining(CodeNode node, IReadOnlyList<string> patterns) =>
        SplitMetadataList(node, "baseTypes").Any(bt =>
            patterns.Any(p => bt.Contains(p, StringComparison.OrdinalIgnoreCase)));
}

public sealed class ProgramMainEntryPointDetector : IEntryPointDetector
{
    public string Name => "ProgramMain";

    public bool IsEntryPoint(CodeNode node, CodeGraph graph)
    {
        if (EntryPointDetectors.HasMetadataFlag(node, "isEntryPointMain"))
        {
            return true;
        }

        if (node.Kind == NodeKind.Method)
        {
            if (node.IsStatic && (node.Name.Equals("Main", StringComparison.Ordinal) || node.Name.Equals("<Main>$", StringComparison.Ordinal)))
            {
                return true;
            }

            if (node.ContainingTypeId is not null &&
                graph.FindNode(node.ContainingTypeId) is { Name: "Startup" } &&
                (node.Name is "Configure" or "ConfigureServices" || node.Metadata?.GetValueOrDefault("methodKind") == "Constructor"))
            {
                return true;
            }

            return false;
        }

        if (node.Kind == NodeKind.Type)
        {
            return node.Name is "Program" or "Startup";
        }

        return false;
    }
}

public sealed class AspNetEntryPointDetector : IEntryPointDetector
{
    private static readonly HashSet<string> ControllerTypeAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApiController", "Controller", "Route", "McpServerToolType"
    };

    private static readonly HashSet<string> EndpointMethodAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "HttpHead", "HttpOptions",
        "AcceptVerbs", "Route", "McpServerTool", "McpServerPrompt", "McpServerResource"
    };

    private static readonly string[] WebBaseTypes =
    [
        "ControllerBase", "Microsoft.AspNetCore.Mvc.Controller", "PageModel",
        "Microsoft.AspNetCore.SignalR.Hub", "IHostedService", "BackgroundService",
        "IMiddleware", "IAsyncActionFilter", "IActionFilter", "IExceptionFilter"
    ];

    public string Name => "AspNetEndpoints";

    public bool IsEntryPoint(CodeNode node, CodeGraph graph)
    {
        if (EntryPointDetectors.HasMetadataFlag(node, "isMinimalApiHandler"))
        {
            return true;
        }

        if (node.Kind == NodeKind.Type)
        {
            return EntryPointDetectors.HasMetadataFlag(node, "hasEndpointMethods") ||
                   EntryPointDetectors.HasAnyAttribute(node, ControllerTypeAttributes) ||
                   EntryPointDetectors.HasAnyBaseTypeContaining(node, WebBaseTypes) ||
                   (node.TypeKind == TypeKind.Class && node.Name.EndsWith("Controller", StringComparison.Ordinal) && node.Accessibility == AccessibilityKind.Public);
        }

        if (node.Kind == NodeKind.Method)
        {
            if (EntryPointDetectors.HasAnyAttribute(node, EndpointMethodAttributes))
            {
                return true;
            }

            if (node.Accessibility == AccessibilityKind.Public &&
                node.ContainingTypeId is not null &&
                graph.FindNode(node.ContainingTypeId) is { } parent &&
                IsEntryPoint(parent, graph))
            {
                return true;
            }
        }

        return false;
    }
}

public sealed class MessagingHandlerEntryPointDetector : IEntryPointDetector
{
    private static readonly string[] HandlerInterfaces =
    [
        "IRequestHandler", "INotificationHandler", "IStreamRequestHandler", "IPipelineBehavior",
        "IRequestPreProcessor", "IRequestPostProcessor", "IRequestExceptionHandler",
        "MassTransit.IConsumer", "IConsumer<", "ISaga", "IStateMachineActivity", "IHandleMessages"
    ];

    public string Name => "MessagingHandlers";

    public bool IsEntryPoint(CodeNode node, CodeGraph graph)
    {
        if (node.Kind == NodeKind.Type)
        {
            return EntryPointDetectors.HasAnyBaseTypeContaining(node, HandlerInterfaces);
        }

        if (node.Kind == NodeKind.Method &&
            node.ContainingTypeId is not null &&
            graph.FindNode(node.ContainingTypeId) is { } parent &&
            IsEntryPoint(parent, graph))
        {
            return node.Name is "Handle" or "Consume" or "Process" ||
                   node.Metadata?.GetValueOrDefault("methodKind") == "Constructor";
        }

        return false;
    }
}

public sealed class TestFrameworkEntryPointDetector : IEntryPointDetector
{
    private static readonly HashSet<string> TestMethodAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Fact", "Theory", "Test", "TestCase", "TestCaseSource", "TestMethod", "DataTestMethod",
        "SetUp", "TearDown", "OneTimeSetUp", "OneTimeTearDown", "TestInitialize", "TestCleanup",
        "ClassInitialize", "ClassCleanup", "AssemblyInitialize", "AssemblyCleanup"
    };

    private static readonly HashSet<string> TestTypeAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TestFixture", "TestClass", "CollectionDefinition"
    };

    public string Name => "TestFrameworks";

    public bool IsEntryPoint(CodeNode node, CodeGraph graph)
    {
        if (node.Kind == NodeKind.Method)
        {
            if (EntryPointDetectors.HasAnyAttribute(node, TestMethodAttributes))
            {
                return true;
            }

            if (node.Metadata?.GetValueOrDefault("methodKind") == "Constructor" &&
                node.ContainingTypeId is not null &&
                graph.FindNode(node.ContainingTypeId) is { } testClass &&
                IsEntryPoint(testClass, graph))
            {
                return true;
            }

            return false;
        }

        if (node.Kind == NodeKind.Type)
        {
            if (EntryPointDetectors.HasMetadataFlag(node, "hasTestMethods") ||
                EntryPointDetectors.HasAnyAttribute(node, TestTypeAttributes))
            {
                return true;
            }

            // Fallback for synthetic graphs that include Method nodes without hasTestMethods metadata
            return graph.GetNodes(NodeKind.Method)
                .Any(m => string.Equals(m.ContainingTypeId, node.Id, StringComparison.Ordinal) &&
                          EntryPointDetectors.HasAnyAttribute(m, TestMethodAttributes));
        }

        return false;
    }
}

public sealed class DiRegistrationEntryPointDetector : IEntryPointDetector
{
    public string Name => "DiRegistrations";

    public bool IsEntryPoint(CodeNode node, CodeGraph graph)
    {
        if (graph.DiRegistrations.Count == 0)
        {
            return false;
        }

        if (node.Kind == NodeKind.Type)
        {
            return graph.DiRegistrations.Any(r =>
                string.Equals(r.ImplementationTypeId, node.Id, StringComparison.Ordinal) ||
                string.Equals(r.ServiceTypeId, node.Id, StringComparison.Ordinal));
        }

        if (node.Kind == NodeKind.Method &&
            node.ContainingTypeId is not null &&
            node.Metadata?.GetValueOrDefault("methodKind") == "Constructor")
        {
            return graph.DiRegistrations.Any(r =>
                string.Equals(r.ImplementationTypeId, node.ContainingTypeId, StringComparison.Ordinal));
        }

        return false;
    }
}

public sealed class PublicApiEntryPointDetector : IEntryPointDetector
{
    public string Name => "PublicLibraryApi";

    public bool IsEntryPoint(CodeNode node, CodeGraph graph)
    {
        if (node.Kind is not (NodeKind.Type or NodeKind.Method))
        {
            return false;
        }

        if (!IsExternallyVisible(node.Accessibility))
        {
            return false;
        }

        var outputKind = node.Metadata?.GetValueOrDefault("outputKind");
        if (string.Equals(outputKind, "ConsoleApplication", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(outputKind, "WindowsApplication", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Verify all enclosing types (for methods and nested types) are also externally visible
        var parentId = node.ContainingTypeId;
        var guard = 0;
        while (parentId is not null && guard++ < 16)
        {
            var parent = graph.FindNode(parentId);
            if (parent is null)
            {
                break;
            }

            if (!IsExternallyVisible(parent.Accessibility))
            {
                return false;
            }

            parentId = parent.ContainingTypeId;
        }

        return true;
    }

    private static bool IsExternallyVisible(AccessibilityKind accessibility) =>
        accessibility is AccessibilityKind.Public or AccessibilityKind.Protected or AccessibilityKind.ProtectedInternal;
}
