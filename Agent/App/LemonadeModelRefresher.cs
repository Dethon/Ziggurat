using Infrastructure.Agents.ChatClients;
using Infrastructure.Clients;

namespace Agent.App;

// The Lemonade boxes are asked what they have at startup and about every minute after — the
// deployment's (the home's) and every household's own — so a model loaded on a box reaches its
// household's menu without a restart, and a box switched off, or one an owner removed, leaves it just
// as quickly. Each refresh handles its own failures; this only keeps the loop alive.
public sealed class LemonadeModelRefresher(
    HouseholdLemonadeHosts households,
    ILogger<LemonadeModelRefresher> logger,
    LemonadeModelDiscovery? deployment = null) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                if (deployment is not null)
                {
                    await deployment.RefreshAsync(stoppingToken);
                }

                await households.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Refreshing the Lemonade boxes' models failed; retrying on the next tick");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}