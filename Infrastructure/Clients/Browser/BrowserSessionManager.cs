using System.Collections.Concurrent;
using Domain.Tools.Web;
using Microsoft.Playwright;

namespace Infrastructure.Clients.Browser;

// The tab-pool policy in one place: which tab a browse lands on, which tab dies at the cap, which
// tab is current, and which numbers the stamping scripts may hand out. Everything here runs in
// milliseconds against faked pages; the browser above it only carries pages around.
public class BrowserSessionManager : IAsyncDisposable
{
    public const int DefaultTabCap = 3;

    private readonly ConcurrentDictionary<string, BrowserSession> _sessions = new();
    private readonly SemaphoreSlim _createLock = new(1, 1);
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _idleTimeout;
    private readonly int _tabCap;
    private readonly ITimer? _pruneTimer;

    public BrowserSessionManager(
        TimeProvider? timeProvider = null,
        TimeSpan? idleTimeout = null,
        TimeSpan? pruneInterval = null,
        int tabCap = DefaultTabCap)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(30);
        _tabCap = Math.Max(1, tabCap);

        if (pruneInterval is { } interval)
        {
            _pruneTimer = _timeProvider.CreateTimer(
                _ => _ = SafePruneAsync(),
                state: null,
                dueTime: interval,
                period: interval);
        }
    }

    private async Task SafePruneAsync()
    {
        try
        {
            await PruneIdleAsync();
        }
        catch
        {
            // Why: a single failure must not kill the periodic timer
        }
    }

    public async Task<BrowserSession> GetOrCreateAsync(string sessionId, CancellationToken ct = default)
    {
        if (_sessions.TryGetValue(sessionId, out var existing))
        {
            existing.LastAccessedAt = _timeProvider.GetUtcNow();
            return existing;
        }

        await _createLock.WaitAsync(ct);
        try
        {
            if (_sessions.TryGetValue(sessionId, out existing))
            {
                existing.LastAccessedAt = _timeProvider.GetUtcNow();
                return existing;
            }

            var session = new BrowserSession(sessionId, _timeProvider.GetUtcNow());
            _sessions[sessionId] = session;
            return session;
        }
        finally
        {
            _createLock.Release();
        }
    }

    public BrowserSession? Get(string sessionId)
    {
        return _sessions.GetValueOrDefault(sessionId);
    }

    // Where a browse lands: a live tab whose address — asked-for or landed-on — matches exactly is
    // reloaded in place; anything else opens a new tab, closing the least-recently-touched one
    // when the pool is full. Pool mutation happens under the create lock, so two parallel browses
    // of different URLs get two tabs rather than racing into one.
    public async Task<TabAcquisition> AcquireTabForBrowseAsync(
        string sessionId, string url, IBrowserContext context, CancellationToken ct = default)
    {
        var session = await GetOrCreateAsync(sessionId, ct);

        await _createLock.WaitAsync(ct);
        BrowserTab? evicted = null;
        try
        {
            session.DropClosedTabs();

            if (session.FindByUrl(url) is { } match)
            {
                Touch(session, match);
                return new TabAcquisition(match, Reused: true);
            }

            if (session.TabList.Count >= _tabCap)
            {
                evicted = session.TabList.MinBy(t => t.LastTouchedAt);
                session.TabList.Remove(evicted!);
                session.CloseRangesOf(evicted!);
            }

            var page = await context.NewPageAsync();
            HandlePageEvents(sessionId, page);

            var tab = new BrowserTab(page, url, _timeProvider.GetUtcNow());
            session.TabList.Add(tab);
            Touch(session, tab);
            return new TabAcquisition(tab, Reused: false);
        }
        finally
        {
            _createLock.Release();
            if (evicted is not null)
            {
                await CloseTabPageAsync(evicted);
            }
        }
    }

    // A page the site itself opened — target=_blank, window.open — is adopted into the pool
    // instead of leaking: it counts against the cap (evicting under it), becomes current, and is
    // left pending so the action whose click spawned it can answer from it.
    public async Task<BrowserTab?> AdoptPopupAsync(
        string sessionId, IPage popup, CancellationToken ct = default)
    {
        var session = _sessions.GetValueOrDefault(sessionId);
        if (session is null)
        {
            return null;
        }

        await _createLock.WaitAsync(ct);
        BrowserTab? evicted = null;
        try
        {
            session.DropClosedTabs();

            if (session.TabList.Count >= _tabCap)
            {
                evicted = session.TabList.MinBy(t => t.LastTouchedAt);
                session.TabList.Remove(evicted!);
                session.CloseRangesOf(evicted!);
            }

            HandlePageEvents(sessionId, popup);
            var tab = new BrowserTab(popup, SafeUrl(popup), _timeProvider.GetUtcNow());
            session.TabList.Add(tab);
            Touch(session, tab);
            session.PendingPopup = tab;
            return tab;
        }
        finally
        {
            _createLock.Release();
            if (evicted is not null)
            {
                await CloseTabPageAsync(evicted);
            }
        }
    }

    private void HandlePageEvents(string sessionId, IPage page)
    {
        // Why: Playwright blocks the page until dialogs are handled
        page.Dialog += async (_, dialog) => await dialog.AcceptAsync();

        // A page this page opens joins the pool too — popups of popups included — so nothing the
        // site creates outlives the session unowned.
        page.Popup += async (_, popup) =>
        {
            try
            {
                await AdoptPopupAsync(sessionId, popup);
            }
            catch
            {
                // An adoption race with a dying session must not take the event loop down.
            }
        };
    }

    // The popup the last adoption left for the action that spawned it, handed over exactly once.
    public BrowserTab? TakePendingPopup(string sessionId)
    {
        var session = _sessions.GetValueOrDefault(sessionId);
        if (session is null)
        {
            return null;
        }

        var pending = session.PendingPopup;
        session.PendingPopup = null;
        return pending;
    }

    private static string SafeUrl(IPage page)
    {
        try
        {
            return page.Url;
        }
        catch
        {
            return "about:blank";
        }
    }

    // Touch is any tool call routed to a tab, view_image included: it refreshes the tab's place in
    // the LRU order, makes it the session's current tab, and resets the session's one idle clock.
    public void TouchTab(string sessionId, BrowserTab tab)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            Touch(session, tab);
        }
    }

    private void Touch(BrowserSession session, BrowserTab tab)
    {
        var now = _timeProvider.GetUtcNow();
        tab.LastTouchedAt = now;
        session.CurrentTab = tab;
        session.LastAccessedAt = now;
    }

    // Where the tab actually landed, recorded beside the address it was asked for — redirects
    // split the two, and reuse matches either.
    public void NoteFinalUrl(string sessionId, BrowserTab tab, string url)
    {
        tab.FinalUrl = url;
        TouchTab(sessionId, tab);
    }

    // One tab, one operation at a time: navigation, action, snapshot and image fetch all take this,
    // so a mid-navigation read cannot answer with a half-replaced DOM. Different tabs of one
    // session do not serialize against each other; pool mutation has its own session-level gate.
    public async Task<IDisposable> LockTabAsync(BrowserTab tab, CancellationToken ct = default)
    {
        await tab.Gate.WaitAsync(ct);
        return new LockReleaser(tab.Gate);
    }

    private sealed class LockReleaser(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
        {
            // Why: a using-block disposes once, but guard against double-release inflating the count
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }

    // A stamp is a promise that the numbers written into the page are numbers nothing else in the
    // session will ever write again. The lease holds the session's stamping lock across the page
    // script, because the count is only known after the script has run — two stamps peeking the
    // same start would reissue every number the slower one writes. A stamp naming its tab also
    // records the committed range in the session's registry, which is what later routes each ref
    // back to the tab that stamped it.
    public async Task<RefStampLease> BeginStampAsync(
        string sessionId, RefNamespace ns, BrowserTab? tab = null, CancellationToken ct = default)
    {
        var session = _sessions.GetValueOrDefault(sessionId)
            ?? throw new InvalidOperationException($"Session '{sessionId}' not found");
        await session.Counters.StampLock(ns).WaitAsync(ct);
        return new RefStampLease(session, ns, tab);
    }

    public sealed class RefStampLease : IDisposable
    {
        private BrowserSession? _session;
        private readonly RefNamespace _namespace;
        private readonly BrowserTab? _tab;

        internal RefStampLease(BrowserSession session, RefNamespace ns, BrowserTab? tab)
        {
            _session = session;
            _namespace = ns;
            _tab = tab;
            Start = session.Counters.Next(ns);
        }

        public int Start { get; }

        public void Commit(int count)
        {
            if (_session is not { } session)
            {
                return;
            }

            session.Counters.Advance(_namespace, Start + count);
            if (_tab is not null && count > 0)
            {
                session.RegisterRange(_namespace, Start, Start + count - 1, _tab);
            }
        }

        public void Dispose() =>
            Interlocked.Exchange(ref _session, null)?.Counters.StampLock(_namespace).Release();
    }

    // Which tab a ref belongs to, or which wall it hits. Reads the session's registry: an Active
    // range whose tab is still in the pool routes; anything else names its recovery.
    public RefRouting RouteRef(string sessionId, string refString)
    {
        var session = _sessions.GetValueOrDefault(sessionId);
        if (session is null)
        {
            return new RefRouting.NoSession();
        }

        var parsed = ParseRef(refString);
        if (parsed is not var (ns, number))
        {
            return new RefRouting.Unknown();
        }

        return session.Route(ns, number);
    }

    private static (RefNamespace Ns, int Number)? ParseRef(string refString)
    {
        if (ElementRef.IsElementRef(refString))
        {
            return (RefNamespace.Element, int.Parse(refString[ElementRef.Prefix.Length..]));
        }

        if (ImageRef.IsImageRef(refString))
        {
            return (RefNamespace.Image, int.Parse(refString[ImageRef.Prefix.Length..]));
        }

        return null;
    }

    // An in-tab navigation replaced the document: every ref stamped on the old document is now
    // superseded — the tab is open, and snapshotting or browsing it again mints fresh ones.
    public void MarkTabNavigated(string sessionId, BrowserTab tab)
    {
        _sessions.GetValueOrDefault(sessionId)?.SupersedeRangesOf(tab);
    }

    public async Task CloseAsync(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            foreach (var tab in session.TabList.ToList())
            {
                await CloseTabPageAsync(tab);
            }
        }
    }

    public void Remove(string sessionId)
    {
        // Why: the session's pages are dead (the connection dropped). Closing them is pointless
        // and would throw, so just drop the references — a fresh web_browse recreates them.
        _sessions.TryRemove(sessionId, out _);
    }

    public void Clear()
    {
        // Why: when the underlying browser connection dies, every cached page points at a
        // dead context. Drop the references so the next access creates fresh pages on the
        // new context — closing the dead pages is pointless and would throw.
        _sessions.Clear();
    }

    public async Task PruneIdleAsync()
    {
        var cutoff = _timeProvider.GetUtcNow() - _idleTimeout;
        var idleIds = _sessions
            .Where(kv => kv.Value.LastAccessedAt < cutoff)
            .Select(kv => kv.Key);

        await Task.WhenAll(idleIds.Select(CloseAsync));
    }

    private static async Task CloseTabPageAsync(BrowserTab tab)
    {
        if (!tab.Page.IsClosed)
        {
            try
            {
                await tab.Page.CloseAsync();
            }
            catch (PlaywrightException)
            {
                // Best-effort: a page whose connection died is already gone.
            }
        }
    }

    private async Task CloseAllAsync()
    {
        var sessions = _sessions.Values.ToList();
        _sessions.Clear();

        foreach (var tab in sessions.SelectMany(s => s.TabList))
        {
            await CloseTabPageAsync(tab);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_pruneTimer is not null)
        {
            await _pruneTimer.DisposeAsync();
        }
        await CloseAllAsync();
        _createLock.Dispose();
        GC.SuppressFinalize(this);
    }
}

