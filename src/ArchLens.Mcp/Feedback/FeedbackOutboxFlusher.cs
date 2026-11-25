using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ArchLens.Mcp.Feedback;

/// <summary>
/// Retries reports left in the outbox by an earlier run (offline, expired token, GitHub outage). Runs once shortly
/// after start, because an MCP stdio server lives only as long as the client session and there is no later tick.
/// </summary>
public sealed class FeedbackOutboxFlusher(FeedbackService feedback, ILogger<FeedbackOutboxFlusher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken).ConfigureAwait(false);
            var delivered = await feedback.FlushAsync(stoppingToken).ConfigureAwait(false);
            if (delivered > 0)
            {
                logger.LogInformation("Delivered {Count} queued ArchLens feedback report(s).", delivered);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Server shut down before the flush window; the outbox keeps the reports for the next start.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not flush queued ArchLens feedback.");
        }
    }
}
