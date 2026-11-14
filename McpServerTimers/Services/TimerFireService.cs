using Domain.Contracts;
using Domain.DTOs.Voice;
using Domain.Exceptions;
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
        catch (VoiceHubUnavailableException ex) when (!ct.IsCancellationRequested)
        {
            // The voice server itself is not there to take it — a household's kit restarting, its house
            // without power, or the deployment's hub redeploying — so no satellite would be heard either:
            // it rings on its owner's channels like a timer whose satellites are all off (user's
            // decision, 2026-11-06, pass 19).
            logger.LogWarning("Timer {TimerId} could not reach its voice server ({Reason}); ringing it on its owner's channels",
                armed.Id, ex.Message);
            await RingOnChannelsAsync(armed, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // One attempt, then the timer is gone — TakeDueAsync already claimed it. An unreachable hub
            // rings on channels above; anything else failing here is the hub answering wrongly at the
            // due second. Accepted trade, not an oversight:
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