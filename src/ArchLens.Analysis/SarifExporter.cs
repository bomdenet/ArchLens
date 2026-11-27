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
        string? toolVersion = null,
        IEnumerable<string>? toolWarnings = null,
        IEnumerable<string>? toolErrors = null,
        bool executionSuccessful = true,
        string? rootPath = null)
    {
        var effectiveVersion = string.IsNullOrWhiteSpace(toolVersion) ? DefaultToolVersion : toolVersion;
        var violationList = violations?.ToList() ?? [];
        var diagList = analyzerDiagnostics?.ToList() ?? [];
        var hasRootBase = !string.IsNullOrWhiteSpace(rootPath);
        Dictionary<string, SarifArtifactLocation>? originalUriBaseIds = null;
        if (hasRootBase)
        {
            var baseUri = ToDirectoryUri(rootPath!);
            if (baseUri is not null)
            {
                originalUriBaseIds = new Dictionary<string, SarifArtifactLocation>(StringComparer.Ordinal)
                {
                    ["SRCROOT"] = new SarifArtifactLocation(baseUri)
                };
            }
        }

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
        var seenResultKeys = new HashSet<(string RuleId, string Level, string Message, string? Uri, int Line, int Col, string? Project)>();

        void TryAddResult(string ruleId, string level, string message, SourceLocation? loc, string? projectName = null)
        {
            var locations = BuildLocations(loc, originalUriBaseIds is not null ? "SRCROOT" : null);
            var firstLoc = locations?[0].PhysicalLocation;
            var key = (ruleId, level, message, firstLoc?.ArtifactLocation.Uri, firstLoc?.Region.StartLine ?? 0, firstLoc?.Region.StartColumn ?? 0, projectName);
            if (seenResultKeys.Add(key))
            {
                results.Add(new SarifResult(
                    RuleId: ruleId,
                    RuleIndex: ruleIndexById[ruleId],
                    Level: level,
                    Message: new SarifMessage(message),
                    Locations: locations,
                    Properties: string.IsNullOrWhiteSpace(projectName)
                        ? null
                        : new Dictionary<string, string> { ["project"] = projectName }));
            }
        }

        foreach (var v in violationList)
        {
            TryAddResult(v.RuleId, MapLevel(v.Severity), v.Message, v.Location);
        }

        foreach (var d in diagList)
        {
            TryAddResult(d.Id, MapLevel(d.Severity), d.Message, d.Location, d.ProjectName);
        }

        var notifications = new List<SarifNotification>();
        if (toolErrors is not null)
        {
            foreach (var err in toolErrors.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.Ordinal))
            {
                notifications.Add(new SarifNotification(Level: "error", Message: new SarifMessage(err)));
            }
        }
        if (toolWarnings is not null)
        {
            foreach (var warn in toolWarnings.Where(w => !string.IsNullOrWhiteSpace(w)).Distinct(StringComparer.Ordinal))
            {
                notifications.Add(new SarifNotification(Level: "warning", Message: new SarifMessage(warn)));
            }
        }

        List<SarifInvocation>? invocations = (!executionSuccessful || notifications.Count > 0)
            ? [new SarifInvocation(
                ExecutionSuccessful: executionSuccessful,
                ToolExecutionNotifications: notifications.Count > 0 ? notifications : null)]
            : null;

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
                    Results: results,
                    Invocations: invocations,
                    OriginalUriBaseIds: originalUriBaseIds)
            ]);

        return JsonSerializer.Serialize(log, JsonOptions);
    }

    private static string MapLevel(RuleSeverity severity) => severity switch
    {
        RuleSeverity.Error => "error",
        RuleSeverity.Warning => "warning",
        _ => "note"
    };

    private static List<SarifLocation>? BuildLocations(SourceLocation? loc, string? uriBaseId)
    {
        if (loc is null || string.IsNullOrWhiteSpace(loc.FilePath))
        {
            return null;
        }

        var isAbsolute = Path.IsPathRooted(loc.FilePath);
        var uri = EncodeArtifactUri(loc.FilePath);
        return
        [
            new SarifLocation(
                new SarifPhysicalLocation(
                    new SarifArtifactLocation(uri, isAbsolute ? null : uriBaseId),
                    new SarifRegion(Math.Max(1, loc.Line), Math.Max(1, loc.Column))))
        ];
    }

    private static string? ToDirectoryUri(string rootPath)
    {
        try
        {
            var full = Path.GetFullPath(rootPath);
            if (!full.EndsWith(Path.DirectorySeparatorChar) && !full.EndsWith(Path.AltDirectorySeparatorChar))
            {
                full += Path.DirectorySeparatorChar;
            }
            return new Uri(full).AbsoluteUri;
        }
        catch
        {
            return null;
        }
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

    private sealed record SarifRun(
        SarifTool Tool,
        List<SarifResult> Results,
        List<SarifInvocation>? Invocations = null,
        Dictionary<string, SarifArtifactLocation>? OriginalUriBaseIds = null);

    private sealed record SarifInvocation(bool ExecutionSuccessful, List<SarifNotification>? ToolExecutionNotifications = null);

    private sealed record SarifNotification(string Level, SarifMessage Message);

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
        List<SarifLocation>? Locations,
        Dictionary<string, string>? Properties = null);

    private sealed record SarifMessage(string Text);

    private sealed record SarifLocation(SarifPhysicalLocation PhysicalLocation);

    private sealed record SarifPhysicalLocation(SarifArtifactLocation ArtifactLocation, SarifRegion Region);

    private sealed record SarifArtifactLocation(string Uri, string? UriBaseId = null);

    private sealed record SarifRegion(int StartLine, int StartColumn);
}
