using Domain.DTOs;

namespace McpChannelSignalR.Attachments;

// The channel server holds bytes it does not otherwise deal in, so this sweep is the only thing
// standing between attachments and unbounded disk growth (ADR 0021). It collects everything topic
// deletion never reaches: conversations nobody deletes, and files uploaded for a message that was
// abandoned before it was sent.
public sealed class AttachmentSweeper(
    AttachmentStore store,
    RetentionSettings retention,
    ILogger<AttachmentSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                var swept = store.Sweep();
                if (swept > 0)
                {
                    logger.LogInformation(
                        "Swept {Count} attachments older than {Window} from the upload store",
                        swept, retention.AttachmentRetention);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Sweeping the upload store failed; retrying on the next tick");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}