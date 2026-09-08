using Domain.Contracts;
using Mcp.Hosting;
using McpServerScheduling.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServerScheduling.Services;

public sealed class ScheduleDispatcherService(
    IScheduleStore store,
    ICronValidator cronValidator,
    ChannelNotificationEmitter emitter,
    SchedulingSettings settings,
    ILogger<ScheduleDispatcherService> logger,
    TimeProvider timeProvider) : BackgroundService
{
    private bool _warnedUndelivered;

    // Liveness is only ever the return value of emitting (see CLAUDE.md) — never a separate
    // property to query. This is not that: it is the loop remembering the outcome of its own last
    // attempt, to decide how soon to try again. Six missed ticks against the schedule store while
    // nobody is listening, rather than one.
    internal const int IdleBackoffMultiplier = 6;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = ResolveInterval(settings.DispatchIntervalSeconds);
        while (!ct.IsCancellationRequested)
        {
            var delivered = true;
            try
            {
                delivered = await DispatchDueAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error dispatching due schedules");
            }

            try
            { await Task.Delay(NextDelay(interval, delivered), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Guard against a 0/negative config value: 0 makes Task.Delay return immediately (tight loop)
    // and a negative span throws, so floor the poll interval at one second.
    internal static TimeSpan ResolveInterval(int dispatchIntervalSeconds) =>
        TimeSpan.FromSeconds(Math.Max(1, dispatchIntervalSeconds));

    internal static TimeSpan NextDelay(TimeSpan interval, bool delivered) =>
        delivered ? interval : interval * IdleBackoffMultiplier;

    // Returns whether the dispatch needs no back-off: nothing was due, or every fire it tried to
    // hand off was actually delivered. False means at least one fire was refused for want of a
    // listener, which is the loop's cue to slow down.
    internal async Task<bool> DispatchDueAsync(CancellationToken ct)
    {
        var delivered = await DispatchDueSchedulesAsync(ct);
        if (delivered)
        {
            NoteDeliveryResumed();
        }

        return delivered;
    }

    private async Task<bool> DispatchDueSchedulesAsync(CancellationToken ct)
    {
        var now = timeProvider.GetUtcNow();
        var due = await store.GetDueSchedulesAsync(now.UtcDateTime, ct);
        var delivered = true;
        foreach (var schedule in due)
        {
            var nextRun = schedule.CronExpression is null
                ? null
                : cronValidator.GetNextOccurrence(schedule.CronExpression, now, timeProvider.LocalTimeZone);

            var plan = ScheduleFirePlanner.Plan(schedule, settings.Delivery.DefaultDeliverTo, nextRun, now);

            // Emit before mutating the store: if no active session receives the
            // notification, leave the schedule due so the next tick retries instead of
            // silently dropping the fire. (Trade-off: a store write that fails after a
            // successful emit can double-fire — at-least-once is the safer default here.)
            if (!await emitter.EmitAsync(plan.Payload, ct))
            {
                WarnUndeliveredOncePerOutage(schedule.Id);
                delivered = false;
                continue;
            }

            if (plan.DeleteAfterFire)
            {
                await store.DeleteAsync(schedule.Id, ct);
            }
            else
            {
                await store.UpdateLastRunAsync(schedule.Id, now.UtcDateTime, plan.NextRunAt, ct);
            }

            logger.LogInformation("Fired schedule {ScheduleId} for agent {AgentId}", schedule.Id, schedule.AgentId);
        }

        return delivered;
    }

    // One warning per outage rather than one per due schedule per tick: an overnight
    // disconnection with a single due schedule used to produce thousands of identical lines.
    // The retry semantics are untouched — the schedule stays due either way.
    private void WarnUndeliveredOncePerOutage(string scheduleId)
    {
        if (_warnedUndelivered)
        {
            return;
        }

        _warnedUndelivered = true;
        logger.LogWarning(
            "No active session received schedule {ScheduleId}; leaving due schedules for retry and muting this warning until delivery resumes",
            scheduleId);
    }

    // Cleared on any tick that left nothing waiting on a listener, not only on a successful emit:
    // the fire an outage held up is often gone by the time the agent returns, so waiting for a
    // delivery to unmute would leave the next outage silent.
    private void NoteDeliveryResumed()
    {
        if (_warnedUndelivered)
        {
            _warnedUndelivered = false;
            logger.LogInformation("Schedule delivery resumed; no fire is waiting on a listener");
        }
    }
}