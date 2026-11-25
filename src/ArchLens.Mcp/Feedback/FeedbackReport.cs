using System.Security.Cryptography;
using System.Text;

namespace ArchLens.Mcp.Feedback;

/// <summary>
/// A sanitized, immutable feedback report. <see cref="Fingerprint"/> is derived from category, area and summary so
/// that a repeated complaint is recognized instead of opening a second identical issue.
/// </summary>
public sealed record FeedbackReport(
    string Category,
    string Severity,
    string Summary,
    string? Details,
    string? Area,
    string? Tool,
    string ArchLensVersion,
    string Runtime,
    DateTimeOffset CreatedAtUtc,
    string Fingerprint)
{
    public static IReadOnlyList<string> Categories { get; } =
        ["bug", "missing_feature", "usability", "performance", "security", "other"];

    public static IReadOnlyList<string> Severities { get; } = ["low", "medium", "high"];

    public static string ComputeFingerprint(string category, string? area, string summary)
    {
        var normalized = $"{category.Trim().ToLowerInvariant()}|{area?.Trim().ToLowerInvariant()}|{CollapseWhitespace(summary).ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash)[..12];
    }

    private static string CollapseWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>Marker embedded in the issue body; the dedupe lookup searches for it in existing agent-feedback issues.</summary>
public static class FeedbackMarker
{
    public static string For(string fingerprint) => $"<!-- archlens-feedback:{fingerprint} -->";
}

/// <summary>Result of one delivery attempt. <paramref name="Duplicate"/> means an identical report already exists.</summary>
public sealed record FeedbackSendResult(bool Delivered, string? IssueUrl, string? Error, bool Duplicate = false)
{
    public static FeedbackSendResult Success(string? issueUrl, bool duplicate = false) => new(true, issueUrl, null, duplicate);

    public static FeedbackSendResult Failure(string error) => new(false, null, error);
}

/// <summary>Transport used by <see cref="FeedbackService"/>. Implemented over HTTP; faked in tests.</summary>
public interface IFeedbackSender
{
    Task<FeedbackSendResult> SendAsync(FeedbackReport report, CancellationToken cancellationToken);
}
