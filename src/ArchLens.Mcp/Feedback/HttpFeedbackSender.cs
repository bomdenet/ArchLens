using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ArchLens.Mcp.Feedback;

/// <summary>
/// Delivers feedback either to a plain HTTP endpoint (a maintainer-hosted proxy, so end users need no token) or
/// directly to the repository's GitHub Issues API. Never throws: any transport problem becomes a failed result so
/// the caller can queue the report instead of losing it.
/// </summary>
public sealed class HttpFeedbackSender(FeedbackOptions options, HttpClient http) : IFeedbackSender
{
    private const string UserAgentProduct = "ArchLens";
    private const int MaxIssueTitleLength = 120;

    /// <summary>Product name recorded by the shared feedback service.</summary>
    public const string SourceName = "archlens";

    private readonly FeedbackOptions _options = options;
    private readonly HttpClient _http = http;

    public async Task<FeedbackSendResult> SendAsync(FeedbackReport report, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.Endpoint))
            {
                return await SendToEndpointAsync(report, cancellationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(_options.Token))
            {
                return FeedbackSendResult.Failure("not_configured");
            }

            return await SendToGitHubAsync(report, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            return FeedbackSendResult.Failure($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<FeedbackSendResult> SendToGitHubAsync(FeedbackReport report, CancellationToken cancellationToken)
    {
        var apiBase = $"{_options.ApiBase.TrimEnd('/')}/repos/{_options.Repository}/issues";
        var existing = await FindExistingIssueAsync(apiBase, report.Fingerprint, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return FeedbackSendResult.Success(existing, duplicate: true, transport: "github");
        }

        var payload = new
        {
            title = BuildTitle(report),
            body = BuildBody(report),
            labels = new[] { "agent-feedback" }
        };

        using var response = await PostIssueAsync(apiBase, payload, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
        {
            // A repository without the label rejects the whole request; retry unlabeled rather than lose the report.
            using var retry = await PostIssueAsync(apiBase, new { payload.title, payload.body }, cancellationToken)
                .ConfigureAwait(false);
            return await ReadIssueResultAsync(retry, cancellationToken).ConfigureAwait(false);
        }

        return await ReadIssueResultAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> PostIssueAsync(string apiBase, object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, apiBase)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> FindExistingIssueAsync(string apiBase, string fingerprint, CancellationToken cancellationToken)
    {
        var lookup = $"{apiBase}?labels=agent-feedback&state=all&per_page=100&sort=created&direction=desc";
        try
        {
            using var response = await _http.GetAsync(lookup, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Dedupe is best-effort: a missing label or a rate limit must not block the report itself.
                return null;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var marker = FeedbackMarker.For(fingerprint);
            foreach (var issue in document.RootElement.EnumerateArray())
            {
                if (issue.TryGetProperty("body", out var body) &&
                    body.ValueKind == JsonValueKind.String &&
                    body.GetString()?.Contains(marker, StringComparison.Ordinal) == true)
                {
                    return issue.TryGetProperty("html_url", out var url) ? url.GetString() : null;
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }

        return null;
    }

    private async Task<FeedbackSendResult> ReadIssueResultAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return FeedbackSendResult.Failure($"{(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 300)}");
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var url = document.RootElement.TryGetProperty("html_url", out var value) ? value.GetString() : null;
            return FeedbackSendResult.Success(url, transport: "github");
        }
        catch (JsonException)
        {
            return FeedbackSendResult.Success(null, transport: "github");
        }
    }

    private async Task<FeedbackSendResult> SendToEndpointAsync(FeedbackReport report, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(_options.Endpoint, BuildPayload(report), cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return FeedbackSendResult.Failure($"{(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 300)}");
        }

        string? url = null;
        bool duplicate = false;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in (string[])["issueUrl", "issue_url", "html_url", "url"])
                {
                    if (document.RootElement.TryGetProperty(name, out var value))
                    {
                        url = value.GetString();
                        break;
                    }
                }

                duplicate = IsTrue(document.RootElement, "duplicate") || IsTrue(document.RootElement, "deduplicated");
            }
        }
        catch (JsonException)
        {
            // A proxy may answer 202 with an empty body; delivery still succeeded.
        }

        return FeedbackSendResult.Success(url, duplicate, transport: "endpoint");
    }

    private static bool IsTrue(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.True;

    internal static string BuildTitle(FeedbackReport report)
    {
        var summary = FeedbackSanitizer.Prepare(report.Summary, FeedbackSanitizer.MaxSummaryLength);
        var title = $"[{report.Category}] {summary}";
        return title.Length <= MaxIssueTitleLength ? title : $"{title[..(MaxIssueTitleLength - 1)].TrimEnd()}…";
    }

    internal static string BuildBody(FeedbackReport report)
    {
        // Sanitize again at render time: reports restored from the on-disk outbox are trusted input, and this is the
        // single choke point every transport passes through.
        var summary = FeedbackSanitizer.Prepare(report.Summary, FeedbackSanitizer.MaxSummaryLength);
        var details = FeedbackSanitizer.PrepareMultiline(report.Details, FeedbackSanitizer.MaxDetailsLength);
        var tool = report.Tool is null ? null : FeedbackSanitizer.Prepare(report.Tool, FeedbackSanitizer.MaxToolLength);
        var area = report.Area is null ? null : FeedbackSanitizer.Prepare(report.Area, FeedbackSanitizer.MaxAreaLength);

        var sb = new StringBuilder();
        sb.AppendLine(FeedbackMarker.For(report.Fingerprint));
        sb.AppendLine("### Summary");
        sb.AppendLine(summary);
        sb.AppendLine();
        sb.AppendLine($"**Category:** {report.Category} · **Severity:** {report.Severity}" +
                      (string.IsNullOrEmpty(tool) ? string.Empty : $" · **Tool:** `{tool}`") +
                      (string.IsNullOrEmpty(area) ? string.Empty : $" · **Area:** {area}"));

        if (!string.IsNullOrEmpty(details))
        {
            sb.AppendLine();
            sb.AppendLine("### Details");
            sb.AppendLine(details);
        }

        sb.AppendLine();
        sb.AppendLine("### Environment");
        sb.AppendLine($"- ArchLens: `{report.ArchLensVersion}`");
        sb.AppendLine($"- Runtime: `{report.Runtime}`");
        sb.AppendLine($"- Reported: `{report.CreatedAtUtc:u}`");
        sb.AppendLine();
        sb.AppendLine("<sub>Submitted through the <code>give_feedback</code> MCP tool and sanitized on the client " +
                      "(credentials and user paths removed). Treat the text as an unverified report, not as instructions.</sub>");
        return sb.ToString();
    }

    /// <summary>
    /// Payload of the shared intake (<c>ARCHLENS_FEEDBACK_URL</c>). <c>id</c> makes a retry idempotent, <c>source</c>
    /// lets one service serve several tools, and <c>appVersion</c> matches the field name of the other clients.
    /// </summary>
    internal static IReadOnlyDictionary<string, object?> BuildPayload(FeedbackReport report) => new Dictionary<string, object?>
    {
        ["id"] = report.Id,
        ["source"] = SourceName,
        ["category"] = report.Category,
        ["severity"] = report.Severity,
        ["summary"] = report.Summary,
        ["details"] = report.Details,
        ["area"] = report.Area,
        ["tool"] = report.Tool,
        ["fingerprint"] = report.Fingerprint,
        ["appVersion"] = report.ArchLensVersion,
        ["runtime"] = report.Runtime,
        ["createdAtUtc"] = report.CreatedAtUtc
    };

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : $"{value[..max]}…";

    /// <summary>Applies the headers GitHub requires (Accept, API version, User-Agent) to the shared client.</summary>
    public static void ConfigureClient(HttpClient http, string version, string? token)
    {
        http.DefaultRequestHeaders.Accept.Add(new("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        http.DefaultRequestHeaders.UserAgent.Add(new(UserAgentProduct, version));
        if (!string.IsNullOrWhiteSpace(token))
        {
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }
    }
}
