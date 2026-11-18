using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.Glucose;

// The clock's pass over everyone's glucose watches, once a minute: a link backing off is asked up to
// fifteen minutes apart and a refused one never, so a link failing for its `for`, or a sensor's end coming
// within its span, is judged here rather than after a poll that may not come.
public sealed class GlucoseWatchLoop(GlucoseWatchEngine engine, TimeProvider clock, ILogger<GlucoseWatchLoop> logger)
    : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await engine.TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Judging glucose watches on the clock failed; retrying on the next tick");
            }
        }
    }
}