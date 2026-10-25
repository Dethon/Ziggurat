using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.Tools.Web;
using Infrastructure.StateManagers;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Browser;

// What each person is logged into and what of it outlives a restart. A handoff's sites become
// remembered or live-only when its Done says which; the person's context is saved then, and again
// shortly after it is used, so a cookie a site rotates is in the next restore. Every save leaves
// the person's live-only sites out, which is what keeps an unticked login off the disk. There is
// no expiry of our own: a remembered login lasts until it is forgotten or the site ends it.
public sealed class PersonLogins : IDisposable
{
    private readonly IPersonContexts _contexts;
    private readonly IPersonStateStore _store;
    private readonly TimeProvider _time;
    private readonly TimeSpan _debounce;
    private readonly ILogger<PersonLogins> _logger;
    private readonly ConcurrentDictionary<string, Person> _people = new();

    public PersonLogins(
        IPersonContexts contexts, IPersonStateStore store, TimeProvider time, TimeSpan debounce,
        ILogger<PersonLogins> logger)
    {
        _contexts = contexts;
        _store = store;
        _time = time;
        _debounce = debounce;
        _logger = logger;
        _contexts.PersonActivity += OnActivity;
        _contexts.ContextsClosed += OnContextsClosed;
    }

    // One person's in-memory half: the sites that live only until the browser restarts, the
    // remembered sites used since the last save, the handoffs open now, and the save on its way.
    private sealed class Person
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<string, LiveOnlySite> LiveOnly { get; } = [];
        public Dictionary<string, DateTimeOffset> Used { get; } = [];
        public int OpenHandoffs { get; set; }
        public ITimer? PendingSave { get; set; }
    }

    private sealed record LiveOnlySite(DateTimeOffset HandedOffAt, DateTimeOffset? LastUsedAt);

    public sealed record LoginSite(string Domain, bool Remembered, DateTimeOffset HandedOffAt, DateTimeOffset? LastUsedAt);

    public bool CanRemember => _store.CanWrite;

    // While a handoff is open the person's context holds a login half made; nothing is saved
    // until Done says whether to keep it.
    public void BeginHandoff(string person)
    {
        var entry = For(person);
        lock (entry)
        {
            entry.OpenHandoffs++;
        }
    }

    // A handoff nobody finished — out of time, or its tab lost: whatever it set in the person's
    // context stays there only until the browser restarts. Its sites are live-only unless the
    // person already remembered them, so no later save carries a half-made login.
    public async Task AbandonHandoffAsync(
        string person, IReadOnlyCollection<string> visitedHosts, CancellationToken ct = default)
    {
        var entry = For(person);
        EndHandoff(entry);
        if (visitedHosts.Count == 0)
        {
            return;
        }

        var remembered = (await _store.LoadAsync(person, ct))?.Sites.Select(s => s.Domain).ToHashSet() ?? [];
        var now = _time.GetUtcNow();
        lock (entry)
        {
            foreach (var site in visitedHosts.Select(RegistrableDomain.Of).Where(site => !remembered.Contains(site)))
            {
                entry.LiveOnly.TryAdd(site, new LiveOnlySite(now, null));
            }
        }
    }

    private static void EndHandoff(Person entry)
    {
        lock (entry)
        {
            entry.OpenHandoffs = Math.Max(0, entry.OpenHandoffs - 1);
        }
    }

    // The browser restarted: every live-only login went with its context.
    private void OnContextsClosed()
    {
        foreach (var entry in _people.Values)
        {
            lock (entry)
            {
                entry.LiveOnly.Clear();
                entry.Used.Clear();
            }
        }
    }

    public async Task CompleteHandoffAsync(
        string person, IReadOnlyCollection<string> visitedHosts, bool remember, CancellationToken ct = default)
    {
        var entry = For(person);
        var now = _time.GetUtcNow();
        HashSet<string> sites = [.. visitedHosts.Select(RegistrableDomain.Of)];
        EndHandoff(entry);

        // Saved ticked or not: an unticked Done scrubs from the record whatever a save made before
        // it may have caught of the login.
        await SaveAsync(person, entry, remembered =>
        {
            foreach (var site in sites)
            {
                if (remember && CanRemember)
                {
                    remembered[site] = new RememberedSite(site, now, null);
                    lock (entry)
                    {
                        entry.LiveOnly.Remove(site);
                    }
                }
                else
                {
                    remembered.Remove(site);
                    lock (entry)
                    {
                        entry.LiveOnly[site] = new LiveOnlySite(now, null);
                    }
                }
            }
        }, ct);
    }

    // Everything the person is logged into that the agent can reach: remembered, and live-only
    // until the browser restarts.
    public async Task<IReadOnlyList<LoginSite>> SitesAsync(string person, CancellationToken ct = default)
    {
        var entry = For(person);
        var record = await _store.LoadAsync(person, ct);
        List<LoginSite> sites;
        lock (entry)
        {
            sites =
            [
                .. (record?.Sites ?? []).Where(s => !entry.LiveOnly.ContainsKey(s.Domain))
                    .Select(s => new LoginSite(s.Domain, true, s.HandedOffAt,
                        entry.Used.TryGetValue(s.Domain, out var used) ? used : s.LastUsedAt)),
                .. entry.LiveOnly.Select(l => new LoginSite(l.Key, false, l.Value.HandedOffAt, l.Value.LastUsedAt))
            ];
        }

        return [.. sites.OrderBy(s => s.Domain, StringComparer.Ordinal)];
    }

    // The site's cookies and storage leave the person's live context — rebuilt from its state
    // without them, which closes the person's tabs — and the saved record, so the person is
    // logged out of it now and after a restart. Refused while one of their handoffs is open: the
    // rebuild would close the tab they are typing into.
    public async Task<bool> ForgetAsync(string person, string site, CancellationToken ct = default)
    {
        var entry = For(person);
        var domain = RegistrableDomain.Of(site);
        lock (entry)
        {
            if (entry.OpenHandoffs > 0)
            {
                return false;
            }
        }

        await entry.Gate.WaitAsync(ct);
        try
        {
            var record = await _store.LoadAsync(person, ct);
            HashSet<string> liveOnly;
            lock (entry)
            {
                entry.LiveOnly.Remove(domain);
                entry.Used.Remove(domain);
                liveOnly = [.. entry.LiveOnly.Keys];
            }

            var live = await _contexts.ExportStateAsync(person, ct);
            if (live is not null)
            {
                await _contexts.ReplaceStateAsync(person, StorageStateFilter.Without(live, [domain]), ct);
            }

            var state = live ?? record?.StorageState;
            if (CanRemember && state is not null)
            {
                await _store.SaveAsync(person, new PersonState(
                    StorageStateFilter.Without(state, [.. liveOnly, domain]),
                    [.. (record?.Sites ?? []).Where(s => s.Domain != domain)]), ct);
            }

            return true;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private void OnActivity(string person, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return;
        }

        var entry = For(person);
        var site = RegistrableDomain.Of(uri.Host);
        var now = _time.GetUtcNow();
        lock (entry)
        {
            if (entry.LiveOnly.TryGetValue(site, out var live))
            {
                entry.LiveOnly[site] = live with { LastUsedAt = now };
            }
            else
            {
                entry.Used[site] = now;
            }

            // At most one save on its way: steady traffic must not push it off for ever.
            if (!CanRemember || entry.OpenHandoffs > 0 || entry.PendingSave is not null)
            {
                return;
            }

            entry.PendingSave = _time.CreateTimer(_ => _ = SaveQuietlyAsync(person, entry), null, _debounce,
                Timeout.InfiniteTimeSpan);
        }
    }

    private async Task SaveQuietlyAsync(string person, Person entry)
    {
        try
        {
            await SaveAsync(person, entry, _ => { }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving the remembered logins of {Person} failed", person);
        }
    }

    // Under the person's gate: the record read, changed, and written back with the context's state
    // as it is now, live-only sites left out.
    private async Task SaveAsync(
        string person, Person entry, Action<Dictionary<string, RememberedSite>> change, CancellationToken ct)
    {
        await entry.Gate.WaitAsync(ct);
        try
        {
            lock (entry)
            {
                entry.PendingSave?.Dispose();
                entry.PendingSave = null;
            }

            var record = await _store.LoadAsync(person, ct);
            var remembered = (record?.Sites ?? []).ToDictionary(s => s.Domain);
            change(remembered);
            if (!CanRemember)
            {
                return;
            }

            Dictionary<string, DateTimeOffset> used;
            HashSet<string> liveOnly;
            lock (entry)
            {
                used = new Dictionary<string, DateTimeOffset>(entry.Used);
                entry.Used.Clear();
                liveOnly = [.. entry.LiveOnly.Keys];
            }

            foreach (var (site, at) in used.Where(u => remembered.ContainsKey(u.Key)))
            {
                remembered[site] = remembered[site] with { LastUsedAt = at };
            }

            var state = await _contexts.ExportStateAsync(person, ct) ?? record?.StorageState;
            if (state is null)
            {
                return;
            }

            await _store.SaveAsync(person,
                new PersonState(StorageStateFilter.Without(state, liveOnly), [.. remembered.Values]), ct);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private Person For(string person) => _people.GetOrAdd(person, _ => new Person());

    public void Dispose()
    {
        _contexts.PersonActivity -= OnActivity;
        _contexts.ContextsClosed -= OnContextsClosed;
        foreach (var entry in _people.Values)
        {
            entry.PendingSave?.Dispose();
        }
    }
}