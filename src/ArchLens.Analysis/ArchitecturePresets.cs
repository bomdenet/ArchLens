using ArchLens.Model;

namespace ArchLens.Analysis;

/// <summary>
/// Ready-to-use architectural rule presets for Clean Architecture, Vertical Slice Architecture,
/// Modular Monolith, and Domain-Driven Design (DDD).
/// </summary>
public static class ArchitecturePresets
{
    public sealed record PresetInfo(
        string Id,
        string Name,
        string Description,
        IReadOnlyList<string> Aliases);

    public static IReadOnlyList<PresetInfo> All { get; } =
    [
        new("clean-architecture", "Clean Architecture",
            "Enforces inward dependency flow (Domain <- Application <- Infrastructure/Presentation), acyclic namespaces, zero captive DI dependencies, and complexity/cohesion limits.",
            ["clean", "onion"]),
        new("vertical-slice", "Vertical Slice Architecture",
            "Isolates feature slices under *.Features.<Slice>.* from cross-slice dependencies, keeps Shared/Common independent of features, and guards complexity & DI lifetimes.",
            ["vsa", "slices"]),
        new("modular-monolith", "Modular Monolith",
            "Enforces strict boundary isolation across *.Modules.<Module>.* so modules communicate only via .Contracts / .Public / .IntegrationEvents namespaces, with zero cycles or captive DI.",
            ["modular", "modules"]),
        new("ddd", "Domain-Driven Design (DDD)",
            "Protects the Domain model from Infrastructure/Presentation/Application leakage, enforces repository/interface naming conventions, acyclic namespaces, and cohesive aggregates (LCOM4).",
            ["domain-driven-design"])
    ];

