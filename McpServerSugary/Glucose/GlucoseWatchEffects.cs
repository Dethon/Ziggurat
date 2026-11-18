using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;
using Domain.Tools.Sugary.Watches;
using Microsoft.Extensions.Logging;

namespace McpServerSugary.Glucose;

// A watch as it fired: whose sensor (and so which household), the watch as kept, and its spec as parsed.
public sealed record FiredGlucoseWatch(Account Subject, StoredGlucoseWatch Watch, GlucoseWatchSpec Spec);

// How many of a fire's effects reached somebody: a prompt an agent took, an announcement rung. A `once`
// watch is spent only by a fire that reached somebody.
public sealed record GlucoseEffectsOutcome(int Delivered)
{
    public static readonly GlucoseEffectsOutcome None = new(0);

    public bool AnyDelivered => Delivered > 0;
}

// What a watch does when one of its triggers fires: its prompt and its announcement (tickets 12 and 13).
// Throwing counts as nothing delivered, and is the engine's to contain.
public interface IGlucoseWatchEffects
{
    Task<GlucoseEffectsOutcome> FireAsync(FiredGlucoseWatch watch, GlucoseTriggerFire report, CancellationToken ct);
}

// Until the prompt and the announcement are wired, a fire is a line in the log that reached nobody, so a
// `once` watch stays armed.
public sealed class LoggedGlucoseWatchEffects(ILogger<LoggedGlucoseWatchEffects> logger) : IGlucoseWatchEffects
{
    public Task<GlucoseEffectsOutcome> FireAsync(FiredGlucoseWatch watch, GlucoseTriggerFire report, CancellationToken ct)
    {
        logger.LogInformation(
            "Glucose watch {WatchId} of household {HouseholdId} fired on trigger {TriggerIndex} ({Kind}) at {At}; no effect is wired yet",
            watch.Watch.Id, watch.Subject.TenantId, report.TriggerIndex, report.Trigger.Kind, report.At);
        return Task.FromResult(GlucoseEffectsOutcome.None);
    }
}