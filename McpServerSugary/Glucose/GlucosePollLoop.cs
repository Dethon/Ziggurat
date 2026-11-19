using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.Glucose;

// Asks the poller every few seconds which links are due; each link keeps its own moment of the period
// (GlucosePoller), so the tick only bounds how late a poll can be. A sweep is not waited for before the
// next tick: a link LibreLinkUp is slow to answer holds its own poll, never anyone else's.
public sealed class GlucosePollLoop(GlucosePoller poller, TimeProvider clock, ILogger<GlucosePollLoop> logger)
    : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sweeps = new List<Task>();
        try
        {
            using var timer = new PeriodicTimer(Tick, clock);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                sweeps.RemoveAll(sweep => sweep.IsCompleted);
                sweeps.Add(SweepAsync(stoppingToken));
            }
        }
        finally
        {
            await Task.WhenAll(sweeps);
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        try
        {
            await poller.PollDueAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Polling LibreLinkUp failed; retrying on the next tick");
        }
    }
}