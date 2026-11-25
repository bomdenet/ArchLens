using System.Globalization;
using System.Text.Json;

namespace ArchLens.Mcp.Feedback;

/// <summary>Report waiting for delivery, plus the retry bookkeeping needed to give up eventually.</summary>
public sealed record PendingFeedback(FeedbackReport Report, int Attempts, string? LastError)
{
    public bool Exhausted(int maxAttempts) => Attempts >= maxAttempts;
}

/// <summary>
/// Durable local queue for reports that could not be delivered. One file per report keeps the format debuggable and
/// makes deduplication trivial; a corrupt file is skipped rather than breaking the tool.
/// </summary>
public sealed class FeedbackOutbox(FeedbackOptions options)
{
    public const int MaxAttempts = 5;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly FeedbackOptions _options = options;
    private readonly object _gate = new();

    public string DirectoryPath => _options.OutboxDirectory;

    /// <summary>Adds a report unless an identical fingerprint is already pending. Returns false when deduplicated.</summary>
    public bool Enqueue(FeedbackReport report, string? lastError = null)
    {
        lock (_gate)
        {
            Directory.CreateDirectory(DirectoryPath);
            if (FindByFingerprint(report.Fingerprint) is not null)
            {
                return false;
            }

            var fileName = string.Create(
                CultureInfo.InvariantCulture,
                $"{report.CreatedAtUtc:yyyyMMddTHHmmssfff}-{report.Fingerprint}.json");
            Write(Path.Combine(DirectoryPath, fileName), new PendingFeedback(report, 0, lastError));
            return true;
        }
    }

    public bool Contains(string fingerprint)
    {
        lock (_gate)
        {
            return FindByFingerprint(fingerprint) is not null;
        }
    }

    /// <summary>Reads every pending report, skipping unreadable files instead of failing the whole flush.</summary>
    public IReadOnlyList<(string Path, PendingFeedback Pending)> ReadAll()
    {
        lock (_gate)
        {
            if (!Directory.Exists(DirectoryPath))
            {
                return [];
            }

            var items = new List<(string, PendingFeedback)>();
            foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
            {
                if (TryRead(path) is { } pending)
                {
                    items.Add((path, pending));
                }
            }

            return items;
        }
    }

    /// <summary>Records a failed attempt; an exhausted report is moved aside so it stops consuming rate limit slots.</summary>
    public void RecordFailure(string path, PendingFeedback pending, string error)
    {
        lock (_gate)
        {
            var updated = pending with { Attempts = pending.Attempts + 1, LastError = error };
            if (updated.Exhausted(MaxAttempts))
            {
                MoveToFailed(path);
                return;
            }

            Write(path, updated);
        }
    }

    public void Remove(string path)
    {
        lock (_gate)
        {
            TryDelete(path);
        }
    }

    private string? FindByFingerprint(string fingerprint)
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return null;
        }

        foreach (var path in Directory.EnumerateFiles(DirectoryPath, $"*-{fingerprint}.json"))
        {
            return path;
        }

        return null;
    }

    private PendingFeedback? TryRead(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<PendingFeedback>(File.ReadAllText(path), JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Write(string path, PendingFeedback pending)
    {
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(pending, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Feedback must never break analysis; the caller reports the queue state, not an I/O stack trace.
        }
    }

    private void MoveToFailed(string path)
    {
        try
        {
            var failedDir = Path.Combine(DirectoryPath, "failed");
            Directory.CreateDirectory(failedDir);
            File.Move(path, Path.Combine(failedDir, Path.GetFileName(path)), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing actionable: the next flush retries the same file.
        }
    }
}

/// <summary>
/// Sliding one-hour window stored next to the outbox so the limit survives server restarts. Without it a looping
/// agent could open dozens of issues from one machine before a human notices.
/// </summary>
internal sealed class FeedbackRateLimiter(string filePath, int maxPerHour, TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _filePath = filePath;
    private readonly int _maxPerHour = maxPerHour;
    private readonly TimeProvider _time = timeProvider;
    private readonly object _gate = new();

    public bool TryAcquire()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var windowStart = now.AddHours(-1);
            var recent = Load().Where(stamp => stamp > windowStart).ToList();
            if (recent.Count >= _maxPerHour)
            {
                return false;
            }

            recent.Add(now);
            Save(recent);
            return true;
        }
    }

    private List<DateTimeOffset> Load()
    {
        try
        {
            return File.Exists(_filePath)
                ? JsonSerializer.Deserialize<List<DateTimeOffset>>(File.ReadAllText(_filePath), JsonOptions) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Save(List<DateTimeOffset> stamps)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(stamps, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unwritable limiter degrades to "no limit" instead of blocking feedback.
        }
    }
}
