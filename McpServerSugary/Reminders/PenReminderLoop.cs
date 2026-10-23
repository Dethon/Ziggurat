using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.Reminders;

// Asks every quarter of an hour, so a reminder lands soon after the hour and a server that was down
// catches up soon after it is back.
public sealed class PenReminderLoop(PenReminders reminders, TimeProvider clock, ILogger<PenReminderLoop> logger)
    : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await reminders.RemindAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Sending pen reminders failed; retrying on the next tick");
            }
        }
    }
}