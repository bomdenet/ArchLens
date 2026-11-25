namespace ArchLens.Mcp.Feedback;

/// <summary>Statuses reported back to the agent; the tool surfaces them verbatim.</summary>
public static class FeedbackStatus
{
    public const string Created = "created";
    public const string Duplicate = "duplicate";
    public const string Queued = "queued";
    public const string Disabled = "disabled";
}

public sealed record FeedbackOutcome(
    string Status,
    string Fingerprint,
    string? IssueUrl = null,
    string? Reason = null,
    string? Error = null,
    string? Hint = null,
    string? Transport = null);

/// <summary>
/// Decides what happens to a report: deliver now, queue for a later start, or explain why nothing was sent.
/// Everything here is best-effort by design — a feedback tool that can fail an analysis is worse than no tool.
/// </summary>
public sealed class FeedbackService
{
    private readonly FeedbackOptions _options;
    private readonly IFeedbackSender _sender;
    private readonly FeedbackOutbox _outbox;
    private readonly FeedbackRateLimiter _limiter;

    public FeedbackService(
        FeedbackOptions options,
        IFeedbackSender sender,
        FeedbackOutbox outbox,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _sender = sender;
        _outbox = outbox;
        _limiter = new FeedbackRateLimiter(
            Path.Combine(options.OutboxDirectory, "rate-limit.json"),
            options.MaxPerHour,
            timeProvider ?? TimeProvider.System);
    }

    public bool IsEnabled => _options.Mode != FeedbackMode.Off && (_options.Mode == FeedbackMode.Outbox || _options.CanDeliver);

    public string Repository => _options.Repository;

    public async Task<FeedbackOutcome> SubmitAsync(FeedbackReport report, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            return new FeedbackOutcome(
                FeedbackStatus.Disabled,
                report.Fingerprint,
                Hint: _options.Mode == FeedbackMode.Off
                    ? "Feedback is disabled by ARCHLENS_FEEDBACK_MODE=off."
                    : "Feedback is not configured: set ARCHLENS_FEEDBACK_TOKEN (GitHub PAT with issues:write), " +
                      "GITHUB_TOKEN, or ARCHLENS_FEEDBACK_URL (proxy endpoint).");
        }

        if (_outbox.Contains(report.Fingerprint))
        {
            return new FeedbackOutcome(FeedbackStatus.Queued, report.Fingerprint, Reason: "already_pending");
        }

        if (_options.Mode == FeedbackMode.Outbox || !_options.CanDeliver)
        {
            _outbox.Enqueue(report);
            return new FeedbackOutcome(FeedbackStatus.Queued, report.Fingerprint, Reason: "outbox_mode", Transport: "outbox");
        }

        if (!_limiter.TryAcquire())
        {
            _outbox.Enqueue(report, "rate_limited");
            return new FeedbackOutcome(FeedbackStatus.Queued, report.Fingerprint, Reason: "rate_limited", Transport: "outbox");
        }

        var result = await _sender.SendAsync(report, cancellationToken).ConfigureAwait(false);
        if (result.Delivered)
        {
            return new FeedbackOutcome(
                result.Duplicate ? FeedbackStatus.Duplicate : FeedbackStatus.Created,
                report.Fingerprint,
                IssueUrl: result.IssueUrl,
                Hint: result.Duplicate ? "An identical report already exists; it was not duplicated." : null,
                Transport: result.Transport);
        }

        _outbox.Enqueue(report, result.Error);
        return new FeedbackOutcome(
            FeedbackStatus.Queued,
            report.Fingerprint,
            Reason: "delivery_failed",
            Error: result.Error,
            Transport: "outbox");
    }

    /// <summary>
    /// Retries queued reports. Called at server start and after a successful interactive submit; returns how many
    /// reports were delivered so the log line (stderr) stays informative without being chatty.
    /// </summary>
    public async Task<int> FlushAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled || !_options.CanDeliver)
        {
            return 0;
        }

        var delivered = 0;
        foreach (var (path, pending) in _outbox.ReadAll())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var result = await _sender.SendAsync(pending.Report, cancellationToken).ConfigureAwait(false);
            if (result.Delivered)
            {
                _outbox.Remove(path);
                delivered++;
                continue;
            }

            _outbox.RecordFailure(path, pending, result.Error ?? "delivery_failed");
        }

        return delivered;
    }
}
