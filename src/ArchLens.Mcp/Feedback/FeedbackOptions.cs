namespace ArchLens.Mcp.Feedback;

/// <summary>
/// Feedback transport configuration. MCP servers are configured by their host process, so every knob is read
/// from an environment variable (client config <c>env</c> map, shell profile, or CI secrets) instead of argv.
/// Delivery is opt-in: without a token or an endpoint the tool reports <c>disabled</c> and never writes anything.
/// </summary>
public sealed record FeedbackOptions(
    FeedbackMode Mode,
    string Repository,
    string? Token,
    string? Endpoint,
    int MaxPerHour,
    string OutboxDirectory,
    string ApiBase = FeedbackOptions.DefaultApiBase)
{
    public const string DefaultRepository = "bomdenet/ArchLens";
    public const string DefaultApiBase = "https://api.github.com";
    public const int DefaultMaxPerHour = 5;

    /// <summary>
    /// Shared feedback intake for all bomdenet tools. It is the default transport because it needs no credential on
    /// the user's machine; <c>ARCHLENS_FEEDBACK_MODE=off</c> disables reporting entirely.
    /// </summary>
    public const string DefaultEndpoint = "https://feedback.bomdenet.com/api/feedback";

    /// <summary>True when feedback can be delivered immediately; <see cref="FeedbackMode.Outbox"/> still queues locally.</summary>
    public bool CanDeliver => Endpoint is not null || Token is not null;

    public static FeedbackOptions FromEnvironment()
    {
        var mode = ParseMode(Environment.GetEnvironmentVariable("ARCHLENS_FEEDBACK_MODE"));
        var token = FirstNonEmpty(
            Environment.GetEnvironmentVariable("ARCHLENS_FEEDBACK_TOKEN"),
            Environment.GetEnvironmentVariable("GITHUB_TOKEN"));
        var endpoint = FirstNonEmpty(Environment.GetEnvironmentVariable("ARCHLENS_FEEDBACK_URL")) ?? DefaultEndpoint;
        var repository = FirstNonEmpty(Environment.GetEnvironmentVariable("ARCHLENS_FEEDBACK_REPO")) ?? DefaultRepository;
        var apiBase = FirstNonEmpty(Environment.GetEnvironmentVariable("ARCHLENS_FEEDBACK_API")) ?? DefaultApiBase;

        var maxPerHour = int.TryParse(Environment.GetEnvironmentVariable("ARCHLENS_FEEDBACK_MAX_PER_HOUR"), out var parsed)
            ? Math.Clamp(parsed, 1, 50)
            : DefaultMaxPerHour;

        var outbox = FirstNonEmpty(Environment.GetEnvironmentVariable("ARCHLENS_FEEDBACK_OUTBOX"))
            ?? Path.Combine(GetLocalDataDirectory(), "feedback");

        return new FeedbackOptions(mode, repository, token, endpoint, maxPerHour, outbox, apiBase);
    }

    /// <summary>
    /// <c>off</c> disables the tool, <c>outbox</c> only queues locally (useful for air-gapped machines), and
    /// <c>auto</c> delivers when a token or endpoint exists.
    /// </summary>
    private static FeedbackMode ParseMode(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "off" or "disabled" or "false" or "0" => FeedbackMode.Off,
        "outbox" or "local" => FeedbackMode.Outbox,
        _ => FeedbackMode.Auto
    };

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string GetLocalDataDirectory() =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } local
            ? Path.Combine(local, "ArchLens")
            : Path.Combine(Path.GetTempPath(), "ArchLens");
}

public enum FeedbackMode
{
    Auto,
    Outbox,
    Off
}