public record TabAcquisition(BrowserTab Tab, bool Reused);

// One live page in a session's pool. The model never sees it: a tab is addressed only through the
// refs it stamped, or by being the tab last touched.
public class BrowserTab(IPage page, string requestedUrl, DateTimeOffset createdAt)
{
    public IPage Page { get; } = page;

    // The address the model asked for, verbatim — the one it knows to ask for again.
    public string RequestedUrl { get; internal set; } = requestedUrl;

    // The address the page actually landed on, updated by every navigation on the tab.
    public string FinalUrl { get; internal set; } = requestedUrl;

    public DateTimeOffset LastTouchedAt { get; internal set; } = createdAt;

    // Serializes navigation, action, snapshot and image fetch on this one tab.
    internal SemaphoreSlim Gate { get; } = new(1, 1);
}

// One issued run of numbers: which tab stamped them, the address the model knows that tab by, and
// whether the run still resolves. A tombstone (Closed) outlives its tab so the refusal can name
// the page to browse again.
public enum RefRangeState
{
    Active,
    Superseded,
    Closed
}

public abstract record RefRouting
{
    public sealed record NoSession : RefRouting;

    public sealed record Routed(BrowserTab Tab) : RefRouting;

    // The tab is open; a later stamp renumbered its refs. Url is the address to browse (or
    // snapshot) for fresh ones.
    public sealed record Superseded(string Url) : RefRouting;

