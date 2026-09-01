using Infrastructure.Clients;

namespace Agent.App;

// The Lemonade chat host is asked what it has at startup and about every minute after, so a model
// loaded on the box reaches the menu without a restart, and a box switched off leaves it just as
// quickly. The refresh handles its own failures; this only keeps the loop alive.
public sealed class LemonadeModelRefresher(
    LemonadeModelDiscovery discovery,
    ILogger<LemonadeModelRefresher> logger) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                await discovery.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Refreshing the Lemonade chat host's models failed; retrying on the next tick");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}