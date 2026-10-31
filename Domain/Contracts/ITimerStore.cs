using Domain.DTOs.Voice;

namespace Domain.Contracts;

// Every timer is a household's, so everything but the fire loop's claim is asked by household.
public interface ITimerStore
{
    Task ArmAsync(ArmedTimer timer, CancellationToken ct = default);
    Task<ArmedTimer?> GetAsync(string tenantId, string id, CancellationToken ct = default);
    Task<IReadOnlyList<ArmedTimer>> ListAsync(string tenantId, CancellationToken ct = default);
    Task<bool> CancelAsync(string tenantId, string id, CancellationToken ct = default);
    // Atomically removes and returns every timer due as of asOfUtc, every household's (fire-once semantics).
    Task<IReadOnlyList<ArmedTimer>> TakeDueAsync(DateTime asOfUtc, CancellationToken ct = default);
}