    // The tab is gone; Url is the address the ref belonged to.
    public sealed record Closed(string Url) : RefRouting;

    public sealed record Unknown : RefRouting;
}

public class BrowserSession(string sessionId, DateTimeOffset createdAt)
{
    // Bounded so one long conversation cannot grow the bookkeeping without limit. Oldest entries
    // fall off first; a ref whose entry fell off routes as unknown, which lands on the current
    // tab's ordinary not-found answer.
    private const int MaxRanges = 128;

    private sealed record RefRange(RefNamespace Ns, int Start, int End, BrowserTab Tab, string Url)
    {
        public RefRangeState State { get; set; } = RefRangeState.Active;
    }

    private readonly List<RefRange> _ranges = [];

    public string SessionId { get; } = sessionId;
    public DateTimeOffset CreatedAt { get; } = createdAt;
    public DateTimeOffset LastAccessedAt { get; internal set; } = createdAt;
    public BrowserTab? CurrentTab { get; internal set; }
    public SessionRefCounters Counters { get; } = new();

    // The popup the last adoption left behind, for the action whose click spawned it.
    internal BrowserTab? PendingPopup { get; set; }

    internal List<BrowserTab> TabList { get; } = [];

    public IReadOnlyList<BrowserTab> Tabs => TabList;

