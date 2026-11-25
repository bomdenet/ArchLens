using System.Text.RegularExpressions;

namespace ArchLens.Mcp.Feedback;

/// <summary>
/// Defensive cleanup for model-written feedback before it leaves the machine. This is a guard against obvious
/// mistakes (a pasted token, an absolute user path), not a data-loss-prevention system: the tool description and
/// the issue body both tell the reader to review content before acting on it.
/// </summary>
public static partial class FeedbackSanitizer
{
    public const int MaxSummaryLength = 240;
    public const int MaxDetailsLength = 4000;
    public const int MaxAreaLength = 100;
    public const int MaxToolLength = 64;

    /// <summary>Normalizes whitespace, strips control characters, and removes HTML comment delimiters.</summary>
    public static string Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = Whitespace().Replace(value.Replace("<!--", " ").Replace("-->", " "), " ").Trim();
        normalized = ControlChars().Replace(normalized, string.Empty);
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength].TrimEnd();
    }

    /// <summary>Replaces credential-shaped strings and user-specific absolute paths with placeholders.</summary>
    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var redacted = GitHubToken().Replace(value, "<redacted-token>");
        redacted = OpenAiStyleKey().Replace(redacted, "<redacted-key>");
        redacted = SlackToken().Replace(redacted, "<redacted-token>");
        redacted = AwsAccessKey().Replace(redacted, "<redacted-key>");
        redacted = BearerToken().Replace(redacted, "Bearer <redacted-token>");
        redacted = SecretAssignment().Replace(redacted, "$1=<redacted>");
        redacted = WindowsUserPath().Replace(redacted, "%USERPROFILE%");
        redacted = PosixUserPath().Replace(redacted, "~");
        return redacted;
    }

    /// <summary>Clean + redact in the order the reporter expects: secrets never survive into a truncated tail.</summary>
    public static string Prepare(string? value, int maxLength) => Clean(Redact(value ?? string.Empty), maxLength);

    /// <summary>
    /// Variant for reproduction steps: keeps line breaks (they carry the numbered steps) while still removing
    /// control characters, HTML comment delimiters and credentials.
    /// </summary>
    public static string PrepareMultiline(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Replace("<!--", " ")
            .Replace("-->", " ");
        normalized = ControlCharsExceptNewline().Replace(normalized, string.Empty);
        normalized = Redact(normalized);
        normalized = string.Join('\n', normalized
            .Split('\n')
            .Select(static line => line.TrimEnd())
            .Where(static line => line.Length > 0));
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength].TrimEnd();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F\u200B-\u200D\uFEFF]")]
    private static partial Regex ControlChars();

    [GeneratedRegex(@"[\u0000-\u0009\u000B\u000C\u000E-\u001F\u007F\u200B-\u200D\uFEFF]")]
    private static partial Regex ControlCharsExceptNewline();

    [GeneratedRegex(@"\b(?:gh[pousr]|github_pat)_[A-Za-z0-9_]{16,}\b")]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_-]{16,}\b")]
    private static partial Regex OpenAiStyleKey();

    [GeneratedRegex(@"\bxox[baprs]-[A-Za-z0-9-]{10,}\b")]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b")]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9._\-]{10,}")]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"(?i)\b(token|password|passwd|secret|api[-_ ]?key|authorization)\b\s*[:=]\s*\S+")]
    private static partial Regex SecretAssignment();

    [GeneratedRegex(@"[A-Za-z]:\\Users\\[^\\\s""']+")]
    private static partial Regex WindowsUserPath();

    [GeneratedRegex(@"/(?:home|Users)/[^/\s""']+")]
    private static partial Regex PosixUserPath();
}