    public static bool TryResolvePresetId(string name, out string canonicalId)
    {
        var normalized = name.Trim().ToLowerInvariant();
        foreach (var preset in All)
        {
            if (preset.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                preset.Aliases.Any(a => a.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            {
                canonicalId = preset.Id;
                return true;
            }
        }

        canonicalId = string.Empty;
        return false;
    }

    public static IReadOnlyList<IArchitectureRule> CreateRules(string presetName, string? rootNamespace = null)
    {
        if (!TryResolvePresetId(presetName, out var canonical))
        {
            throw new ArgumentException(
                $"Unknown architectural preset '{presetName}'. Available presets: {string.Join(", ", All.Select(p => p.Id))}.",
                nameof(presetName));
        }

        var normalizedRoot = NormalizeRootNamespace(rootNamespace);

        return canonical switch
        {
            "clean-architecture" => CreateCleanArchitectureRules(normalizedRoot),
            "vertical-slice" => CreateVerticalSliceRules(normalizedRoot),
            "modular-monolith" => CreateModularMonolithRules(normalizedRoot),
            "ddd" => CreateDddRules(normalizedRoot),
            _ => throw new ArgumentException($"Unsupported preset '{presetName}'.", nameof(presetName))
        };
    }

    public static string GenerateYamlTemplate(string presetName, string? rootNamespace = null)
    {
        if (!TryResolvePresetId(presetName, out var canonical))
        {
            throw new ArgumentException(
                $"Unknown architectural preset '{presetName}'. Available presets: {string.Join(", ", All.Select(p => p.Id))}.",
                nameof(presetName));
        }

        var sanitizedRoot = rootNamespace?
            .Replace("\r", "", StringComparison.Ordinal)
            .Replace("\n", "", StringComparison.Ordinal)
            .Replace("\\", "", StringComparison.Ordinal)
            .Replace("\"", "", StringComparison.Ordinal)
            .Trim();

        var nsLine = string.IsNullOrWhiteSpace(sanitizedRoot)
            ? "# root_namespace: MyApp"
            : $"root_namespace: \"{sanitizedRoot}\"";

        return $"""
            # ArchLens configuration generated from preset '{canonical}'
            version: 1
            preset: {canonical}
            {nsLine}

            # You can append project-specific rules below; they are merged with the preset rules.
            rules: []
            """;
    }

    private static IReadOnlyList<IArchitectureRule> CreateCleanArchitectureRules(string? root) =>
    [
        new ForbidDependencyRule(
            "CLEAN-001", "Domain must not depend on outer layers",
            "Domain layer must remain pure with zero references to Application, Infrastructure, Persistence, Web, or Api.",
            RuleSeverity.Error,
            LayerPatterns(root, "Domain"),
            LayerPatterns(root, "Application", "Infrastructure", "Persistence", "Presentation", "Web", "Api")),

        new ForbidDependencyRule(
            "CLEAN-002", "Application must not depend on Infrastructure or Presentation",
            "Application use cases should depend only on Domain abstractions, never concrete Infrastructure or Web/UI layers.",
            RuleSeverity.Error,
            LayerPatterns(root, "Application"),
            LayerPatterns(root, "Infrastructure", "Persistence", "Presentation", "Web", "Api")),

        new NoCyclesRule("CLEAN-003", "No project dependency cycles", "Projects must form a DAG.", RuleSeverity.Error, NodeKind.Project),
        new NoCyclesRule("CLEAN-004", "No namespace dependency cycles", "Namespaces must form a DAG.", RuleSeverity.Error, NodeKind.Namespace),
        new NoCaptiveDependenciesRule("CLEAN-005", "No captive DI dependencies", "Singletons must not capture Scoped dependencies.", RuleSeverity.Error),
        new MaxMetricRule("CLEAN-006", "Keep cognitive complexity readable", "Methods should keep cognitive complexity <= 15.", RuleSeverity.Warning, "cognitive_complexity", 15, GlobalTargetPattern(root)),
        new MaxMetricRule("CLEAN-007", "Prevent low-cohesion god classes", "Classes should keep LCOM4 <= 2.", RuleSeverity.Warning, "lcom4", 2, GlobalTargetPattern(root))
    ];

    private static IReadOnlyList<IArchitectureRule> CreateVerticalSliceRules(string? root) =>
    [
        new SliceIsolationRule(
            "VSA-001", "Vertical slices must remain isolated",
            "Types in *.Features.<SliceA>.* must not directly depend on *.Features.<SliceB>.* internals (only .Contracts/.Shared/.Public).",
            RuleSeverity.Error,
            segmentMarker: "Features",
            allowedPublicSubNamespaces: ["Contracts", "Public", "Shared", "Events", "Abstractions"],
            rootNamespace: root),

        new ForbidDependencyRule(
            "VSA-002", "Shared/Common kernel must not depend on Features",
            "Shared kernel code cannot reference individual feature slices.",
            RuleSeverity.Error,
            LayerPatterns(root, "Shared"),
            LayerPatterns(root, "Features")),

        new ForbidDependencyRule(
            "VSA-003", "Common kernel must not depend on Features",
            "Common infrastructure cannot reference individual feature slices.",
            RuleSeverity.Error,
            LayerPatterns(root, "Common"),
            LayerPatterns(root, "Features")),

        new NoCyclesRule("VSA-004", "No namespace dependency cycles", "Namespaces must form a DAG.", RuleSeverity.Error, NodeKind.Namespace),
        new NoCaptiveDependenciesRule("VSA-005", "No captive DI dependencies", "Singletons must not capture Scoped dependencies.", RuleSeverity.Error),
        new MaxMetricRule("VSA-006", "Keep feature handlers concise", "Methods should keep cognitive complexity <= 15.", RuleSeverity.Warning, "cognitive_complexity", 15, GlobalTargetPattern(root))
    ];

    private static IReadOnlyList<IArchitectureRule> CreateModularMonolithRules(string? root) =>
    [
        new SliceIsolationRule(
            "MOD-001", "Modules may only depend on other modules' public contracts",
            "Types in *.Modules.<ModuleA>.* may only reference *.Modules.<ModuleB>.Contracts/.Public/.IntegrationEvents/.Abstractions.",
            RuleSeverity.Error,
            segmentMarker: "Modules",
            allowedPublicSubNamespaces: ["Contracts", "Public", "IntegrationEvents", "Abstractions"],
            rootNamespace: root),

        new ForbidDependencyRule(
            "MOD-002", "Shared/BuildingBlocks must not depend on Modules",
            "Shared kernel / BuildingBlocks must remain independent of business modules.",
            RuleSeverity.Error,
            LayerPatterns(root, "BuildingBlocks"),
            LayerPatterns(root, "Modules")),

        new ForbidDependencyRule(
            "MOD-003", "Shared kernel must not depend on Modules",
            "Shared kernel must remain independent of business modules.",
            RuleSeverity.Error,
            LayerPatterns(root, "Shared"),
            LayerPatterns(root, "Modules")),

        new NoCyclesRule("MOD-004", "No project dependency cycles", "Module projects must form a DAG.", RuleSeverity.Error, NodeKind.Project),
        new NoCyclesRule("MOD-005", "No namespace dependency cycles", "Namespaces must form a DAG.", RuleSeverity.Error, NodeKind.Namespace),
        new NoCaptiveDependenciesRule("MOD-006", "No captive DI dependencies", "Singletons must not capture Scoped dependencies.", RuleSeverity.Error)
    ];

    private static IReadOnlyList<IArchitectureRule> CreateDddRules(string? root) =>
    [
        new ForbidDependencyRule(
            "DDD-001", "Domain model must not depend on Infrastructure, Persistence, or Application",
            "Entities, Value Objects, Aggregates, and Domain Services must remain persistence- and transport-ignorant.",
            RuleSeverity.Error,
            LayerPatterns(root, "Domain"),
            LayerPatterns(root, "Application", "Infrastructure", "Persistence", "Web", "Api")),

        new NamingConventionRule(
            "DDD-002", "Domain repository interfaces must start with I",
            "Interfaces must follow standard I* naming.",
            RuleSeverity.Warning,
            LayerPatterns(root, "Domain"), "I*", TypeKind.Interface),

        new NoCyclesRule("DDD-003", "No namespace dependency cycles", "Bounded context namespaces must form a DAG.", RuleSeverity.Error, NodeKind.Namespace),
        new NoCaptiveDependenciesRule("DDD-004", "No captive DI dependencies", "Singletons must not capture Scoped dependencies.", RuleSeverity.Error),
        new MaxMetricRule("DDD-005", "Keep aggregates cohesive (LCOM4)", "Domain types should maintain high cohesion (LCOM4 <= 2).", RuleSeverity.Warning, "lcom4", 2, LayerPatterns(root, "Domain")),
        new MaxMetricRule("DDD-006", "Limit constructor parameter bloat", "Types should keep constructor parameters <= 6.", RuleSeverity.Warning, "constructor_params", 6, GlobalTargetPattern(root))
    ];

    private static string? NormalizeRootNamespace(string? rootNamespace)
    {
        if (string.IsNullOrWhiteSpace(rootNamespace))
        {
            return null;
        }

        var trimmed = rootNamespace.Trim().Trim('.', '*');
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string GlobalTargetPattern(string? root) =>
        root is null ? "*" : $"{root}.*";

    private static IReadOnlyList<string> LayerPatterns(string? root, params string[] layers)
    {
        var patterns = new List<string>(root is null ? layers.Length * 2 : layers.Length);
        foreach (var layer in layers)
        {
            if (root is null)
            {
                // Match both top-level "Domain.Order" and prefixed "MyApp.Domain.Order"
                patterns.Add($"{layer}.*");
                patterns.Add($"*.{layer}.*");
            }
            else
            {
                patterns.Add($"{root}.{layer}.*");
            }
        }
        return patterns;
    }
}

/// <summary>
/// Enforces boundary isolation between peer modules/slices (e.g. *.Modules.&lt;Name&gt;.* or *.Features.&lt;Name&gt;.*),
/// allowing cross-module references only into explicitly permitted contract sub-namespaces.
/// </summary>
public sealed class SliceIsolationRule(
    string id,
    string name,
    string description,
    RuleSeverity severity,
    string segmentMarker,
    IReadOnlyList<string> allowedPublicSubNamespaces,
    string? rootNamespace = null) : IArchitectureRule
{
    private readonly string _marker = string.IsNullOrWhiteSpace(segmentMarker) ? "Modules" : segmentMarker.Trim();
    private readonly string? _normalizedRoot = string.IsNullOrWhiteSpace(rootNamespace)
        ? null
        : rootNamespace.Trim().Trim('.', '*');

    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public RuleSeverity Severity { get; } = severity;

    public IEnumerable<RuleViolation> Evaluate(CodeGraph graph, IReadOnlyDictionary<string, NodeMetrics> metrics)
    {
        var seenPairs = new HashSet<(string From, string To)>();

        foreach (var edge in graph.Edges)
        {
            if (edge.Kind == DependencyKind.ProjectReference) continue;

            var from = graph.FindNode(edge.FromId);
            var to = graph.FindNode(edge.ToId);
            if (from is not { Kind: NodeKind.Type } || to is not { Kind: NodeKind.Type }) continue;

            if (!MatchesRoot(from.Namespace) || !MatchesRoot(to.Namespace)) continue;

            var fromSlicePath = ExtractSlice(from.Namespace, _marker, out _, out _);
            var toSlicePath = ExtractSlice(to.Namespace, _marker, out var toSliceName, out var toSubSegment);
            if (fromSlicePath is null || toSlicePath is null) continue;
            if (string.Equals(fromSlicePath, toSlicePath, StringComparison.OrdinalIgnoreCase)) continue;

            // Allow references into shared/contract peer slices directly under the marker (e.g. *.Modules.Contracts.* or *.Features.Shared.*)
            if (toSliceName is not null &&
                allowedPublicSubNamespaces.Any(a => a.Equals(toSliceName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // Allow references into explicitly public contract sub-namespaces (e.g. *.Modules.Billing.Contracts.*)
            if (toSubSegment is not null &&
                allowedPublicSubNamespaces.Any(a => a.Equals(toSubSegment, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!seenPairs.Add((from.Id, to.Id))) continue;

            yield return new RuleViolation(
                RuleId: Id,
                RuleName: Name,
                Severity: Severity,
                Message: $"Cross-{_marker.ToLowerInvariant()} boundary violation: '{from.FullName}' ({fromSlicePath}) depends on internal type '{to.FullName}' ({toSlicePath}). Only [{string.Join(", ", allowedPublicSubNamespaces)}] sub-namespaces are allowed across boundaries.",
                NodeId: from.Id,
                TargetNodeId: to.Id,
                Location: edge.Location ?? from.Location);
        }
    }

    private bool MatchesRoot(string? ns)
    {
        if (string.IsNullOrWhiteSpace(_normalizedRoot))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(ns))
        {
            return false;
        }

        return ns.Equals(_normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               ns.StartsWith(_normalizedRoot + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractSlice(string? ns, string marker, out string? sliceName, out string? subSegment)
    {
        sliceName = null;
        subSegment = null;
        if (string.IsNullOrWhiteSpace(ns)) return null;

        var segments = ns.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals(marker, StringComparison.OrdinalIgnoreCase))
            {
                sliceName = segments[i + 1];
                subSegment = i + 2 < segments.Length ? segments[i + 2] : null;
                // Key by full prefix + marker + sliceName so nested slices under different parent modules stay distinct
                return string.Join('.', segments[..(i + 2)]);
            }
        }

        return null;
    }
}
