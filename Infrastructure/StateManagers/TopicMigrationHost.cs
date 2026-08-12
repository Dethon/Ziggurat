using Domain.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.StateManagers;

// The migration runs from the Agent host and from nowhere else, so the topic index has one
// builder as well as one writer. Channels do not wait for it: a channel started against a store
// this has not reached serves an empty list until it finishes, which is the price of deleting
// the scan rather than keeping it as a fallback.
public sealed class TopicMigrationHost(
    IThreadStateStore store,
    ILogger<TopicMigrationHost> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await store.MigrateTopicsAsync(stoppingToken);
            logger.LogInformation("The topic index is built from the conversations already stored");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing retries this. A failed migration leaves the sidebar short of the topics it
            // never reached, which is visible and recoverable by restarting; taking the host down
            // over it would be worse.
            logger.LogError(ex, "Building the topic index from the stored conversations failed");
        }
    }
}