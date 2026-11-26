using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArchLens.Model;

namespace ArchLens.Analysis;

/// <summary>
/// Exports ArchLens rule violations and Roslyn analyzer diagnostics in SARIF 2.1.0 format
/// for integration with GitHub Code Scanning, Azure DevOps, and IDEs.
/// </summary>
public static class SarifExporter
{
    private const string SarifSchema = "https://json.schemastore.org/sarif-2.1.0.json";
    private const string SarifVersion = "2.1.0";

    private static readonly string DefaultToolVersion =
        typeof(SarifExporter).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(SarifExporter).Assembly.GetName().Version?.ToString(3)
        ?? "0.11.0";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static string Export(
        IEnumerable<RuleViolation>? violations = null,
        IEnumerable<AnalyzerDiagnostic>? analyzerDiagnostics = null,
        string? toolVersion = null)
    {
        var effectiveVersion = string.IsNullOrWhiteSpace(toolVersion) ? DefaultToolVersion : toolVersion;
        var violationList = violations?.ToList() ?? [];
        var diagList = analyzerDiagnostics?.ToList() ?? [];

        var rulesById = new Dictionary<string, SarifReportingDescriptor>(StringComparer.Ordinal);

        foreach (var v in violationList)
        {
            if (!rulesById.ContainsKey(v.RuleId))
            {
                rulesById[v.RuleId] = new SarifReportingDescriptor(
                    Id: v.RuleId,
                    ShortDescription: new SarifMessage(string.IsNullOrWhiteSpace(v.RuleName) ? v.RuleId : v.RuleName),
                    DefaultConfiguration: new SarifConfiguration(MapLevel(v.Severity)));
            }
        }

        foreach (var d in diagList)
        {
            if (!rulesById.ContainsKey(d.Id))
            {
                rulesById[d.Id] = new SarifReportingDescriptor(
                    Id: d.Id,
                    ShortDescription: new SarifMessage(string.IsNullOrWhiteSpace(d.Title) ? d.Id : d.Title),
                    DefaultConfiguration: new SarifConfiguration(MapLevel(d.Severity)),
                    Properties: string.IsNullOrWhiteSpace(d.Category)
                        ? null
                        : new Dictionary<string, string> { ["category"] = d.Category });
            }
        }

        var orderedRules = rulesById.Values.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        var ruleIndexById = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < orderedRules.Count; i++)
        {
            ruleIndexById[orderedRules[i].Id] = i;
        }

        var results = new List<SarifResult>(violationList.Count + diagList.Count);
        foreach (var v in violationList)
        {
            results.Add(new SarifResult(
                RuleId: v.RuleId,
                RuleIndex: ruleIndexById[v.RuleId],
                Level: MapLevel(v.Severity),
                Message: new SarifMessage(v.Message),
                Locations: BuildLocations(v.Location)));
        }

        foreach (var d in diagList)
        {
            results.Add(new SarifResult(
                RuleId: d.Id,
                RuleIndex: ruleIndexById[d.Id],
                Level: MapLevel(d.Severity),
                Message: new SarifMessage(d.Message),
                Locations: BuildLocations(d.Location)));
        }

        var log = new SarifLog(
            Schema: SarifSchema,
            Version: SarifVersion,
            Runs:
            [
                new SarifRun(
                    Tool: new SarifTool(
                        new SarifToolComponent(
                            Name: "ArchLens",
                            Version: effectiveVersion,
                            InformationUri: "https://github.com/archlens/archlens",
                            Rules: orderedRules)),
                    Results: results)
            ]);

        return JsonSerializer.Serialize(log, JsonOptions);
    }

    private static string MapLevel(RuleSeverity severity) => severity switch
    {
        RuleSeverity.Error => "error",
        RuleSeverity.Warning => "warning",
        _ => "note"
    };

    private static List<SarifLocation>? BuildLocations(SourceLocation? loc)
    {
        if (loc is null || string.IsNullOrWhiteSpace(loc.FilePath))
        {
            return null;
        }

        var uri = EncodeArtifactUri(loc.FilePath);
        return
        [
            new SarifLocation(
                new SarifPhysicalLocation(
                    new SarifArtifactLocation(uri),
                    new SarifRegion(Math.Max(1, loc.Line), Math.Max(1, loc.Column))))
        ];
    }

    /// <summary>
    /// Converts a file path into an RFC 3986 compliant URI or relative URI reference per SARIF 2.1.0 §3.4.3.
    /// </summary>
    private static string EncodeArtifactUri(string rawPath)
    {
        if (Path.IsPathRooted(rawPath))
        {
            try
            {
                return new Uri(Path.GetFullPath(rawPath)).AbsoluteUri;
            }
            catch
            {
                // Fall back to segment encoding
            }
        }

        var normalized = rawPath.Replace('\\', '/');
        var segments = normalized.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = Uri.EscapeDataString(segments[i]);
        }

        return string.Join("/", segments);
    }

    private sealed record SarifLog(
        [property: JsonPropertyName("$schema")] string Schema,
        string Version,
        List<SarifRun> Runs);

    private sealed record SarifRun(SarifTool Tool, List<SarifResult> Results);

    private sealed record SarifTool(SarifToolComponent Driver);

    private sealed record SarifToolComponent(
        string Name,
        string Version,
        string InformationUri,
        List<SarifReportingDescriptor> Rules);

    private sealed record SarifReportingDescriptor(
        string Id,
        SarifMessage ShortDescription,
        SarifConfiguration DefaultConfiguration,
        Dictionary<string, string>? Properties = null);

    private sealed record SarifConfiguration(string Level);

    private sealed record SarifResult(
        string RuleId,
        int RuleIndex,
        string Level,
        SarifMessage Message,
        List<SarifLocation>? Locations);

    private sealed record SarifMessage(string Text);

    private sealed record SarifLocation(SarifPhysicalLocation PhysicalLocation);

    private sealed record SarifPhysicalLocation(SarifArtifactLocation ArtifactLocation, SarifRegion Region);

    private sealed record SarifArtifactLocation(string Uri);

    private sealed record SarifRegion(int StartLine, int StartColumn);
}
