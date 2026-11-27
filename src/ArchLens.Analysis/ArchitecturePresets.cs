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

    public static List<IArchitectureRule> MergeRulesById(
        IEnumerable<IArchitectureRule> baseRules,
        IEnumerable<IArchitectureRule> overrideRules)
    {
        var merged = new List<IArchitectureRule>();
        var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Upsert(IArchitectureRule rule)
        {
            if (indexById.TryGetValue(rule.Id, out var existingIdx))
            {
                merged[existingIdx] = rule;
            }
            else
            {
                indexById[rule.Id] = merged.Count;
                merged.Add(rule);
            }
        }

        foreach (var r in baseRules) Upsert(r);
        foreach (var r in overrideRules) Upsert(r);
        return merged;
    }

    public static IReadOnlyList<IArchitectureRule> CreateRules(
        string presetName,
        string? rootNamespace = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? layerMapping = null,
        IReadOnlyList<string>? excludePatterns = null)
    {
        if (!TryResolvePresetId(presetName, out var canonical))
        {
            throw new ArgumentException(
                $"Unknown architectural preset '{presetName}'. Available presets: {string.Join(", ", All.Select(p => p.Id))}.");
        }

        var normalizedRoot = NormalizeRootNamespace(rootNamespace);

        return canonical switch
        {
            "clean-architecture" => CreateCleanArchitectureRules(normalizedRoot, layerMapping, excludePatterns),
            "vertical-slice" => CreateVerticalSliceRules(normalizedRoot, layerMapping, excludePatterns),
            "modular-monolith" => CreateModularMonolithRules(normalizedRoot, layerMapping, excludePatterns),
            "ddd" => CreateDddRules(normalizedRoot, layerMapping, excludePatterns),
            _ => throw new ArgumentException($"Unsupported preset '{presetName}'.", nameof(presetName))
        };
    }

    public static string GenerateYamlTemplate(string presetName, string? rootNamespace = null)
    {
        if (!TryResolvePresetId(presetName, out var canonical))
        {
            throw new ArgumentException(
                $"Unknown architectural preset '{presetName}'. Available presets: {string.Join(", ", All.Select(p => p.Id))}.");
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

            # Optional custom layer mapping when namespaces do not follow canonical layer names:
            # layers:
            #   domain: ["MyApp.Core.*"]
            #   application: ["MyApp.UseCases.*"]
            #   infrastructure: ["MyApp.Data.*"]
            #   presentation: ["MyApp.Api.*"]

            # You can append project-specific rules below; they are merged with the preset rules.
            rules: []
            """;
    }

    private static IReadOnlyList<IArchitectureRule> CreateCleanArchitectureRules(
        string? root,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? layerMapping,
        IReadOnlyList<string>? excludePatterns) =>
    [
        new ForbidDependencyRule(
            "CLEAN-001", "Domain must not depend on outer layers",
            "Domain layer must remain pure with zero references to Application, Infrastructure, Persistence, Web, or Api.",
            RuleSeverity.Error,
            LayerPatterns(root, layerMapping, "Domain"),
            LayerPatterns(root, layerMapping, "Application", "Infrastructure", "Persistence", "Presentation", "Web", "Api"),
            excludeFromPatterns: excludePatterns),

        new ForbidDependencyRule(
            "CLEAN-002", "Application must not depend on Infrastructure or Presentation",
            "Application use cases should depend only on Domain abstractions, never concrete Infrastructure or Web/UI layers.",
            RuleSeverity.Error,
            LayerPatterns(root, layerMapping, "Application"),
            LayerPatterns(root, layerMapping, "Infrastructure", "Persistence", "Presentation", "Web", "Api"),
            excludeFromPatterns: excludePatterns),

        new NoCyclesRule("CLEAN-003", "No project dependency cycles", "Projects must form a DAG.", RuleSeverity.Error, NodeKind.Project),
        new NoCyclesRule("CLEAN-004", "No cross-project namespace dependency cycles", "Cross-project namespaces must form a DAG (excludes EF migrations and parent-child hierarchies).", RuleSeverity.Warning, NodeKind.Namespace, GlobalTargetPattern(root), excludeMigrations: true, crossProjectOnly: true),
        new NoCaptiveDependenciesRule("CLEAN-005", "No captive DI dependencies", "Singletons must not capture Scoped dependencies.", RuleSeverity.Error),
        new MaxMetricRule("CLEAN-006", "Keep cognitive complexity readable", "Methods should keep cognitive complexity <= 15.", RuleSeverity.Warning, "max_method_cognitive_complexity", 15, GlobalTargetPattern(root), excludePatterns: excludePatterns),
        new MaxMetricRule("CLEAN-007", "Prevent low-cohesion classes", "Classes should keep LCOM4 <= 2 (excludes EF DbContext/Migrations).", RuleSeverity.Warning, "lcom4", 2, GlobalTargetPattern(root), excludePatterns: excludePatterns)
    ];

    private static IReadOnlyList<IArchitectureRule> CreateVerticalSliceRules(
        string? root,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? layerMapping,
        IReadOnlyList<string>? excludePatterns) =>
    [
        new SliceIsolationRule(
            "VSA-001", "Vertical slices must remain isolated",
            "Types in *.Features.<SliceA>.* must not directly depend on *.Features.<SliceB>.* internals (only .Contracts/.Shared/.Public).",
            RuleSeverity.Error,
            segmentMarker: "Features",
            allowedPublicSubNamespaces: ["Contracts", "Public", "Shared", "Events", "Abstractions"],
            rootNamespace: root,
            excludePatterns: excludePatterns),

        new ForbidDependencyRule(
            "VSA-002", "Shared/Common kernel must not depend on Features",
            "Shared kernel code cannot reference individual feature slices.",
            RuleSeverity.Error,
            LayerPatterns(root, layerMapping, "Shared"),
            LayerPatterns(root, layerMapping, "Features"),
            excludeFromPatterns: excludePatterns),

        new ForbidDependencyRule(
            "VSA-003", "Common kernel must not depend on Features",
            "Common infrastructure cannot reference individual feature slices.",
            RuleSeverity.Error,
            LayerPatterns(root, layerMapping, "Common"),
            LayerPatterns(root, layerMapping, "Features"),
            excludeFromPatterns: excludePatterns),

        new NoCyclesRule("VSA-004", "No namespace dependency cycles", "Namespaces must form a DAG.", RuleSeverity.Warning, NodeKind.Namespace, GlobalTargetPattern(root), excludeMigrations: true),
        new NoCaptiveDependenciesRule("VSA-005", "No captive DI dependencies", "Singletons must not capture Scoped dependencies.", RuleSeverity.Error),
        new MaxMetricRule("VSA-006", "Keep feature handlers concise", "Methods should keep cognitive complexity <= 15.", RuleSeverity.Warning, "max_method_cognitive_complexity", 15, GlobalTargetPattern(root), excludePatterns: excludePatterns)
    ];

    private static IReadOnlyList<IArchitectureRule> CreateModularMonolithRules(
        string? root,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? layerMapping,
        IReadOnlyList<string>? excludePatterns) =>
    [
        new SliceIsolationRule(
            "MOD-001", "Modules may only depend on other modules' public contracts",
            "Types in *.Modules.<ModuleA>.* may only reference *.Modules.<ModuleB>.Contracts/.Public/.IntegrationEvents/.Abstractions.",
            RuleSeverity.Error,
            segmentMarker: "Modules",
            allowedPublicSubNamespaces: ["Contracts", "Public", "IntegrationEvents", "Abstractions"],
            rootNamespace: root,
            excludePatterns: excludePatterns),

        new ForbidDependencyRule(
            "MOD-002", "Shared/BuildingBlocks must not depend on Modules",
            "Shared kernel / BuildingBlocks must remain independent of business modules.",
            RuleSeverity.Error,
            LayerPatterns(root, layerMapping, "BuildingBlocks"),
            LayerPatterns(root, layerMapping, "Modules"),
            excludeFromPatterns: excludePatterns),

        new ForbidDependencyRule(
            "MOD-003", "Shared kernel must not depend on Modules",
            "Shared kernel must remain independent of business modules.",
            RuleSeverity.Error,
            LayerPatterns(root, layerMapping, "Shared"),
            LayerPatterns(root, layerMapping, "Modules"),
            excludeFromPatterns: excludePatterns),

        new NoCyclesRule("MOD-004", "No project dependency cycles", "Module projects must form a DAG.", RuleSeverity.Error, NodeKind.Project),
        new NoCyclesRule("MOD-005", "No namespace dependency cycles", "Namespaces must form a DAG.", RuleSeverity.Warning, NodeKind.Namespace, GlobalTargetPattern(root), excludeMigrations: true),
        new NoCaptiveDependenciesRule("MOD-006", "No captive DI dependencies", "Singletons must not capture Scoped dependencies.", RuleSeverity.Error)
    ];

    private static IReadOnlyList<IArchitectureRule> CreateDddRules(
        string? root,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? layerMapping,
        IReadOnlyList<string>? excludePatterns) =>
    [
        new ForbidDependencyRule(
            "DDD-001", "Domain model must not depend on Infrastructure, Persistence, or Application",
            "Entities, Value Objects, Aggregates, and Domain Services must remain persistence- and transport-ignorant.",
            RuleSeverity.Error,
            LayerPatterns(root, layerMapping, "Domain"),
            LayerPatterns(root, layerMapping, "Application", "Infrastructure", "Persistence", "Web", "Api"),
            excludeFromPatterns: excludePatterns),

        new NamingConventionRule(
            "DDD-002", "Domain repository interfaces must start with I",
            "Interfaces must follow standard I* naming.",
            RuleSeverity.Warning,
            LayerPatterns(root, layerMapping, "Domain"), "I*", TypeKind.Interface,
            excludePatterns: excludePatterns),

        new NoCyclesRule("DDD-003", "No namespace dependency cycles", "Bounded context namespaces must form a DAG.", RuleSeverity.Warning, NodeKind.Namespace, GlobalTargetPattern(root), excludeMigrations: true),
        new NoCaptiveDependenciesRule("DDD-004", "No captive DI dependencies", "Singletons must not capture Scoped dependencies.", RuleSeverity.Error),
        new MaxMetricRule("DDD-005", "Keep aggregates cohesive (LCOM4)", "Domain types should maintain high cohesion (LCOM4 <= 2).", RuleSeverity.Warning, "lcom4", 2, LayerPatterns(root, layerMapping, "Domain"), excludePatterns: excludePatterns),
        new MaxMetricRule("DDD-006", "Limit constructor parameter bloat", "Types should keep constructor parameters <= 6.", RuleSeverity.Warning, "constructor_params", 6, GlobalTargetPattern(root), excludePatterns: excludePatterns)
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

    private static IReadOnlyList<string> LayerPatterns(
        string? root,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? layerMapping,
        params string[] layers)
    {
        var patterns = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var layer in layers)
        {
            if (TryGetMappedLayerPatterns(layerMapping, layer, out var mapped) && mapped.Count > 0)
            {
                foreach (var p in mapped)
                {
                    if (!string.IsNullOrWhiteSpace(p) && seen.Add(p)) patterns.Add(p);
                }
                continue;
            }

            if (root is null)
            {
                if (seen.Add($"{layer}.*")) patterns.Add($"{layer}.*");
                if (seen.Add($"*.{layer}.*")) patterns.Add($"*.{layer}.*");
            }
            else
            {
                if (seen.Add($"{root}.{layer}.*")) patterns.Add($"{root}.{layer}.*");
            }
        }
        return patterns;
    }

    private static bool TryGetMappedLayerPatterns(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? layerMapping,
        string layer,
        out IReadOnlyList<string> patterns)
    {
        if (layerMapping is null || layerMapping.Count == 0)
        {
            patterns = [];
            return false;
        }

        foreach (var (key, val) in layerMapping)
        {
            if (key.Equals(layer, StringComparison.OrdinalIgnoreCase) ||
                key.Replace("_", "", StringComparison.Ordinal).Equals(layer, StringComparison.OrdinalIgnoreCase))
            {
                patterns = val;
                return true;
            }
        }

        patterns = [];
        return false;
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
    string? rootNamespace = null,
    IReadOnlyList<string>? excludePatterns = null) : IArchitectureRule
{
    private readonly string _marker = string.IsNullOrWhiteSpace(segmentMarker) ? "Modules" : segmentMarker.Trim();
    private readonly string? _normalizedRoot = string.IsNullOrWhiteSpace(rootNamespace)
        ? null
        : rootNamespace.Trim().Trim('.', '*');
    private readonly IReadOnlyList<string>? _excludePatterns = excludePatterns;

    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Description { get; } = description;
    public RuleSeverity Severity { get; } = severity;

    internal bool HasUnmatchedSegment(CodeGraph graph) =>
        !graph.GetNodes(NodeKind.Type).Any(t =>
            MatchesRoot(t.Namespace) &&
            ExtractSlice(t.Namespace, _marker, out _, out _) is not null);

    public IEnumerable<RuleViolation> Evaluate(CodeGraph graph, IReadOnlyDictionary<string, NodeMetrics> metrics)
    {
        var seenPairs = new HashSet<(string From, string To)>();

        foreach (var edge in graph.Edges)
        {
            if (edge.Kind == DependencyKind.ProjectReference) continue;

            var from = graph.FindNode(edge.FromId);
            var to = graph.FindNode(edge.ToId);
            if (from is not { Kind: NodeKind.Type } || to is not { Kind: NodeKind.Type }) continue;
            if (_excludePatterns is { Count: > 0 } && GlobMatcher.MatchesAny(from.FullName, _excludePatterns)) continue;

            if (!MatchesRoot(from.Namespace) || !MatchesRoot(to.Namespace)) continue;

            var fromSlicePath = ExtractSlice(from.Namespace, _marker, out _, out _);
            var toSlicePath = ExtractSlice(to.Namespace, _marker, out var toSliceName, out var toSubSegment);
            if (fromSlicePath is null || toSlicePath is null) continue;
            if (string.Equals(fromSlicePath, toSlicePath, StringComparison.OrdinalIgnoreCase)) continue;

            // Allow references into shared/contract peer slices directly under the marker (e.g. *.Modules.Contracts.* or *.Features.Shared.*)
            if (toSliceName is not null &&
                allowedPublicSubNamespaces.Any(a => !string.IsNullOrWhiteSpace(a) && a.Equals(toSliceName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // Allow references into explicitly public contract sub-namespaces (e.g. *.Modules.Billing.Contracts.*)
            if (toSubSegment is not null &&
                allowedPublicSubNamespaces.Any(a => !string.IsNullOrWhiteSpace(a) && a.Equals(toSubSegment, StringComparison.OrdinalIgnoreCase)))
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
