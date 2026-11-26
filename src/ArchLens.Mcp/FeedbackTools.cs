using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using ArchLens.Mcp.Feedback;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace ArchLens.Mcp;

/// <summary>
/// Lets an agent report friction with ArchLens itself — a wrong analysis result, a confusing payload, a missing
/// capability. Reports are sanitized, rate-limited, deduplicated by fingerprint and queued locally when the intake
/// is unreachable, so the tool never blocks or breaks an analysis run.
/// </summary>
[McpServerToolType]
public sealed class FeedbackTools(FeedbackService feedback)
{
    private static readonly string ArchLensVersion = typeof(FeedbackTools).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(FeedbackTools).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";

    private static readonly string RuntimeDescription =
        $"{RuntimeInformation.FrameworkDescription} ({RuntimeInformation.RuntimeIdentifier})";

    private readonly FeedbackService _feedback = feedback;

    [McpServerTool(Name = "give_feedback", Title = "Give ArchLens Feedback", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = true)]
    [Description(
        "Report a bug, a wrong or confusing result, a missing capability, or a performance problem in ArchLens " +
        "itself (its MCP tools, CLI, rules, presets, metrics or payload format). Use it after a long analysis run or " +
        "when a tool clearly did not behave as documented. Report ArchLens only, never the user's own codebase or an " +
        "unrelated project; write in English and include the concrete call and the observed versus expected result. " +
        "Never include secrets, tokens, credentials, personal data or proprietary source code. Do not report the same " +
        "issue twice. Delivery goes to the shared bomdenet feedback intake (or the ArchLens GitHub issue tracker when " +
        "configured); when neither is reachable the report stays on this machine and is retried at the next server start.")]
    public async Task<CallToolResult> GiveFeedbackAsync(
        [Description("Kind of feedback: 'bug', 'missing_feature', 'usability', 'performance', 'security', or 'other'.")] string category,
        [Description("How much the issue affected the work: 'low', 'medium', or 'high'.")] string severity,
        [Description("One-sentence report, at most 240 characters.")] string summary,
        [Description("Optional reproduction steps plus observed and expected behavior, at most 4000 characters.")] string? details = null,
        [Description("Optional affected area such as an MCP tool name, CLI command, rule id or preset, at most 100 characters.")] string? area = null,
        [Description("Optional ArchLens MCP tool that misbehaved, e.g. 'hotspots', 'check_rules', 'query'.")] string? tool = null,
        CancellationToken cancellationToken = default)
    {
        var cleanCategory = FeedbackSanitizer.Clean(category, 32).ToLowerInvariant();
        if (!FeedbackReport.Categories.Contains(cleanCategory))
        {
            return McpResponseFormatter.Error(
                "invalid_feedback",
                $"Parameter 'category' must be one of: {string.Join(", ", FeedbackReport.Categories)}.");
        }

        var cleanSeverity = FeedbackSanitizer.Clean(severity, 16).ToLowerInvariant();
        if (!FeedbackReport.Severities.Contains(cleanSeverity))
        {
            return McpResponseFormatter.Error(
                "invalid_feedback",
                $"Parameter 'severity' must be one of: {string.Join(", ", FeedbackReport.Severities)}.");
        }

        var cleanSummary = FeedbackSanitizer.Prepare(summary, FeedbackSanitizer.MaxSummaryLength);
        if (cleanSummary.Length == 0)
        {
            return McpResponseFormatter.Error("invalid_feedback", "Parameter 'summary' must not be empty.");
        }

        var cleanDetails = Normalize(FeedbackSanitizer.PrepareMultiline(details, FeedbackSanitizer.MaxDetailsLength));
        var cleanArea = Normalize(FeedbackSanitizer.Prepare(area, FeedbackSanitizer.MaxAreaLength));
        var cleanTool = Normalize(FeedbackSanitizer.Prepare(tool, FeedbackSanitizer.MaxToolLength));

        var report = new FeedbackReport(
            Id: Guid.NewGuid(),
            cleanCategory,
            cleanSeverity,
            cleanSummary,
            cleanDetails,
            cleanArea,
            cleanTool,
            ArchLensVersion,
            RuntimeDescription,
            DateTimeOffset.UtcNow,
            FeedbackReport.ComputeFingerprint(cleanCategory, cleanArea, cleanSummary));

        var outcome = await _feedback.SubmitAsync(report, cancellationToken).ConfigureAwait(false);
        return McpResponseFormatter.Ok(new
        {
            status = outcome.Status,
            transport = outcome.Transport,
            repository = outcome.Transport == "github" ? _feedback.Repository : null,
            issueUrl = outcome.IssueUrl,
            fingerprint = outcome.Fingerprint,
            reason = outcome.Reason,
            error = outcome.Error,
            hint = outcome.Hint
        });
    }

    private static string? Normalize(string value) => value.Length == 0 ? null : value;
}
