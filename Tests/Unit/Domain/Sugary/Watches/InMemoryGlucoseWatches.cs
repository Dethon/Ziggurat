using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;

namespace Tests.Unit.Domain.Sugary.Watches;

// The store as the contract says it behaves: each household's watches apart, a list kept to what the
// reader is shown, an id taken once per household, and a rewrite forgetting what was judged of it.
public sealed class InMemoryGlucoseWatches : IGlucoseWatches
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(string Tenant, string Id), StoredGlucoseWatch> _watches = [];
    private readonly Dictionary<(string Tenant, string Id), IReadOnlyList<GlucoseTriggerCondition>> _conditions = [];

    public StoredGlucoseWatch? Stored(string tenant, string id)
    {
        lock (_lock)
        {
            return _watches.GetValueOrDefault((tenant, id));
        }
    }

    public void Put(string tenant, StoredGlucoseWatch watch)
    {
        lock (_lock)
        {
            _watches[(tenant, watch.Id)] = watch;
        }
    }

    public void Judged(string tenant, string id, params GlucoseTriggerCondition[] conditions)
    {
        lock (_lock)
        {
            _conditions[(tenant, id)] = conditions;
        }
    }

    public Task<IReadOnlyList<StoredGlucoseWatch>> ListAsync(Account reader, bool everyone, CancellationToken ct)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<StoredGlucoseWatch>>(_watches
                .Where(w => w.Key.Tenant == reader.TenantId)
                .Select(w => w.Value)
                .Where(w => everyone || w.SubjectUserId == reader.UserId || w.WriterUserId == reader.UserId)
                .OrderBy(w => w.Id, StringComparer.Ordinal)
                .ToList());
        }
    }

    public Task<StoredGlucoseWatch?> FindAsync(Account reader, string id, CancellationToken ct) =>
        Task.FromResult(Stored(reader.TenantId, id));

    public Task<bool> AddAsync(Account writer, StoredGlucoseWatch watch, CancellationToken ct)
    {
        lock (_lock)
        {
            return Task.FromResult(_watches.TryAdd((writer.TenantId, watch.Id), watch));
        }
    }

    public Task<bool> ReplaceAsync(Account writer, StoredGlucoseWatch watch, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_watches.ContainsKey((writer.TenantId, watch.Id)))
            {
                return Task.FromResult(false);
            }

            _watches[(writer.TenantId, watch.Id)] = watch;
            _conditions.Remove((writer.TenantId, watch.Id));
            return Task.FromResult(true);
        }
    }

    public Task<bool> RemoveAsync(Account reader, string id, CancellationToken ct)
    {
        lock (_lock)
        {
            _conditions.Remove((reader.TenantId, id));
            return Task.FromResult(_watches.Remove((reader.TenantId, id)));
        }
    }

    public Task<IReadOnlyList<GlucoseTriggerCondition>> ConditionsAsync(Account reader, string id, CancellationToken ct)
    {
        lock (_lock)
        {
            return Task.FromResult(_conditions.GetValueOrDefault((reader.TenantId, id)) ?? []);
        }
    }
}