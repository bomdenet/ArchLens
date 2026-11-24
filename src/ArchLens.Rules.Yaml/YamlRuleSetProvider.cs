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
        return ParseYaml(yaml);
    }

    public IReadOnlyList<IArchitectureRule> ParseYaml(string yamlContent)
    {
        var config = Deserializer.Deserialize<ArchLensYamlConfig>(yamlContent)
            ?? new ArchLensYamlConfig();

        var rules = new List<IArchitectureRule>();
        var index = 1;

        foreach (var entry in config.Rules ?? [])
        {
            var id = string.IsNullOrWhiteSpace(entry.Id) ? $"RULE-{index:D3}" : entry.Id;
            var name = string.IsNullOrWhiteSpace(entry.Name) ? id : entry.Name;
            var description = entry.Description ?? string.Empty;
            var severity = ParseSeverity(entry.Severity);
            var type = (entry.Type ?? string.Empty).Trim().ToLowerInvariant();

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
                    ParseScope(entry.Scope)),

                "max_metric" => new MaxMetricRule(
                    id, name, description, severity,
                    entry.Metric ?? "cyclomatic_complexity",
                    entry.Threshold ?? 15,
                    entry.Target),

                "naming_convention" => new NamingConventionRule(
                    id, name, description, severity,
                    entry.Target ?? "*",
                    entry.Pattern ?? "*",
                    ParseTypeKind(entry.TypeKind)),

                _ => throw new InvalidOperationException($"Unsupported rule type '{entry.Type}' in rule '{id}'.")
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

    private sealed class ArchLensYamlConfig
    {
        public int Version { get; set; } = 1;
        public List<RuleEntry>? Rules { get; set; }
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
        public string? Scope { get; set; }
        public string? Metric { get; set; }
        public double? Threshold { get; set; }
        public string? Target { get; set; }
        public string? Pattern { get; set; }
        public string? TypeKind { get; set; }
    }
}
