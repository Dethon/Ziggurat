using Domain.Security.Signing;

namespace Infrastructure.Agents;

// Each (household, agent, person)'s read registry, kept while it is used: a built app reads on every
// page it shows, and building one dials every server the agent names. Kept as an open conversation's
// session is kept, and dropped as it is: every lease asks whether the household has since defined the
// agent otherwise or its servers changed (ReadRegistry.IsStaleAsync), and a stale one is rebuilt. One
// nobody has leased for AppReadSettings.RegistryIdle is let go, its clients with it.
//
// A registry is disposed only once nothing holds it: evicted while a read runs on it, it lasts until
// that read ends.
public sealed class ReadRegistryCache(AgentReadRegistries registries, TimeProvider clock, AppReadSettings settings)
    : IAsyncDisposable
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Key, Entry> _entries = [];

    // `viewer` is the identity the agent verified (AgentReadRegistries.BuildAsync's); it throws what the
    // build throws (an agent the household lacks, a viewer who is no person), keeping nothing.
    public async Task<ReadLease> LeaseAsync(IdentityClaims viewer, string agentId, CancellationToken ct)
    {
        var account = viewer.Account
                      ?? throw new ArgumentException("a registry is read by a person of a household", nameof(viewer));
        var key = new Key(account.TenantId, agentId, account.UserId);
        await SweepAsync();

        // A stale registry is rebuilt once: the build just made is as fresh as the household's answer.
        for (var rebuilt = false; ; rebuilt = true)
        {
            var (entry, made) = Take(key, () => registries.BuildAsync(viewer, agentId, CancellationToken.None));
            try
            {
                var registry = await entry.Build.Value.WaitAsync(ct);
                if (made || rebuilt || !await registry.IsStaleAsync(ct))
                {
                    return new ReadLease(registry, () => ReleaseAsync(entry, evict: false));
                }

                await ReleaseAsync(entry, evict: true);
            }
            catch
            {
                // A build that failed is never kept, so the next read builds again.
                await ReleaseAsync(entry, evict: entry.Build.Value.IsFaulted || entry.Build.Value.IsCanceled);
                throw;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<Entry> idle;
        lock (_gate)
        {
            foreach (var entry in _entries.Values)
            {
                entry.Evicted = true;
            }

            idle = [.. _entries.Values.Where(e => e.Leases == 0)];
            _entries.Clear();
        }

        foreach (var entry in idle)
        {
            await DisposeAsync(entry);
        }
    }

    private (Entry Entry, bool Made) Take(Key key, Func<Task<ReadRegistry>> build)
    {
        lock (_gate)
        {
            var made = !_entries.TryGetValue(key, out var entry);
            if (made)
            {
                entry = new Entry(key, new Lazy<Task<ReadRegistry>>(build));
                _entries[key] = entry;
            }

            entry!.Leases++;
            return (entry, made);
        }
    }

    private async ValueTask ReleaseAsync(Entry entry, bool evict)
    {
        bool dispose;
        lock (_gate)
        {
            entry.Leases--;
            entry.LastUsed = clock.GetUtcNow();
            if (evict)
            {
                entry.Evicted = true;
                if (_entries.GetValueOrDefault(entry.Key) == entry)
                {
                    _entries.Remove(entry.Key);
                }
            }

            dispose = entry.Evicted && entry.Leases == 0;
        }

        if (dispose)
        {
            await DisposeAsync(entry);
        }
    }

    private async Task SweepAsync()
    {
        List<Entry> idle;
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            idle = [.. _entries.Values.Where(e => e.Leases == 0 && now - e.LastUsed >= settings.RegistryIdle)];
            foreach (var entry in idle)
            {
                entry.Evicted = true;
                _entries.Remove(entry.Key);
            }
        }

        foreach (var entry in idle)
        {
            await DisposeAsync(entry);
        }
    }

    private static async ValueTask DisposeAsync(Entry entry)
    {
        if (entry.Build.IsValueCreated && entry.Build.Value.IsCompletedSuccessfully)
        {
            await entry.Build.Value.Result.DisposeAsync();
        }
    }

    private sealed record Key(string HouseholdId, string AgentId, string UserId);

    // Guarded by the cache's lock, all but the build.
    private sealed class Entry(Key key, Lazy<Task<ReadRegistry>> build)
    {
        public Key Key { get; } = key;
        public Lazy<Task<ReadRegistry>> Build { get; } = build;
        public int Leases { get; set; }
        public DateTimeOffset LastUsed { get; set; }
        public bool Evicted { get; set; }
    }
}

// One read's hold on a cached registry: disposing it lets the cache let the registry go.
public sealed class ReadLease(ReadRegistry registry, Func<ValueTask> release) : IAsyncDisposable
{
    public ReadRegistry Registry { get; } = registry;

    public ValueTask DisposeAsync() => release();
}

// How long a read registry nobody is reading through is kept. A generic tunable, the agent's alone.
public sealed record AppReadSettings
{
    public TimeSpan RegistryIdle { get; init; } = TimeSpan.FromMinutes(2);
}