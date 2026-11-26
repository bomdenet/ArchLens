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

        string[] candidates = [".archlens.yaml", ".archlens.yml", "archlens.yaml", "archlens.yml"];
        while (!string.IsNullOrWhiteSpace(dir))
        {
            foreach (var candidate in candidates)
            {
                var full = Path.Combine(dir, candidate);
                if (File.Exists(full))
                {
                    return full;
                }
            }

            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
            {
                break;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        return null;
    }

    public IReadOnlyList<IArchitectureRule> LoadRules(string path) =>
        LoadRules(path, rootNamespaceOverride: null);

    public IReadOnlyList<IArchitectureRule> LoadRules(string path, string? rootNamespaceOverride)
    {
        var yaml = File.ReadAllText(path);
        try
        {
            return ParseYamlCore(yaml, path, rootNamespaceOverride);
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidDataException($"Invalid rule definition in '{path}': {ex.Message}", ex);
        }
    }

    public string? LoadRootNamespace(string path)
    {
        if (!CanLoad(path))
        {
            return null;
        }

        try
        {
            var config = Deserializer.Deserialize<ArchLensYamlConfig>(File.ReadAllText(path));
            return string.IsNullOrWhiteSpace(config?.RootNamespace) ? null : config.RootNamespace.Trim();
        }
        catch
        {
            return null;
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
            throw new InvalidDataException(FormatYamlError(ex, path), ex);
        }
    }

    public IReadOnlyList<IArchitectureRule> ParseYaml(string yamlContent, string? rootNamespaceOverride = null) =>
        ParseYamlCore(yamlContent, path: null, rootNamespaceOverride);

    private IReadOnlyList<IArchitectureRule> ParseYamlCore(string yamlContent, string? path, string? rootNamespaceOverride = null)
    {
        ArchLensYamlConfig config;
        try
        {
            config = Deserializer.Deserialize<ArchLensYamlConfig>(yamlContent)
                ?? new ArchLensYamlConfig();
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new InvalidDataException(FormatYamlError(ex, path), ex);
        }

        var effectiveRootNs = !string.IsNullOrWhiteSpace(rootNamespaceOverride)
            ? rootNamespaceOverride.Trim()
            : config.RootNamespace;

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
                rules.AddRange(ArchitecturePresets.CreateRules(presetName, effectiveRootNs, layerMapping));
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
                "forbid_dependency" => BuildForbidDependencyRule(id, name, description, severity, entry, config.Exclude),

                "allow_only_dependencies" => BuildAllowOnlyDependenciesRule(id, name, description, severity, entry),

                "no_cycles" => BuildNoCyclesRule(id, name, description, severity, entry),

                "max_metric" => BuildMaxMetricRule(id, name, description, severity, entry, config.Exclude, includeEfAndGenerated),

                "god_class" => BuildGodClassRule(id, name, description, severity, entry, config.Exclude, includeEfAndGenerated),

                "naming_convention" => BuildNamingConventionRule(id, name, description, severity, entry, config.Exclude),

                "no_captive_dependencies" => new NoCaptiveDependenciesRule(
                    id, name, description, severity),

                "no_dead_code" => BuildNoDeadCodeRule(id, name, description, severity, entry, globalAcks, config.Exclude),

                "slice_isolation" => BuildSliceIsolationRule(id, name, description, severity, entry, effectiveRootNs),

                _ => throw new InvalidOperationException(
                    $"Unsupported rule type '{entry.Type}' in rule '{id}'. Supported types: forbid_dependency, allow_only_dependencies, no_cycles, max_metric, god_class, naming_convention, no_captive_dependencies, no_dead_code, slice_isolation.")
            };

            rules.Add(rule);
            index++;
        }

        return rules;
    }

    private static ForbidDependencyRule BuildForbidDependencyRule(
        string id, string name, string description, RuleSeverity severity, RuleEntry entry, List<string>? globalExclude)
    {
        var fromProjects = CombinePatterns(entry.FromProject, entry.FromProjects);
        var toProjects = CombinePatterns(entry.ToProject, entry.ToProjects);
        var excludeFromProjects = CombinePatterns(entry.ExcludeFromProject, entry.ExcludeFromProjects);
        var toPatterns = entry.To?.Where(t => !string.IsNullOrWhiteSpace(t)).ToList() ?? [];
        var mergedExcludeFrom = MergeExcludes(globalExclude, entry.ExcludeFrom);

        if (string.IsNullOrWhiteSpace(entry.From) && (fromProjects is null || fromProjects.Count == 0))
        {
            throw new InvalidOperationException($"Rule '{id}' (forbid_dependency) requires 'from' or 'from_project'/'from_projects'.");
        }
        if (toPatterns.Count == 0 && (toProjects is null || toProjects.Count == 0))
        {
            throw new InvalidOperationException($"Rule '{id}' (forbid_dependency) requires at least one entry in 'to' or 'to_project'/'to_projects'.");
        }

        ValidateGlobs(id, [entry.From], fromProjects, toPatterns, toProjects, mergedExcludeFrom, excludeFromProjects);
        return new ForbidDependencyRule(
            id, name, description, severity,
            entry.From ?? "*",
            toPatterns,
            mergedExcludeFrom,
            fromProjects,
            toProjects,
            excludeFromProjects);
    }

    private static AllowOnlyDependenciesRule BuildAllowOnlyDependenciesRule(
        string id, string name, string description, RuleSeverity severity, RuleEntry entry)
    {
        var fromProjects = CombinePatterns(entry.FromProject, entry.FromProjects);
        var allowed = (entry.Allow ?? entry.To)?.Where(a => !string.IsNullOrWhiteSpace(a)).ToList() ?? [];

        if (string.IsNullOrWhiteSpace(entry.From) && (fromProjects is null || fromProjects.Count == 0))
        {
            throw new InvalidOperationException($"Rule '{id}' (allow_only_dependencies) requires 'from' or 'from_project'/'from_projects'.");
        }
        if (allowed.Count == 0)
        {
            throw new InvalidOperationException($"Rule '{id}' (allow_only_dependencies) requires at least one entry in 'allow'.");
        }

        ValidateGlobs(id, [entry.From], fromProjects, allowed);
        return new AllowOnlyDependenciesRule(id, name, description, severity, entry.From ?? "*", allowed, fromProjects);
    }

    private static NoCyclesRule BuildNoCyclesRule(
        string id, string name, string description, RuleSeverity severity, RuleEntry entry)
    {
        GlobMatcher.ValidatePattern(entry.Target, $"rule '{id}'");
        return new NoCyclesRule(
            id, name, description, severity,
            ParseScope(entry.Scope),
            entry.Target,
            excludeMigrations: !(entry.IncludeMigrations ?? false),
            crossProjectOnly: entry.CrossProjectOnly ?? false,
            excludeParentChild: !(entry.IncludeParentChild ?? false));
    }

    private static MaxMetricRule BuildMaxMetricRule(
        string id, string name, string description, RuleSeverity severity, RuleEntry entry,
        List<string>? globalExclude, bool includeEfAndGenerated)
    {
        if (string.IsNullOrWhiteSpace(entry.Metric))
        {
            throw new InvalidOperationException($"Rule '{id}' (max_metric) requires 'metric' (e.g. 'max_method_cyclomatic_complexity', 'lcom4', 'efferent_coupling').");
        }
        if (!entry.Threshold.HasValue)
        {
            throw new InvalidOperationException($"Rule '{id}' (max_metric) requires a numeric 'threshold'.");
        }

        var scope = string.IsNullOrWhiteSpace(entry.Scope) ? NodeKind.Type : ParseScope(entry.Scope);
        if (scope is not (NodeKind.Type or NodeKind.Method))
        {
            throw new InvalidOperationException($"Rule '{id}' (max_metric) does not support scope '{entry.Scope}'. Supported scopes: type, method.");
        }

        var excludes = MergeExcludes(globalExclude, entry.Exclude);
        ValidateGlobs(id, [entry.Target], excludes);
        return new MaxMetricRule(
            id, name, description, severity,
            entry.Metric.Trim(),
            entry.Threshold.Value,
            entry.Target,
            scope,
            excludeEfAndGenerated: !includeEfAndGenerated,
            excludePatterns: excludes);
    }

    private static GodClassRule BuildGodClassRule(
        string id, string name, string description, RuleSeverity severity, RuleEntry entry,
        List<string>? globalExclude, bool includeEfAndGenerated)
    {
        if (entry.MinConditions.HasValue && (entry.MinConditions.Value < 1 || entry.MinConditions.Value > 3))
        {
            throw new InvalidOperationException($"Rule '{id}' (god_class) 'min_conditions' must be between 1 and 3 (received {entry.MinConditions.Value}).");
        }

        var excludes = MergeExcludes(globalExclude, entry.Exclude);
        ValidateGlobs(id, [entry.Target], excludes);
        return new GodClassRule(
            id, name, description, severity,
            maxLines: entry.MaxLines ?? 500,
            maxComplexity: entry.MaxComplexity ?? 50,
            maxCoupling: entry.MaxCoupling ?? 20,
            minConditions: entry.MinConditions ?? 3,
            targetPatterns: string.IsNullOrWhiteSpace(entry.Target) ? null : [entry.Target],
            excludePatterns: excludes,
            excludeEfAndGenerated: !includeEfAndGenerated);
    }

    private static NamingConventionRule BuildNamingConventionRule(
        string id, string name, string description, RuleSeverity severity, RuleEntry entry, List<string>? globalExclude)
    {
        if (string.IsNullOrWhiteSpace(entry.Pattern))
        {
            throw new InvalidOperationException($"Rule '{id}' (naming_convention) requires 'pattern' (e.g. 'I*', '*Service').");
        }

        var excludes = MergeExcludes(globalExclude, entry.Exclude);
        ValidateGlobs(id, [entry.Target, entry.Pattern], excludes);
        return new NamingConventionRule(
            id, name, description, severity,
            entry.Target ?? "*",
            entry.Pattern.Trim(),
            ParseTypeKind(entry.TypeKind),
            excludes);
    }

    private static NoDeadCodeRule BuildNoDeadCodeRule(
        string id, string name, string description, RuleSeverity severity, RuleEntry entry,
        IReadOnlyList<DeadCodeAcknowledgement> globalAcks, List<string>? globalExclude)
    {
        if (!string.IsNullOrWhiteSpace(entry.Profile))
        {
            var p = entry.Profile.Trim().ToLowerInvariant();
            if (p is not ("auto" or "app" or "lib" or "strict"))
            {
                throw new InvalidOperationException($"Rule '{id}' (no_dead_code) has invalid profile '{entry.Profile}'. Supported profiles: 'auto', 'app', 'lib', 'strict'.");
            }
        }

        var scope = ParseScope(entry.Scope ?? "type");
        if (scope is not (NodeKind.Type or NodeKind.Method))
        {
            throw new InvalidOperationException($"Rule '{id}' (no_dead_code) does not support scope '{entry.Scope}'. Supported scopes: type, method.");
        }

        var excludes = MergeExcludes(globalExclude, entry.Exclude);
        ValidateGlobs(id, [entry.Target], excludes);
        return new NoDeadCodeRule(
            id, name, description, severity,
            scope,
            entry.TreatPublicApiAsEntryPoint ?? true,
            entry.Target,
            entry.Profile,
            MergeAcknowledgements(globalAcks, ParseAcknowledgements(entry.Acknowledge)),
            excludes);
    }

    private static SliceIsolationRule BuildSliceIsolationRule(
        string id, string name, string description, RuleSeverity severity, RuleEntry entry, string? rootNamespace)
    {
        if (string.IsNullOrWhiteSpace(entry.Segment))
        {
            throw new InvalidOperationException($"Rule '{id}' (slice_isolation) requires 'segment' (e.g. 'Features' or 'Modules').");
        }

        return new SliceIsolationRule(
            id, name, description, severity,
            entry.Segment.Trim(),
            entry.Allow ?? ["Contracts", "Public", "IntegrationEvents", "Abstractions"],
            rootNamespace);
    }

    private static void ValidateGlobs(string ruleId, params IEnumerable<string?>?[] groups)
    {
        foreach (var group in groups)
        {
            if (group is null) continue;
            foreach (var pattern in group)
            {
                GlobMatcher.ValidatePattern(pattern, $"rule '{ruleId}'");
            }
        }
    }

    private static string FormatYamlError(YamlDotNet.Core.YamlException ex, string? path)
    {
        var locationPrefix = string.IsNullOrWhiteSpace(path)
            ? $"Invalid YAML at line {ex.Start.Line}, column {ex.Start.Column}"
            : $"Invalid YAML in rules file '{path}' at line {ex.Start.Line}, column {ex.Start.Column}";

        // Walk inner exception chain when YamlDotNet wraps the real cause behind "Exception during deserialization"
        var effectiveMessage = ex.Message;
        var inner = ex.InnerException;
        while (inner is not null)
        {
            if (effectiveMessage.Contains("Exception during deserialization", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(inner.Message))
            {
                effectiveMessage = inner.Message;
            }
            inner = inner.InnerException;
        }

        var detail = SanitizeYamlDotNetMessage(effectiveMessage);
        if (ex.InnerException is FormatException fex && !string.IsNullOrWhiteSpace(fex.Message) &&
            !detail.Contains(fex.Message, StringComparison.Ordinal))
        {
            detail = $"{detail} ({fex.Message})";
        }

        var listHint = effectiveMessage.Contains("System.String", StringComparison.Ordinal) &&
                       effectiveMessage.Contains("deserialize", StringComparison.OrdinalIgnoreCase)
            ? " Hint: use 'from_projects:' / 'to_projects:' / 'exclude_from_projects:' for YAML lists, or a single string for 'from_project:' / 'to_project:'."
            : string.Empty;

        return $"{locationPrefix}: {detail}{listHint}";
    }

    private static string SanitizeYamlDotNetMessage(string message)
    {
        const string marker = "No node deserializer was able to deserialize the node into type ";
        var idx = message.IndexOf(marker, StringComparison.Ordinal);
        if (idx >= 0)
        {
            var typeSpec = message[(idx + marker.Length)..].Trim();
            var expected = DescribeExpectedYamlType(typeSpec);
            return $"Invalid value type: expected {expected}.";
        }

        const string castMarker = "Invalid cast from '";
        var castIdx = message.IndexOf(castMarker, StringComparison.Ordinal);
        if (castIdx >= 0)
        {
            var toIdx = message.IndexOf("' to '", castIdx, StringComparison.Ordinal);
            if (toIdx > castIdx)
            {
                var fromSpec = message[(castIdx + castMarker.Length)..toIdx];
                var toSpec = message[(toIdx + 6)..].TrimEnd('.', '\'');
                return $"Invalid value type: expected {DescribeExpectedYamlType(toSpec)} (got {DescribeExpectedYamlType(fromSpec)}).";
            }
        }

        if (message.Contains("Expected 'SequenceStart'", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid value type: expected a YAML list (e.g. ['item1', 'item2']).";
        }

        if (message.Contains("Expected 'MappingStart'", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid value type: expected a YAML mapping (key: value).";
        }

        if (message.Contains("Expected 'Scalar'", StringComparison.OrdinalIgnoreCase))
        {
            return "Invalid value type: expected a single scalar value (string, number, or boolean).";
        }

        // Strip assembly-qualified CLR metadata if YamlDotNet includes it in other exception messages
        var versionIdx = message.IndexOf(", Version=", StringComparison.Ordinal);
        if (versionIdx > 0)
        {
            var assemblyComma = message.LastIndexOf(',', versionIdx - 1);
            if (assemblyComma > 0)
            {
                return message[..assemblyComma].TrimEnd() + ".";
            }
        }

        return message;
    }

    private static string DescribeExpectedYamlType(string clrTypeSpec)
    {
        if (clrTypeSpec.Contains("System.Int32", StringComparison.Ordinal) ||
            clrTypeSpec.Contains("System.Int64", StringComparison.Ordinal))
        {
            return "an integer";
        }
        if (clrTypeSpec.Contains("System.Double", StringComparison.Ordinal) ||
            clrTypeSpec.Contains("System.Single", StringComparison.Ordinal) ||
            clrTypeSpec.Contains("System.Decimal", StringComparison.Ordinal))
        {
            return "a number";
        }
        if (clrTypeSpec.Contains("System.Boolean", StringComparison.Ordinal))
        {
            return "a boolean (true or false)";
        }
        if (clrTypeSpec.Contains("System.Collections.Generic.List", StringComparison.Ordinal))
        {
            return "a YAML list";
        }
        if (clrTypeSpec.Contains("System.Collections.Generic.Dictionary", StringComparison.Ordinal))
        {
            return "a YAML mapping";
        }
        if (clrTypeSpec.Contains("System.String", StringComparison.Ordinal))
        {
            return "a string";
        }
        return "a valid YAML value";
    }

    private static RuleSeverity ParseSeverity(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return RuleSeverity.Error;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "error" or "err" => RuleSeverity.Error,
            "warning" or "warn" => RuleSeverity.Warning,
            "info" or "information" or "note" => RuleSeverity.Info,
            _ => throw new InvalidOperationException($"Invalid severity '{raw}'. Supported values: error, warning, info.")
        };
    }

    private static NodeKind ParseScope(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return NodeKind.Namespace;
        }

        return raw.Trim().ToLowerInvariant() switch
        {
            "project" => NodeKind.Project,
            "namespace" => NodeKind.Namespace,
            "type" => NodeKind.Type,
            "method" => NodeKind.Method,
            _ => throw new InvalidOperationException($"Invalid scope '{raw}'. Supported values: project, namespace, type, method.")
        };
    }

    private static TypeKind? ParseTypeKind(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (!int.TryParse(trimmed, out _) &&
            Enum.TryParse<TypeKind>(trimmed, ignoreCase: true, out var kind) &&
            Enum.IsDefined(typeof(TypeKind), kind) &&
            kind != TypeKind.Unknown)
        {
            return kind;
        }

        throw new InvalidOperationException($"Invalid type_kind '{raw}'. Supported values: class, interface, struct, record, enum, delegate.");
    }

    private static List<string>? CombinePatterns(string? single, List<string>? list)
    {
        if (string.IsNullOrWhiteSpace(single) && (list is null || list.Count == 0))
        {
            return null;
        }

        var combined = new List<string>();
        if (!string.IsNullOrWhiteSpace(single))
        {
            combined.Add(single.Trim());
        }
        if (list is not null)
        {
            foreach (var item in list)
            {
                if (!string.IsNullOrWhiteSpace(item))
                {
                    combined.Add(item.Trim());
                }
            }
        }
        return combined.Count > 0 ? combined : null;
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

    private static readonly HashSet<string> ValidLayerNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "domain", "application", "infrastructure", "presentation"
    };

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
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var trimmedKey = key.Trim();
            if (!ValidLayerNames.Contains(trimmedKey))
            {
                throw new InvalidOperationException(
                    $"Unknown layer name '{trimmedKey}' in 'layers:'. Supported layer keys: domain, application, infrastructure, presentation.");
            }

            if (values is null)
            {
                continue;
            }

            var cleaned = values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToList();
            foreach (var pat in cleaned)
            {
                GlobMatcher.ValidatePattern(pat, $"layers.{trimmedKey}");
            }
            if (cleaned.Count > 0)
            {
                map[trimmedKey] = cleaned;
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
        public string? FromProject { get; set; }
        public List<string>? FromProjects { get; set; }
        public List<string>? To { get; set; }
        public string? ToProject { get; set; }
        public List<string>? ToProjects { get; set; }
        public List<string>? Allow { get; set; }
        public List<string>? ExcludeFrom { get; set; }
        public string? ExcludeFromProject { get; set; }
        public List<string>? ExcludeFromProjects { get; set; }
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
