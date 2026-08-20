using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.Monitor;

namespace Agent.App;

// Keeps the monitor running, and is the only thing that decides what a monitor that stopped means.
//
// It used to call it in a bare while loop: a dependency that refused instantly was retried
// instantly, for as long as it kept refusing, with the one log line that said why buried under its
// own repetitions. Every ending is now paid for with a wait that doubles, and the wait is only
// forgiven by a run that lasted.
public class ChatMonitoring(
    ChatMonitor monitor,
    IMetricsPublisher metricsPublisher,
    TimeProvider timeProvider,
    ILogger<ChatMonitoring> logger,
    // The schedule rather than the policy, because it is stateful — it is the thing that knows how
    // many restarts are behind the current delay — and because a test that cannot hold the jitter
    // still cannot assert a wait.
    MonitorRestartSchedule? restartSchedule = null) : BackgroundService
{
    private readonly MonitorRestartSchedule _schedule =
        restartSchedule ?? new MonitorRestartSchedule(new MonitorRestartPolicy());

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var startedAt = timeProvider.GetTimestamp();
            Exception? fault = null;

            try
            {
                await monitor.Monitor(cancellationToken);
            }
            // Shutdown, from anywhere inside the monitor or its channels. It is the ordinary way
            // this service ends and it is neither a fault nor a restart.
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (MonitorFault.IsFatal(ex))
            {
                Report(MonitorFault.FatalErrorType, MonitorFault.Describe(ex));
                logger.LogCritical(
                    ex,
                    "The chat monitor failed with an error no restart can clear; stopping the host so " +
                    "the fault is visible rather than retried");
                throw;
            }
            catch (Exception ex)
            {
                fault = ex;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (!await BackOffAsync(fault, timeProvider.GetElapsedTime(startedAt), cancellationToken))
            {
                return;
            }
        }
    }

    // False when the wait was cut short by shutdown, which is the one case where the loop must not
    // start the monitor again.
    private async Task<bool> BackOffAsync(Exception? fault, TimeSpan ranFor, CancellationToken ct)
    {
        var delay = _schedule.NextDelay(ranFor);
        var ending = fault is null ? "its channels ended" : MonitorFault.Describe(fault);

        Report(
            MonitorFault.RestartErrorType,
            $"restart {_schedule.Attempt} in {delay.TotalSeconds:0.##}s after running " +
            $"{ranFor.TotalSeconds:0.##}s ({ending})");
        logger.LogWarning(
            "The chat monitor ran for {RanForSeconds:0.##}s and stopped ({Ending}); restart " +
            "{Attempt} in {DelaySeconds:0.##}s",
            ranFor.TotalSeconds, ending, _schedule.Attempt, delay.TotalSeconds);

        try
        {
            await Task.Delay(delay, timeProvider, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void Report(string errorType, string message) =>
        metricsPublisher.Publish(new ErrorEvent
        {
            Service = "agent",
            ErrorType = errorType,
            Message = message
        });
}