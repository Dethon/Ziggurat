using Infrastructure.Clients;

namespace Agent.App;

// The provider is asked what each model accepts at startup and again every hour, so a model that
// gains image support is picked up without a restart. Channels see the change through the
// registration they already have: the connection re-registers a catalog that no longer matches
// the one it last sent.
public sealed class ModelCapabilityRefresher(
    OpenRouterModelCapabilities capabilities,
    ILogger<ModelCapabilityRefresher> logger) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        do
        {
            try
            {
                await capabilities.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // RefreshAsync already keeps the last values that worked; this is only about not
                // ending the loop over a failure it has already handled.
                logger.LogWarning(ex, "Refreshing model capabilities failed; retrying on the next tick");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}