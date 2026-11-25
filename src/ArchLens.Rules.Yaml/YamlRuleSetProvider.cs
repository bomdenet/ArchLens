using ArchLens.Analysis;
using ArchLens.Model;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ArchLens.Rules.Yaml;

/// <summary>
/// Loads declarative architecture rules from .archlens.yaml / .archlens.yml files.
/// </summary>
public sealed class YamlRuleSetProvider : IRuleSetProvider
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public bool CanLoad(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        var ext = Path.GetExtension(path);
        return ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".yml", StringComparison.OrdinalIgnoreCase);
    }

    public static string? DiscoverConfigFile(string targetPath)
    {
        var dir = Directory.Exists(targetPath)
            ? Path.GetFullPath(targetPath)
            : Path.GetDirectoryName(Path.GetFullPath(targetPath));

        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        string[] candidates = [".archlens.yaml", ".archlens.yml", "archlens.yaml", "archlens.yml"];
        foreach (var candidate in candidates)
        {
            var full = Path.Combine(dir, candidate);
            if (File.Exists(full))
            {
                return full;
            }
        }

        return null;
    }

    public IReadOnlyList<IArchitectureRule> LoadRules(string path)
    {
        var yaml = File.ReadAllText(path);
        try
        {
            return ParseYaml(yaml);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new InvalidDataException(
                $"Invalid YAML in rules file '{path}' at line {ex.Start.Line}, column {ex.Start.Column}: {ex.Message}", ex);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidDataException($"Invalid rule definition in '{path}': {ex.Message}", ex);
        }
    }

    public IReadOnlyList<DeadCodeAcknowledgement> LoadDeadCodeAcknowledgements(string path)
    {
        if (!CanLoad(path))
        {
            return [];
        }

        try
        {
            var config = Deserializer.Deserialize<ArchLensYamlConfig>(File.ReadAllText(path));
            return ParseAcknowledgements(config?.Acknowledge);
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new InvalidDataException(
                $"Invalid YAML in rules file '{path}' at line {ex.Start.Line}, column {ex.Start.Column}: {ex.Message}", ex);
        }
    }

    public IReadOnlyList<IArchitectureRule> ParseYaml(string yamlContent)
    {
        ArchLensYamlConfig config;
        try
        {
            config = Deserializer.Deserialize<ArchLensYamlConfig>(yamlContent)
                ?? new ArchLensYamlConfig();
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new InvalidDataException(
                $"Invalid YAML at line {ex.Start.Line}, column {ex.Start.Column}: {ex.Message}", ex);
        }

        var rules = new List<IArchitectureRule>();
        var layerMapping = NormalizeLayerMapping(config.Layers);
        var globalAcks = ParseAcknowledgements(config.Acknowledge);

        var presetNames = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.Preset))
        {
            presetNames.Add(config.Preset);
        }
        if (config.Presets is { Count: > 0 })
        {
            presetNames.AddRange(config.Presets.Where(p => !string.IsNullOrWhiteSpace(p)));
        }

        var canonicalPresets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var presetName in presetNames)
        {
            var resolved = ArchitecturePresets.TryResolvePresetId(presetName, out var canonical)
                ? canonical
                : presetName;
            if (canonicalPresets.Add(resolved))
            {
                rules.AddRange(ArchitecturePresets.CreateRules(presetName, config.RootNamespace, layerMapping));
            }
        }

        var index = 1;

        foreach (var entry in config.Rules ?? [])
        {
            var id = string.IsNullOrWhiteSpace(entry.Id) ? $"RULE-{index:D3}" : entry.Id;
            var name = string.IsNullOrWhiteSpace(entry.Name) ? id : entry.Name;
            var description = entry.Description ?? string.Empty;
            var severity = ParseSeverity(entry.Severity);
            var type = (entry.Type ?? string.Empty).Trim().ToLowerInvariant();
            var includeEfAndGenerated = entry.IncludeEfAndGenerated
                ?? entry.IncludeGeneratedOrEf
                ?? entry.IncludeInfrastructure
                ?? false;

            IArchitectureRule rule = type switch
            {
                "forbid_dependency" => new ForbidDependencyRule(
                    id, name, description, severity,
                    entry.From ?? "*",
                    entry.To ?? [],
                    entry.ExcludeFrom),

                "allow_only_dependencies" => new AllowOnlyDependenciesRule(
                    id, name, description, severity,
                    entry.From ?? "*",
                    entry.Allow ?? entry.To ?? []),

                "no_cycles" => new NoCyclesRule(
                    id, name, description, severity,
                    ParseScope(entry.Scope),
                    entry.Target,
                    excludeMigrations: !(entry.IncludeMigrations ?? false),
                    crossProjectOnly: entry.CrossProjectOnly ?? false,
                    excludeParentChild: !(entry.IncludeParentChild ?? false)),

                "max_metric" => new MaxMetricRule(
                    id, name, description, severity,
                    entry.Metric ?? "max_method_cyclomatic_complexity",
                    entry.Threshold ?? 15,
                    entry.Target,
                    string.IsNullOrWhiteSpace(entry.Scope) ? NodeKind.Type : ParseScope(entry.Scope),
                    excludeEfAndGenerated: !includeEfAndGenerated,
                    excludePatterns: MergeExcludes(config.Exclude, entry.Exclude)),

                "god_class" => new GodClassRule(
                    id, name, description, severity,
                    maxLines: entry.MaxLines ?? 500,
                    maxComplexity: entry.MaxComplexity ?? 50,
                    maxCoupling: entry.MaxCoupling ?? 20,
                    minConditions: entry.MinConditions ?? 3,
                    targetPatterns: string.IsNullOrWhiteSpace(entry.Target) ? null : [entry.Target],
                    excludePatterns: MergeExcludes(config.Exclude, entry.Exclude),
                    excludeEfAndGenerated: !includeEfAndGenerated),

                "naming_convention" => new NamingConventionRule(
                    id, name, description, severity,
                    entry.Target ?? "*",
                    entry.Pattern ?? "*",
                    ParseTypeKind(entry.TypeKind)),

                "no_captive_dependencies" => new NoCaptiveDependenciesRule(
                    id, name, description, severity),

                "no_dead_code" => new NoDeadCodeRule(
                    id, name, description, severity,
                    ParseScope(entry.Scope ?? "type"),
                    entry.TreatPublicApiAsEntryPoint ?? true,
                    entry.Target,
                    entry.Profile,
                    MergeAcknowledgements(globalAcks, ParseAcknowledgements(entry.Acknowledge))),

                "slice_isolation" => new SliceIsolationRule(
                    id, name, description, severity,
                    string.IsNullOrWhiteSpace(entry.Segment) ? "Modules" : entry.Segment.Trim(),
                    entry.Allow ?? ["Contracts", "Public", "IntegrationEvents", "Abstractions"],
                    config.RootNamespace),

                _ => throw new InvalidOperationException(
                    $"Unsupported rule type '{entry.Type}' in rule '{id}'. Supported types: forbid_dependency, allow_only_dependencies, no_cycles, max_metric, god_class, naming_convention, no_captive_dependencies, no_dead_code, slice_isolation.")
            };

            rules.Add(rule);
            index++;
        }

        return rules;
    }

    private static RuleSeverity ParseSeverity(string? raw) => (raw ?? "error").Trim().ToLowerInvariant() switch
    {
        "info" => RuleSeverity.Info,
        "warning" or "warn" => RuleSeverity.Warning,
        _ => RuleSeverity.Error
    };

    private static NodeKind ParseScope(string? raw) => (raw ?? "namespace").Trim().ToLowerInvariant() switch
    {
        "project" => NodeKind.Project,
        "type" => NodeKind.Type,
        "method" => NodeKind.Method,
        _ => NodeKind.Namespace
    };

    private static TypeKind? ParseTypeKind(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return Enum.TryParse<TypeKind>(raw, ignoreCase: true, out var kind) ? kind : null;
    }

    private static List<string>? MergeExcludes(List<string>? globalExcludes, List<string>? ruleExcludes)
    {
        if ((globalExcludes is null || globalExcludes.Count == 0) &&
            (ruleExcludes is null || ruleExcludes.Count == 0))
        {
            return null;
        }

        var merged = new List<string>();
        if (globalExcludes is not null) merged.AddRange(globalExcludes);
        if (ruleExcludes is not null) merged.AddRange(ruleExcludes);
        return merged;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>>? NormalizeLayerMapping(
        Dictionary<string, List<string>>? layers)
    {
        if (layers is null || layers.Count == 0)
        {
            return null;
        }

        var map = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, values) in layers)
        {
            if (string.IsNullOrWhiteSpace(key) || values is null)
            {
                continue;
            }

            var cleaned = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
            if (cleaned.Count > 0)
            {
                map[key.Trim()] = cleaned;
            }
        }

        return map.Count > 0 ? map : null;
    }

    private static IReadOnlyList<DeadCodeAcknowledgement> ParseAcknowledgements(List<AcknowledgeEntry>? entries)
    {
        if (entries is null || entries.Count == 0)
        {
            return [];
        }

        return entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Symbol))
            .Select(e => new DeadCodeAcknowledgement(e.Symbol!.Trim(), e.Reason?.Trim() ?? string.Empty))
            .ToList();
    }

    private static IReadOnlyList<DeadCodeAcknowledgement>? MergeAcknowledgements(
        IReadOnlyList<DeadCodeAcknowledgement> globalAcks,
        IReadOnlyList<DeadCodeAcknowledgement> ruleAcks)
    {
        if (globalAcks.Count == 0 && ruleAcks.Count == 0)
        {
            return null;
        }

        var merged = new List<DeadCodeAcknowledgement>(globalAcks.Count + ruleAcks.Count);
        merged.AddRange(globalAcks);
        merged.AddRange(ruleAcks);
        return merged;
    }

    private sealed class ArchLensYamlConfig
    {
        public int Version { get; set; } = 1;
        public string? Preset { get; set; }
        public List<string>? Presets { get; set; }
        public string? RootNamespace { get; set; }
        public Dictionary<string, List<string>>? Layers { get; set; }
        public List<string>? Exclude { get; set; }
        public List<AcknowledgeEntry>? Acknowledge { get; set; }
        public List<RuleEntry>? Rules { get; set; }
    }

    private sealed class AcknowledgeEntry
    {
        public string? Symbol { get; set; }
        public string? Reason { get; set; }
    }

    private sealed class RuleEntry
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? Severity { get; set; }
        public string? Type { get; set; }
        public string? From { get; set; }
        public List<string>? To { get; set; }
        public List<string>? Allow { get; set; }
        public List<string>? ExcludeFrom { get; set; }
        public List<string>? Exclude { get; set; }
        public string? Scope { get; set; }
        public string? Metric { get; set; }
        public double? Threshold { get; set; }
        public int? MaxLines { get; set; }
        public int? MaxComplexity { get; set; }
        public int? MaxCoupling { get; set; }
        public int? MinConditions { get; set; }
        public string? Target { get; set; }
        public string? Pattern { get; set; }
        public string? TypeKind { get; set; }
        public bool? TreatPublicApiAsEntryPoint { get; set; }
        public string? Profile { get; set; }
        public string? Segment { get; set; }
        public bool? CrossProjectOnly { get; set; }
        public bool? IncludeMigrations { get; set; }
        public bool? IncludeParentChild { get; set; }
        public bool? IncludeEfAndGenerated { get; set; }
        public bool? IncludeGeneratedOrEf { get; set; }
        public bool? IncludeInfrastructure { get; set; }
        public List<AcknowledgeEntry>? Acknowledge { get; set; }
    }
}
