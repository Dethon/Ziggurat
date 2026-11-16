using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.DTOs.Voice;

namespace Infrastructure.Timers;

// Timers are deliberately non-durable (kitchen-scale countdowns; spec defers durability), so a
// process-local map is the whole store. Keyed by household and id together: an id is the model's to
// choose, and two households' "pasta" timers are two timers.
public sealed class InMemoryTimerStore : ITimerStore
{
    private readonly ConcurrentDictionary<(string TenantId, string Id), ArmedTimer> _timers = new();

    public Task ArmAsync(ArmedTimer timer, CancellationToken ct = default)
    {
        _timers[(timer.TenantId, timer.Id)] = timer;
        return Task.CompletedTask;
    }

    public Task<ArmedTimer?> GetAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(_timers.GetValueOrDefault((tenantId, id)));

    public Task<IReadOnlyList<ArmedTimer>> ListAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ArmedTimer>>(
            _timers.Values
                .Where(t => t.TenantId == tenantId)
                .OrderBy(t => t.FiresAtUtc)
                .ThenBy(t => t.Id, StringComparer.Ordinal)
                .ToList());

    public Task<bool> CancelAsync(string tenantId, string id, CancellationToken ct = default) =>
        Task.FromResult(_timers.TryRemove((tenantId, id), out _));

    public Task<IReadOnlyList<ArmedTimer>> TakeDueAsync(DateTime asOfUtc, CancellationToken ct = default)
    {
        var due = _timers.Values
            .Where(t => t.FiresAtUtc <= asOfUtc)
            .OrderBy(t => t.FiresAtUtc)
            .Where(t => _timers.TryRemove((t.TenantId, t.Id), out _)) // atomic claim per timer
            .ToList();
        return Task.FromResult<IReadOnlyList<ArmedTimer>>(due);
    }
}