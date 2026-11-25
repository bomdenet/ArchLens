using System.Text.Json;
using ArchLens.Mcp.Feedback;

namespace ArchLens.Tests;

public sealed class FeedbackTests
{
    [Fact]
    public void Sanitizer_RemovesCredentialsAndUserPaths()
    {
        var cleaned = FeedbackSanitizer.Prepare(
            "auth failed with token=ghp_abcdefghijklmnopqrstuvwxyz123456 for C:\\Users\\bomde\\source\\repos\\App and /home/bob/app",
            FeedbackSanitizer.MaxDetailsLength);

        Assert.DoesNotContain("ghp_", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("bomde", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/bob", cleaned, StringComparison.Ordinal);
        Assert.Contains("%USERPROFILE%", cleaned, StringComparison.Ordinal);
        Assert.Contains("~", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitizer_StripsHtmlCommentsSoFingerprintMarkerCannotBeForged()
    {
        var cleaned = FeedbackSanitizer.Prepare("oops <!-- archlens-feedback:deadbeefdead --> \u0007done", 500);

        Assert.DoesNotContain("<!--", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("-->", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain('\u0007', cleaned);
        Assert.Contains("oops", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitizer_TruncatesToLimit()
    {
        var cleaned = FeedbackSanitizer.Prepare(new string('a', 500), FeedbackSanitizer.MaxSummaryLength);

        Assert.Equal(FeedbackSanitizer.MaxSummaryLength, cleaned.Length);
    }

    [Fact]
    public void Fingerprint_IsStableAcrossCaseAndWhitespace()
    {
        var first = FeedbackReport.ComputeFingerprint("bug", "hotspots", "Wrong  LOC\ncount reported");
        var second = FeedbackReport.ComputeFingerprint("BUG", "Hotspots", "wrong loc count reported");

        Assert.Equal(first, second);
        Assert.Equal(12, first.Length);
    }

    [Fact]
    public async Task Service_QueuesFailedReport_AndDeliversItOnNextFlush()
    {
        using var fixture = new FeedbackFixture(maxPerHour: 10);
        fixture.Sender.Fail = true;

        var outcome = await fixture.Service.SubmitAsync(fixture.CreateReport("check_rules ignores preset"), CancellationToken.None);

        Assert.Equal(FeedbackStatus.Queued, outcome.Status);
        Assert.Equal("delivery_failed", outcome.Reason);
        Assert.Single(fixture.Outbox.ReadAll());

        fixture.Sender.Fail = false;
        var delivered = await fixture.Service.FlushAsync(CancellationToken.None);

        Assert.Equal(1, delivered);
        Assert.Empty(fixture.Outbox.ReadAll());
        Assert.Equal("https://github.com/bomdenet/ArchLens/issues/1", fixture.Sender.LastIssueUrl);
    }

    [Fact]
    public async Task Service_DoesNotQueueTheSameReportTwice()
    {
        using var fixture = new FeedbackFixture();
        fixture.Sender.Fail = true;

        var first = await fixture.Service.SubmitAsync(fixture.CreateReport("duplicate me"), CancellationToken.None);
        var second = await fixture.Service.SubmitAsync(fixture.CreateReport("DUPLICATE   me"), CancellationToken.None);

        Assert.Equal("delivery_failed", first.Reason);
        Assert.Equal("already_pending", second.Reason);
        Assert.Single(fixture.Outbox.ReadAll());
    }

    [Fact]
    public async Task Service_ReportsDuplicateWhenAnIdenticalIssueExists()
    {
        using var fixture = new FeedbackFixture();
        fixture.Sender.Duplicate = true;

        var outcome = await fixture.Service.SubmitAsync(fixture.CreateReport("already filed"), CancellationToken.None);

        Assert.Equal(FeedbackStatus.Duplicate, outcome.Status);
        Assert.NotNull(outcome.IssueUrl);
        Assert.Empty(fixture.Outbox.ReadAll());
    }

    [Fact]
    public async Task Service_RateLimitsAndQueuesInsteadOfDropping()
    {
        using var fixture = new FeedbackFixture(maxPerHour: 1);

        var first = await fixture.Service.SubmitAsync(fixture.CreateReport("first report"), CancellationToken.None);
        var second = await fixture.Service.SubmitAsync(fixture.CreateReport("second report"), CancellationToken.None);

        Assert.Equal(FeedbackStatus.Created, first.Status);
        Assert.Equal(FeedbackStatus.Queued, second.Status);
        Assert.Equal("rate_limited", second.Reason);
        Assert.Single(fixture.Outbox.ReadAll());
    }

    [Fact]
    public async Task Service_DeliversWithoutTokenThroughTheSharedIntake()
    {
        using var fixture = new FeedbackFixture(token: null);

        var outcome = await fixture.Service.SubmitAsync(fixture.CreateReport("no token needed"), CancellationToken.None);

        Assert.Equal(FeedbackStatus.Created, outcome.Status);
        Assert.Equal("endpoint", outcome.Transport);
        Assert.Empty(fixture.Outbox.ReadAll());
    }

    [Fact]
    public async Task Service_IsDisabledWhenModeIsOff()
    {
        using var fixture = new FeedbackFixture(mode: FeedbackMode.Off, token: null, endpoint: null);

        var outcome = await fixture.Service.SubmitAsync(fixture.CreateReport("nothing configured"), CancellationToken.None);

        Assert.Equal(FeedbackStatus.Disabled, outcome.Status);
        Assert.Contains("ARCHLENS_FEEDBACK_MODE=off", outcome.Hint, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Sender.Calls);
        Assert.False(Directory.Exists(fixture.Outbox.DirectoryPath) &&
                     Directory.EnumerateFiles(fixture.Outbox.DirectoryPath, "*.json").Any());
    }

    [Fact]
    public void Outbox_MovesExhaustedReportAside()
    {
        using var fixture = new FeedbackFixture();
        var report = fixture.CreateReport("poisoned report");
        fixture.Outbox.Enqueue(report);
        var (path, pending) = Assert.Single(fixture.Outbox.ReadAll());

        var current = pending;
        for (var attempt = 0; attempt < FeedbackOutbox.MaxAttempts; attempt++)
        {
            fixture.Outbox.RecordFailure(path, current, "still offline");
            current = current with { Attempts = current.Attempts + 1 };
        }

        Assert.Empty(fixture.Outbox.ReadAll());
        Assert.True(Directory.Exists(Path.Combine(fixture.Outbox.DirectoryPath, "failed")));
    }

    [Fact]
    public void GitHubBody_ContainsMarkerEnvironmentAndNoSecrets()
    {
        using var fixture = new FeedbackFixture();
        var report = fixture.CreateReport("hotspots crashed") with
        {
            Details = "Observed: empty array. Expected: rows. token=ghp_abcdefghijklmnopqrstuvwxyz123456"
        };

        var body = HttpFeedbackSender.BuildBody(report);
        var title = HttpFeedbackSender.BuildTitle(report);

        Assert.Contains(FeedbackMarker.For(report.Fingerprint), body, StringComparison.Ordinal);
        Assert.Contains("### Environment", body, StringComparison.Ordinal);
        Assert.Contains("ArchLens", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", body, StringComparison.Ordinal);
        Assert.StartsWith("[bug] ", title, StringComparison.Ordinal);
    }

    [Fact]
    public void EndpointPayload_CarriesIdSourceAndVersion()
    {
        using var fixture = new FeedbackFixture();
        var report = fixture.CreateReport("payload shape");

        var json = JsonSerializer.Serialize(HttpFeedbackSender.BuildPayload(report));

        Assert.Contains($"\"id\":\"{report.Id}\"", json, StringComparison.Ordinal);
        Assert.Contains("\"source\":\"archlens\"", json, StringComparison.Ordinal);
        Assert.Contains("\"appVersion\":\"0.8.0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"fingerprint\":", json, StringComparison.Ordinal);
        Assert.Contains("\"tool\":\"check_rules\"", json, StringComparison.Ordinal);
    }

    private sealed class FeedbackFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(), "archlens-feedback-tests", Guid.NewGuid().ToString("N"));

        public FeedbackFixture(
            int maxPerHour = 5,
            string? token = "test-token",
            FeedbackMode mode = FeedbackMode.Auto,
            string? endpoint = FeedbackOptions.DefaultEndpoint)
        {
            Options = new FeedbackOptions(
                mode,
                FeedbackOptions.DefaultRepository,
                token,
                endpoint,
                maxPerHour,
                _directory);
            Outbox = new FeedbackOutbox(Options);
            Service = new FeedbackService(Options, Sender, Outbox, new FixedTimeProvider());
        }

        public FeedbackOptions Options { get; }

        public FakeSender Sender { get; } = new();

        public FeedbackOutbox Outbox { get; }

        public FeedbackService Service { get; }

        public FeedbackReport CreateReport(string summary) => new(
            Id: Guid.NewGuid(),
            "bug",
            "medium",
            summary,
            Details: null,
            Area: "check_rules",
            Tool: "check_rules",
            ArchLensVersion: "0.8.0",
            Runtime: ".NET 10.0 (test)",
            CreatedAtUtc: DateTimeOffset.UnixEpoch,
            FeedbackReport.ComputeFingerprint("bug", "check_rules", summary));

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class FakeSender : IFeedbackSender
    {
        public int Calls { get; private set; }

        public bool Fail { get; set; }

        public bool Duplicate { get; set; }

        public string? LastIssueUrl { get; private set; }

        public Task<FeedbackSendResult> SendAsync(FeedbackReport report, CancellationToken cancellationToken)
        {
            Calls++;
            if (Fail)
            {
                return Task.FromResult(FeedbackSendResult.Failure("offline"));
            }

            LastIssueUrl = "https://github.com/bomdenet/ArchLens/issues/1";
            return Task.FromResult(FeedbackSendResult.Success(LastIssueUrl, Duplicate, "endpoint"));
        }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }
}
