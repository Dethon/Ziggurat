using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.Tools.Web;
using Infrastructure.StateManagers;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Browser;

// What each person is logged into and what of it outlives a restart. A login is to the site a
// handoff was opened at, and covers every domain the handoff touched; Done says whether it is
// remembered or lives only until the browser restarts. A save is an allowlist — it writes only the
// cookies and storage of domains a remembered login covers — so whatever nobody chose to remember
// stays off the disk whichever host set it. The person's context is saved in full when a login is
// kept or forgotten, and its cookies alone shortly after it is used, so a cookie a site rotates is
// in the next restore. There is no expiry of our own: a remembered login lasts until it is
// forgotten or the site ends it.
public sealed class PersonLogins : ILoginSites, IDisposable
{
    private readonly IPersonContexts _contexts;
    private readonly IPersonStateStore _store;
    private readonly TimeProvider _time;
    private readonly TimeSpan _debounce;
    private readonly long _maxRecordBytes;
    private readonly ILogger<PersonLogins> _logger;
    private readonly ConcurrentDictionary<string, Person> _people = new();

    public PersonLogins(
        IPersonContexts contexts, IPersonStateStore store, TimeProvider time, TimeSpan debounce,
        long maxRecordBytes, ILogger<PersonLogins> logger)
    {
        _contexts = contexts;
        _store = store;
        _time = time;
        _debounce = debounce;
        _maxRecordBytes = maxRecordBytes;
        _logger = logger;
        _contexts.PersonActivity += OnActivity;
        _contexts.ContextsClosed += OnContextsClosed;
    }