    internal BrowserTab? FindByUrl(string url) =>
        TabList.FirstOrDefault(t => t.RequestedUrl == url || t.FinalUrl == url);

    internal void DropClosedTabs()
    {
        foreach (var dead in TabList.Where(t => t.Page.IsClosed).ToList())
        {
            TabList.Remove(dead);
            CloseRangesOf(dead);
        }

        if (CurrentTab is { Page.IsClosed: true })
        {
            CurrentTab = TabList.LastOrDefault();
        }
    }

    internal void RegisterRange(RefNamespace ns, int start, int end, BrowserTab tab)
    {
        lock (_ranges)
        {
            // A fresh stamp on a tab supersedes everything that namespace stamped there before —
            // the old numbers are no longer on the document.
            foreach (var range in _ranges.Where(r =>
                         r.Tab == tab && r.Ns == ns && r.State == RefRangeState.Active))
            {
                range.State = RefRangeState.Superseded;
            }

            _ranges.Add(new RefRange(ns, start, end, tab, tab.RequestedUrl));
            if (_ranges.Count > MaxRanges)
            {
                _ranges.RemoveAt(0);
            }
        }
    }

    internal void SupersedeRangesOf(BrowserTab tab)
    {
        lock (_ranges)
        {
            foreach (var range in _ranges.Where(r =>
                         r.Tab == tab && r.State == RefRangeState.Active))
            {
                range.State = RefRangeState.Superseded;
            }
        }
    }

    internal void CloseRangesOf(BrowserTab tab)
    {
        lock (_ranges)
        {
            foreach (var range in _ranges.Where(r => r.Tab == tab))
            {
                range.State = RefRangeState.Closed;
            }
        }
    }

    internal RefRouting Route(RefNamespace ns, int number)
    {
        lock (_ranges)
        {
            // Newest first: ranges never overlap within a namespace, but the newest entry is the
            // one that says what the number means today.
            var entry = Enumerable.Reverse(_ranges)
                .FirstOrDefault(r => r.Ns == ns && r.Start <= number && number <= r.End);

            return entry switch
            {
                null => new RefRouting.Unknown(),
                { State: RefRangeState.Active } when TabList.Contains(entry.Tab) && !entry.Tab.Page.IsClosed
                    => new RefRouting.Routed(entry.Tab),
                { State: RefRangeState.Superseded } when TabList.Contains(entry.Tab)
                    => new RefRouting.Superseded(entry.Url),
                _ => new RefRouting.Closed(entry.Url)
            };
        }
    }
}

public enum RefNamespace
{
    Element,
    Image
}

// The session's two monotonic counters, one per ref namespace. Numbers start at 1 and only ever
// move forward; a number handed out is never handed out again within the session. Each namespace
// carries its own stamping lock — the counters are independent, and one lock across both would
// deadlock a caller stamping images while it still holds an element lease.
public class SessionRefCounters
{
    private readonly int[] _next = [1, 1];
    private readonly SemaphoreSlim[] _stampLocks = [new(1, 1), new(1, 1)];

    internal SemaphoreSlim StampLock(RefNamespace ns) => _stampLocks[(int)ns];

    internal int Next(RefNamespace ns) => _next[(int)ns];

    internal void Advance(RefNamespace ns, int to) =>
        _next[(int)ns] = Math.Max(_next[(int)ns], to);
}