using Domain.DTOs;

namespace Domain.Contracts;

// What the store answers when asked what is out there: the registrations still live, and the names
// it was holding that have gone since the last read. A lapsed name is reported once and then
// forgotten, which is what lets an expiry be published as an event without anything watching for
// one — there is no reaper, no sweep and no timer on the hub (docs/adr/0027).
public sealed record OutpostSnapshot(
    IReadOnlyList<OutpostRegistration> Live,
    IReadOnlyList<string> Lapsed);

// The keyed, expiring store behind the registry. It owns nothing but storage: how long an entry
// lives is the registry's decision and arrives as an argument, so the two can be reasoned about
// separately and the registry can be driven without a container.
public interface IOutpostStore
{
    Task SetAsync(OutpostRegistration registration, TimeSpan expiry, CancellationToken ct = default);

    // The refreshed registration, or null where there was nothing to refresh: the machine went
    // quiet long enough to lapse and is only now asking again, which is a re-registration rather
    // than a keepalive. It comes back whole because the keepalive's answer carries the verdict the
    // hub wrote onto it, which is the only channel back to the machine.
    Task<OutpostRegistration?> RefreshAsync(string name, TimeSpan expiry, CancellationToken ct = default);

    // Writes the verdict onto a live registration without touching its lifetime: the machine is
    // keeping itself alive on its own schedule, and the hub learning something about it is not a
    // reason to extend it.
    Task RecordVerdictAsync(string name, OutpostVerdict verdict, CancellationToken ct = default);

    Task<bool> RemoveAsync(string name, CancellationToken ct = default);

    Task<OutpostSnapshot> ReadAsync(CancellationToken ct = default);
}