    // One person's in-memory half: the logins that live only until the browser restarts, the
    // remembered sites used since the last save, the handoffs open now, and the save on its way.
    // Both dictionaries are keyed by site.
    private sealed class Person
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<string, SiteLogin> LiveOnly { get; } = [];
        public Dictionary<string, DateTimeOffset> Used { get; } = [];
        public HashSet<HandoffLease> Open { get; } = [];
        public ITimer? PendingSave { get; set; }
    }

    public sealed record LoginListing(
        string Site, bool Remembered, IReadOnlyList<string> Domains, DateTimeOffset HandedOffAt,
        DateTimeOffset? LastUsedAt);

    public bool CanRemember => _store.CanWrite;

    // Taken before the handoff's tab opens: the login page's own requests must not arm a save of a
    // login nobody has finished. The cookies the person holds now are what the handoff's end is
    // compared with; a person with no context yet will have one restored from their record, so
    // the record's cookies stand in — otherwise every restored cookie would read as the handoff's.
    public async Task<HandoffLease> BeginHandoffAsync(string person, string site, CancellationToken ct = default)
    {
        var before = await _contexts.ExportCookiesAsync(person, ct)
                     ?? (await _store.LoadAsync(person, ct))?.StorageState;
        var lease = new HandoffLease(this, person, SiteOf(site), CookieIdentity.In(before));
        var entry = For(person);
        lock (entry)
        {
            entry.Open.Add(lease);
        }

        return lease;
    }

    // Done ticked: the site's login is remembered, its domains merged with any it had, and a
    // live-only login of it is gone. Unticked: a remembered login of the site is scrubbed from the
    // record and the login lives only until the browser restarts. No other login is touched — a
    // provider this login shares with a remembered one stays remembered through that one.
    internal async Task CompleteAsync(
        HandoffLease lease, IReadOnlyCollection<string> visitedHosts, bool remember, CancellationToken ct)
    {
        var entry = Release(lease);
        var domains = await DomainsOfAsync(lease, visitedHosts, ct);
        var now = _time.GetUtcNow();
        var site = lease.Site;
        var keep = remember && CanRemember;

        // Only a kept login exports in full: its localStorage and IndexedDB are written now, once.
        // An unticked Done still writes, cookies alone, so a remembered login of the site that it
        // scrubs leaves the record — its stored origins with it, by the allowlist.
        await SaveAsync(lease.Person, entry, full: keep, remembered =>
        {
            if (keep)
            {
                SiteLogin? wasLiveOnly;
                lock (entry)
                {
                    entry.LiveOnly.Remove(site, out wasLiveOnly);
                }

                remembered[site] = Login(site, now, domains, remembered.GetValueOrDefault(site)?.Domains,
                    wasLiveOnly?.Domains);
                return;
            }

            remembered.Remove(site, out var scrubbed);
            lock (entry)
            {
                entry.LiveOnly[site] = Login(site, now, domains, scrubbed?.Domains,
                    entry.LiveOnly.GetValueOrDefault(site)?.Domains);
            }
        }, ct);
    }

    // A handoff nobody finished: whatever it set stays in the person's context only until the
    // browser restarts. Its login is live-only unless the site is already remembered, so no save
    // carries a half-made login; one that touched nothing — its tab never opened — is no login.
    internal async Task AbandonAsync(HandoffLease lease, IReadOnlyCollection<string> visitedHosts, CancellationToken ct)
    {
        var entry = Release(lease);
        var domains = await DomainsOfAsync(lease, visitedHosts, ct);
        if (domains.Count == 0)
        {
            return;
        }

        var remembered = await _store.LoadAsync(lease.Person, ct);
        if (remembered?.Logins.Any(login => login.Site == lease.Site) == true)
        {
            return;
        }

        lock (entry)
        {
            entry.LiveOnly[lease.Site] = Login(lease.Site, _time.GetUtcNow(), domains,
                entry.LiveOnly.GetValueOrDefault(lease.Site)?.Domains);
        }
    }

    // First thing on settling, before anything that can throw: the handoff is closed whatever the
    // rest of the settle does.
    private Person Release(HandoffLease lease)
    {
        var entry = For(lease.Person);
        lock (entry)
        {
            entry.Open.Remove(lease);
        }

        return entry;
    }

    // What the handoff touched: the registrable domains of the hosts its main frame went through,
    // and those of every cookie added or changed while it was open — an iframe's sign-in sets
    // cookies on hosts the main frame never visits. The site joins them when the login is made.
    private async Task<IReadOnlyCollection<string>> DomainsOfAsync(
        HandoffLease lease, IReadOnlyCollection<string> visitedHosts, CancellationToken ct)
    {
        var after = CookieIdentity.In(await _contexts.ExportCookiesAsync(lease.Person, ct));
        return
        [
            .. visitedHosts.Select(RegistrableDomain.Of)
                .Concat(after.Where(cookie => !lease.Before.Contains(cookie)).Select(cookie => RegistrableDomain.Of(cookie.Domain)))
                .Where(domain => domain.Length > 0)
                .Distinct()
        ];
    }

    private static SiteLogin Login(string site, DateTimeOffset at, params IEnumerable<string>?[] domains) =>
        new(site, [.. domains.SelectMany(d => d ?? []).Append(site).Distinct().Order(StringComparer.Ordinal)], at, null);

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

    // Everything the person is logged into that the agent can reach: remembered, and live-only
    // until the browser restarts.
    public async Task<IReadOnlyList<LoginListing>> LoginsAsync(string person, CancellationToken ct = default)
    {
        var entry = For(person);
        var record = await _store.LoadAsync(person, ct);
        lock (entry)
        {
            return
            [
                .. (record?.Logins ?? []).Where(login => !entry.LiveOnly.ContainsKey(login.Site))
                    .Select(login => new LoginListing(login.Site, true, login.Domains, login.HandedOffAt,
                        entry.Used.TryGetValue(login.Site, out var used) ? used : login.LastUsedAt))
                    .Concat(entry.LiveOnly.Values.Select(login =>
                        new LoginListing(login.Site, false, login.Domains, login.HandedOffAt, login.LastUsedAt)))
                    .OrderBy(login => login.Site, StringComparer.Ordinal)
            ];
        }
    }

    // Whether the person holds a login, remembered or live-only, that covers the site's domain —
    // the site it was handed off for, or one its handoff went through.
    public async Task<bool> HasLoginAsync(string person, string site, CancellationToken ct = default)
    {
        var domain = SiteOf(site);
        var entry = For(person);
        lock (entry)
        {
            if (entry.LiveOnly.Values.Any(login => Covers(login, domain)))
            {
                return true;
            }
        }

        return (await _store.LoadAsync(person, ct))?.Logins.Any(login => Covers(login, domain)) == true;
    }

    private static bool Covers(SiteLogin login, string domain) => login.Site == domain || login.Domains.Contains(domain);

    // The login leaves, and with it the cookies and storage of every domain no remaining login —
    // remembered or live-only — still covers: out of the live context, rebuilt without them (which
    // closes the person's tabs, so it is skipped when nothing is stripped), and out of the record,
    // so the person is logged out of it now and after a restart. A provider another login signs in
    // through stays. Refused while one of their handoffs is open: the rebuild would close the tab
    // they are typing into.
    public async Task<bool> ForgetAsync(string person, string site, CancellationToken ct = default)
    {
        var entry = For(person);
        var target = SiteOf(site);
        await entry.Gate.WaitAsync(ct);
        try
        {
            List<SiteLogin> liveOnly;
            SiteLogin? liveForgotten;
            lock (entry)
            {
                if (entry.Open.Count > 0)
                {
                    return false;
                }

                entry.LiveOnly.Remove(target, out liveForgotten);
                entry.Used.Remove(target);
                liveOnly = [.. entry.LiveOnly.Values];
            }

            var record = await _store.LoadAsync(person, ct);
            var remembered = (record?.Logins ?? []).Where(login => login.Site != target).ToList();
            var forgotten = (record?.Logins ?? []).Where(login => login.Site == target)
                .SelectMany(login => login.Domains).Concat(liveForgotten?.Domains ?? []).Append(target);
            HashSet<string> stillCovered = [.. remembered.Concat(liveOnly).SelectMany(login => login.Domains)];
            List<string> strip = [.. forgotten.Distinct().Where(domain => !stillCovered.Contains(domain))];

            var live = await _contexts.ExportStateAsync(person, ct);
            if (live is not null && strip.Count > 0)
            {
                await _contexts.ReplaceStateAsync(person, StorageStateFilter.Without(live, strip), ct);
            }

            var state = live ?? record?.StorageState;
            if (CanRemember && record is not null && state is not null)
            {
                await WriteAsync(person, state, remembered, ct);
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
        // A login is used when a request goes to its own site. Its other domains — a provider it
        // signs in through — are shared with other logins and say nothing about this one.
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
            if (!CanRemember || entry.Open.Count > 0 || entry.PendingSave is not null)
            {
                return;
            }

            entry.PendingSave = _time.CreateTimer(_ => _ = SaveQuietlyAsync(person, entry), null, _debounce,
                Timeout.InfiniteTimeSpan);
        }
    }

    private async Task SaveQuietlyAsync(string person, Person entry)
    {
        // A handoff opened while this save waited: Done decides what of it to keep.
        lock (entry)
        {
            if (entry.Open.Count > 0)
            {
                entry.PendingSave?.Dispose();
                entry.PendingSave = null;
                return;
            }
        }

        try
        {
            await SaveAsync(person, entry, full: false, _ => { }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving the remembered logins of {Person} failed", person);
        }
    }

    // Under the person's gate: the record read, its logins changed, and written back with the
    // context's state as it is now, kept to what the remembered logins cover. A full save exports
    // the context's storage too; any other takes its cookies alone over the stored origins — the
    // save after use exists only so a rotated cookie survives a restart, and exporting a mail
    // client's IndexedDB on every browse would rewrite megabytes for it. A person who had nothing
    // remembered and has nothing remembered now has nothing to write.
    private async Task SaveAsync(
        string person, Person entry, bool full, Action<Dictionary<string, SiteLogin>> change, CancellationToken ct)
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
            var remembered = (record?.Logins ?? []).ToDictionary(login => login.Site);
            change(remembered);
            if (!CanRemember || ((record?.Logins.Count ?? 0) == 0 && remembered.Count == 0))
            {
                return;
            }

            Dictionary<string, DateTimeOffset> used;
            lock (entry)
            {
                used = new Dictionary<string, DateTimeOffset>(entry.Used);
                entry.Used.Clear();
            }

            foreach (var (site, at) in used.Where(u => remembered.ContainsKey(u.Key)))
            {
                remembered[site] = remembered[site] with { LastUsedAt = at };
            }

            var stored = record?.StorageState ?? EmptyState;
            var state = (full ? await _contexts.ExportStateAsync(person, ct) : null)
                        ?? (await _contexts.ExportCookiesAsync(person, ct) is { } cookies
                            ? StorageStateFilter.WithCookiesOf(stored, cookies)
                            : stored);
            await WriteAsync(person, state, [.. remembered.Values.OrderBy(login => login.Site, StringComparer.Ordinal)], ct);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    // The record as written: kept to the remembered logins, and under the size a record may take.
    private async Task WriteAsync(string person, string state, IReadOnlyList<SiteLogin> logins, CancellationToken ct)
    {
        var capped = StorageStateFilter.Capped(Kept(state, logins), _maxRecordBytes);
        if (capped.Dropped != StorageStateFilter.Dropped.Nothing)
        {
            _logger.LogWarning(
                "The remembered logins of {Person} exceed {MaxBytes} bytes; saved without {Dropped}, cookies kept",
                person, _maxRecordBytes, capped.Dropped);
        }

        await _store.SaveAsync(person, new PersonState(capped.State, logins), ct);
    }

    // Only what a remembered login covers is written. A domain a remembered login shares with a
    // live-only one — a provider both sign in through — is written, because the remembered login
    // needs it; the live-only login's own site is not.
    private static string Kept(string state, IEnumerable<SiteLogin> remembered) =>
        StorageStateFilter.Only(state, [.. remembered.SelectMany(login => login.Domains).Distinct()]);

    private const string EmptyState = """{"cookies":[],"origins":[]}""";

    // A site as a url or a host: its registrable domain.
    private static string SiteOf(string site) =>
        RegistrableDomain.Of(Uri.TryCreate(site, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : site);

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