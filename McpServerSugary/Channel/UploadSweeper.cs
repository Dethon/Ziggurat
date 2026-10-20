using Domain.DTOs;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.Channel;

// The only thing between the upload store and unbounded disk growth.
public sealed class UploadSweeper(UploadStore store, RetentionSettings retention, ILogger<UploadSweeper> logger)
    : BackgroundService
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
                        "Swept {Count} photos older than {Window} from the upload store",
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