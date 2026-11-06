using Domain.Contracts;
using Domain.DTOs.Voice;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServerTimers.Services;

// Polls the timer store once per second and rings each due timer as an insistent timer alert — on
// its household's satellites through its voice server when it names some, else on its owner's
// channels through the agent. No LLM in the fire path. Timers use a fixed tighter cadence than
// alarms and skip the volume ramp (a kitchen countdown must be heard on round 1; the ramp is for
// wake-up alarms).
public sealed class TimerFireService(
    ITimerStore store,
    IHouseholdVoices voices,
    IChannelAlerts alerts,
    TimeProvider time,
    ILogger<TimerFireService> logger) : BackgroundService
{
    private static readonly InsistentOptions _timerRing = new()
    {
        GapSeconds = 10, MaxRepeats = 12, RampStartPercent = 100
    };

    // Constructed eagerly (not inside ExecuteAsync) so the first tick's due-time is pinned relative to
    // `time` at service-construction, not whenever the base BackgroundService.StartAsync's Task.Run
    // happens to schedule ExecuteAsync onto a pool thread. PeriodicTimer(period, TimeProvider) registers
    // its ITimer with the provider in its own constructor, so a lazily-constructed timer under a
    // FakeTimeProvider can miss an Advance() that races ahead of that scheduling delay.
    private readonly PeriodicTimer _timer = new(TimeSpan.FromSeconds(1), time);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(ct))
            {
                var due = await store.TakeDueAsync(time.GetUtcNow().UtcDateTime, ct);
                foreach (var armed in due)
                {
                    await FireAsync(armed, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutting down.
        }
    }

    public override void Dispose()
    {
        _timer.Dispose();
        base.Dispose();
    }

    private async Task FireAsync(ArmedTimer armed, CancellationToken ct)
    {
        try
        {
            if (armed.Target is not { } target)
            {
                await RingOnChannelsAsync(armed, ct);
                return;
            }

            var rung = await voices.For(armed.TenantId).Announcer.StartAsync(new AnnounceRequest
            {
                Target = target,
                Text = armed.Text ?? $"{armed.Id} timer",
                Kind = AnnounceKind.Timer,
                Insistent = _timerRing
            }, ct);
            if (RangNowhere(rung))
            {
                // Every satellite it names is off: nothing would be heard. It rings where a timer with
                // no satellite rings, on its owner's channels (user's decision, 2026-11-06).
                logger.LogInformation("Timer {TimerId}'s satellites are all offline; ringing it on its owner's channels", armed.Id);
                await RingOnChannelsAsync(armed, ct);
                return;
            }

            logger.LogInformation("Timer {TimerId} fired", armed.Id);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // One attempt, then the timer is gone — TakeDueAsync already claimed it. Targets are
            // validated at create, so failing here means the hub was unreachable at the due second
            // (typically a hub redeploy overlapping it). Accepted trade, not an oversight:
            // retry/durability machinery isn't worth it for kitchen-scale countdowns on a
            // deliberately non-durable store. Guard on the host token, not the exception type:
            // whatever the announcer throws while the host is live (the HTTP adapter's typed
            // unavailable, a raw timeout from an in-process implementation) must not unwind the
            // poll loop — only real shutdown (ct cancelled) propagates.
            logger.LogWarning(ex, "Timer {TimerId} failed to ring", armed.Id);
        }
    }

    private static bool RangNowhere(AnnounceResponse rung) =>
        rung.Satellites.Count > 0 && rung.Satellites.All(s => s.Status == AnnouncementOutcome.Offline);

    // The same accepted trade as a satellite's: one attempt, and an agent that was not listening to
    // take it is a timer that did not ring.
    private async Task RingOnChannelsAsync(ArmedTimer armed, CancellationToken ct)
    {
        if (await alerts.RingAsync(armed, ct))
        {
            logger.LogInformation("Timer {TimerId} fired on its owner's channels", armed.Id);
        }
        else
        {
            logger.LogWarning("Timer {TimerId} failed to ring: no agent was listening to ring it on its owner's channels", armed.Id);
        }
    }
}