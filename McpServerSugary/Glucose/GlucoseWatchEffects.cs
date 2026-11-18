using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;
using Domain.Tools.Sugary.Watches;
using Mcp.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.Glucose;

// A watch as it fired: whose sensor (and so which household), the watch as kept, and its spec as parsed;
// and, for what the fire says, the household's clock the engine judged it on and the unit the person
// reads glucose in.
public sealed record FiredGlucoseWatch(Account Subject, StoredGlucoseWatch Watch, GlucoseWatchSpec Spec)
{
    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Utc;
    public GlucoseUnit Unit { get; init; } = GlucoseUnit.Mgdl;
}

// How many of a fire's effects reached somebody: a prompt an agent took, an announcement rung. A `once`
// watch is spent only by a fire that reached somebody.
public sealed record GlucoseEffectsOutcome(int Delivered)
{
    public static readonly GlucoseEffectsOutcome None = new(0);

    public bool AnyDelivered => Delivered > 0;
}

// What a watch does when one of its triggers fires: its prompts and its announcements, in order.
// Throwing counts as nothing delivered, and is the engine's to contain.
public interface IGlucoseWatchEffects
{
    Task<GlucoseEffectsOutcome> FireAsync(FiredGlucoseWatch watch, GlucoseTriggerFire report, CancellationToken ct);
}

// One announcement of a fired watch rung: on the household's satellites, or on the people's own channels
// when there is none to ring on. True when it rang somewhere.
public interface IGlucoseAnnounceEffect
{
    Task<bool> RingAsync(FiredGlucoseWatch watch, GlucoseAnnounceEffect effect, GlucoseTriggerFire report, CancellationToken ct);
}

// Until announcements are wired (ticket 13), one is a line in the log that rang nowhere.
public sealed class UnwiredGlucoseAnnounce(ILogger<UnwiredGlucoseAnnounce> logger) : IGlucoseAnnounceEffect
{
    public Task<bool> RingAsync(FiredGlucoseWatch watch, GlucoseAnnounceEffect effect, GlucoseTriggerFire report, CancellationToken ct)
    {
        logger.LogInformation(
            "Glucose watch {WatchId} of household {HouseholdId} would announce on trigger {TriggerIndex}; announcements are not wired yet",
            watch.Watch.Id, watch.Subject.TenantId, report.TriggerIndex);
        return Task.FromResult(false);
    }
}

// The effects as a fire runs them, in the order written. A prompt is put on Sugary's channel for the agent
// as an unprompted fire (GlucoseWatchFire) and counts when an agent was polling to take it — gate-on-live,
// so one nobody took is not kept to arrive late, and its `once` watch stays armed; the engine never waits
// on the turn itself, which runs on the agent's time. An announcement is the announce seam's, and one that
// throws is logged and the effects after it still run.
public sealed class GlucoseWatchEffects(
    ChannelNotificationEmitter emitter,
    IGlucoseAnnounceEffect announce,
    DeliverySettings delivery,
    TimeProvider clock,
    ILogger<GlucoseWatchEffects> logger) : IGlucoseWatchEffects
{
    public async Task<GlucoseEffectsOutcome> FireAsync(FiredGlucoseWatch watch, GlucoseTriggerFire report, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var delivered = 0;
        // A loop, not a query: each effect is awaited in turn, in the order the watch says them.
        foreach (var (effect, index) in watch.Spec.Effects.Select((e, i) => (e, i)))
        {
            delivered += effect switch
            {
                GlucosePromptEffect prompt => await PromptAsync(watch, prompt, index, report, now, ct) ? 1 : 0,
                GlucoseAnnounceEffect announcement => await AnnounceAsync(watch, announcement, report, ct) ? 1 : 0,
                _ => 0
            };
        }

        return new GlucoseEffectsOutcome(delivered);
    }

    private async Task<bool> PromptAsync(
        FiredGlucoseWatch watch, GlucosePromptEffect prompt, int index, GlucoseTriggerFire report, DateTimeOffset now,
        CancellationToken ct)
    {
        var fire = GlucoseWatchFire.Compose(watch, prompt, index, report, delivery.DefaultDeliverTo, now);
        if (await emitter.EmitAsync(fire, ct))
        {
            logger.LogInformation("Glucose watch {WatchId} of household {HouseholdId} put its prompt {EffectIndex} before agent {AgentId}",
                watch.Watch.Id, watch.Subject.TenantId, index, watch.Watch.AgentId);
            return true;
        }

        logger.LogWarning(
            "Glucose watch {WatchId} of household {HouseholdId} fired on trigger {TriggerIndex}, and no agent was listening for its prompt {EffectIndex}",
            watch.Watch.Id, watch.Subject.TenantId, report.TriggerIndex, index);
        return false;
    }

    private async Task<bool> AnnounceAsync(
        FiredGlucoseWatch watch, GlucoseAnnounceEffect announcement, GlucoseTriggerFire report, CancellationToken ct)
    {
        try
        {
            return await announce.RingAsync(watch, announcement, report, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Glucose watch {WatchId} of household {HouseholdId} failed to announce on trigger {TriggerIndex}",
                watch.Watch.Id, watch.Subject.TenantId, report.TriggerIndex);
            return false;
        }
    }
}