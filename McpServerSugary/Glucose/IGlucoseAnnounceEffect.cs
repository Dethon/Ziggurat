using Domain.Tools.Sugary.Watches;

namespace McpServerSugary.Glucose;

// What a fired watch's announce effect does: rings its alarm, answering whether anything rang.
public interface IGlucoseAnnounceEffect
{
    Task<bool> RingAsync(FiredGlucoseWatch watch, GlucoseAnnounceEffect effect, GlucoseTriggerFire report, CancellationToken ct);
}