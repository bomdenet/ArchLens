using System.ComponentModel;
using System.Text;
using ArchLens.Analysis;
using ArchLens.Query;
using ModelContextProtocol.Server;

namespace ArchLens.Mcp;

/// <summary>
/// Built-in MCP Resources exposing ArchLens SQL-over-Graph schema, architectural presets catalog,
/// and .archlens.yaml rule reference directly into agent context.
/// </summary>
[McpServerResourceType]
public static class ArchLensResources
{
    [McpServerResource(UriTemplate = "archlens://schema/sql", Name = "ArchLens SQL Schema", MimeType = "text/markdown")]
    [Description("Relational schema documentation and example queries for the read-only SQLite query tool.")]
    public static string GetSqlSchema() => GraphSqlQueryEngine.SchemaDocumentation;

    [McpServerResource(UriTemplate = "archlens://presets", Name = "ArchLens Architecture Presets", MimeType = "text/markdown")]
    [Description("Catalog of built-in architectural rule presets (clean-architecture, vertical-slice, modular-monolith, ddd) and sample .archlens.yaml snippets.")]
    public static string GetPresetsCatalog()
    {
        var sb = new StringBuilder();
        sb.AppendLine("# ArchLens Built-in Architecture Presets");
        sb.AppendLine();
        sb.AppendLine("Use `preset: <id>` and optional `root_namespace: MyApp` in `.archlens.yaml`, or run `archlens init --preset <id>`.");
        sb.AppendLine();

        foreach (var preset in ArchitecturePresets.All)
        {
            sb.AppendLine($"## `{preset.Id}` — {preset.Name}");
            sb.AppendLine(preset.Description);
            sb.AppendLine($"Aliases: `{string.Join("`, `", preset.Aliases)}`");
            sb.AppendLine();
            sb.AppendLine("```yaml");
            sb.AppendLine(ArchitecturePresets.GenerateYamlTemplate(preset.Id, "MyApp"));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [McpServerResource(UriTemplate = "archlens://rules/reference", Name = "ArchLens YAML Rules Reference", MimeType = "text/markdown")]
    [Description("Reference of supported .archlens.yaml rule types, metrics, scopes, and plugin extension conventions.")]
    public static string GetRulesReference() => """
        # ArchLens `.archlens.yaml` Rule Reference

        ## Top-level keys
        - `version`: `1`
        - `preset` / `presets`: built-in preset(s) (`clean-architecture`, `vertical-slice`, `modular-monolith`, `ddd`)
        - `root_namespace`: optional root namespace prefix for presets (e.g. `MyApp`)
        - `layers`: optional custom layer-to-namespace mapping for presets (keys: `domain`, `application`, `infrastructure`, `presentation`)
        - `exclude`: optional global glob exclusions for metric/god-class rules
        - `acknowledge`: optional list of `{ symbol, reason }` entries for intentionally unreferenced symbols (excluded from dead-code reports)
        - `rules`: list of custom rule entries

        ## Rule types (`type`)
        - `forbid_dependency`: `from`, `to[]`, `exclude_from[]?`
        - `allow_only_dependencies`: `from`, `allow[]`
        - `slice_isolation`: `segment` (`Modules` or `Features`), `allow[]` (permitted contract sub-namespaces)
        - `no_cycles`: `scope` (`project | namespace | type | method`), `target?`, `cross_project_only?`, `include_migrations?`, `include_parent_child?` (default `false` — skips pure `A <-> A.Sub` parent-child cycles)
        - `max_metric`: `metric`, `threshold`, `target?`, `exclude[]?`, `include_ef_and_generated?` (alias `include_generated_or_ef`; deprecated `include_infrastructure`)
          - Supported metrics: `max_method_cyclomatic_complexity` (alias `cyclomatic_complexity`), `total_cyclomatic_complexity`, `max_method_cognitive_complexity` (alias `cognitive_complexity`), `total_cognitive_complexity`, `lcom4`, `efferent_coupling`/`ce`, `afferent_coupling`/`ca`, `instability`, `line_count`/`loc`, `constructor_params`, plus any custom metric name from `.archlens/plugins/*.dll`
        - `god_class`: `max_lines?` (500), `max_complexity?` (50), `max_coupling?` (20), `min_conditions?` (3), `target?`, `exclude[]?`, `include_ef_and_generated?`
        - `naming_convention`: `target`, `pattern`, `type_kind?`
        - `no_captive_dependencies`: flags Singleton -> Scoped (direct or via Transient) DI bugs
        - `no_dead_code`: `scope` (`type | method`), `profile` (`auto | app | lib | strict`), `target?`, `treat_public_api_as_entry_point?`, `acknowledge[]?`

        ## Dynamic Plugins (`.archlens/plugins/`)
        Place compiled `.dll` assemblies implementing `IArchitectureRule`, `ICustomMetricCalculator`, or `IEntryPointDetector`
        (from `ArchLens.Model`) into `.archlens/plugins/` next to the solution and enable plugin execution with `--allow-plugins`
        (or `ARCHLENS_ALLOW_PLUGINS=1`); ArchLens loads them in isolated collectible `AssemblyLoadContext`s without locking DLL files.
        """;
}
