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

    // ---- The tab protocol ----------------------------------------------------------------------
    // One method per operation intent. A caller states what the operation is — a browse of a URL,
    // a call routed by a ref, a ref-less call on the current tab, a call addressed to a tab by URL
    // — hands over the work to do on the page, and receives the work's result or a named wall. The
    // ordering (acquire or route, lock, re-check open under the lock, touch, read the URL before
    // the work, run the work, supersede per intent, note the final URL, commit the stamp lease,
    // override with the closed wall, tell a dead tab from a dead connection) lives here, once.

    // Bounded: each retry is a fresh acquisition, which at the cap evicts another tab — an
    // unbounded spin under adversarial concurrency would churn tabs whose refs the model holds.
    // Three misses in a row is a session replacing tabs faster than a browse can hold one, and
    // the retryable wall beats joining the churn.
    public const int AcquireRetryBound = 3;

    // Resolving a ref is an existence check, not a wait for something in flight: the element was
    // in the snapshot the agent is holding, so either it is still there or the page moved on.
    // Kept generous enough to ride out an in-progress re-render, far below operation timeouts.
    public const int RefResolutionTimeoutMs = 2_000;

    // Browsing a URL: reuse-or-open under the pool gate, with the bounded retry for a tab evicted
    // between acquisition and locking. A browse always supersedes the tab's refs — a same-URL
    // re-browse reloads the DOM, so the old numbers are no longer on the document.
    public async Task<TabOutcome<T>> BrowseAsync<T>(
        string sessionId,
        string url,
        IBrowserContext context,
        Func<TabWorkContext, Task<T>> work,
        CancellationToken ct = default)
    {
        foreach (var _ in Enumerable.Range(0, AcquireRetryBound))
        {
            var acquisition = await AcquireTabForBrowseAsync(sessionId, url, context, ct);
            var tabLock = await LockTabAsync(acquisition.Tab, ct);
            if (acquisition.Tab.Page.IsClosed)
            {
                // Evicted in the instant between acquisition and locking: the pool drops the dead
                // tab on the next acquisition and opens a fresh one.
                tabLock.Dispose();
                continue;
            }

            using (tabLock)
            {
                return await RunOnLockedTabAsync(
                    _sessions[sessionId], acquisition.Tab,
                    StampPolicy.Restamp(RefNamespace.Image), supersedeAlways: true, work, ct);
            }
        }

        return new TabOutcome<T>.AcquireExhausted();
    }

    // The shared pipeline under an already-held tab lock: touch, read the URL before the work, run
    // the work, decide supersede, note where the tab landed, commit the lease — and answer the
    // closed wall over anything the work said when the page died along the way.
    private async Task<TabOutcome<T>> RunOnLockedTabAsync<T>(
        BrowserSession session,
        BrowserTab tab,
        StampPolicy stamping,
        bool supersedeAlways,
        Func<TabWorkContext, Task<T>> work,
        CancellationToken ct)
    {
        Touch(session, tab);
        var ctx = new TabWorkContext(this, session, tab, stamping, SafeUrl(tab.Page), ct);
        try
        {
            T result;
            try
            {
                result = await work(ctx);
            }
            catch (Exception ex) when (IsConnectionClosed(ex) && tab.Page.IsClosed)
            {
                // One tab dying mid-work throws the same text as the whole connection dying; the
                // closed page says which it truly was, and the session's other tabs live on.
                return new TabOutcome<T>.Closed(tab.RequestedUrl);
            }

            if (tab.Page.IsClosed)
            {
                // The closed wall wins over the work's own answer: whatever the work produced, it
                // read a page that is gone.
                return new TabOutcome<T>.Closed(tab.RequestedUrl);
            }

            CommitTail(session, tab, ctx, supersedeAlways);
            return new TabOutcome<T>.Ran(result);
        }
        finally
        {
            ctx.ReleaseLease();
        }
    }

    private void CommitTail(BrowserSession session, BrowserTab tab, TabWorkContext ctx, bool supersedeAlways)
    {
        var finalUrl = SafeUrl(tab.Page);

        // Browse always supersedes; every other intent supersedes iff the URL moved across the
        // work — same-URL client-side re-renders deliberately keep refs alive (ADR-0034's "acting
        // does not unmake handles"). Superseding before the commit registers the work's fresh
        // range, so a restamp never kills its own numbers.
        if (supersedeAlways || finalUrl != ctx.UrlBefore)
        {
            session.SupersedeRangesOf(tab);
        }

        NoteFinalUrl(session.SessionId, tab, finalUrl);
        ctx.CommitLease();
    }

    // A "has been closed" out of Playwright is definitive proof a page, context or connection is
    // unusable; whether it was one tab or the whole connection is decided by Page.IsClosed at the
    // catch site.
    internal static bool IsConnectionClosed(Exception ex) =>
        ex is PlaywrightException &&
        (ex.Message.Contains("has been closed", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("Target closed", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("Connection closed", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("Browser closed", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase) ||
         ex.Message.Contains("WebSocket", StringComparison.OrdinalIgnoreCase));

    // Where a browse lands: a live tab whose address — asked-for or landed-on — matches exactly is
    // reloaded in place; anything else opens a new tab, closing the least-recently-touched one
    // when the pool is full. Pool mutation happens under the session's own gate, so two parallel
    // browses of one session get two tabs rather than racing into one, and one session's pool work
    // never queues behind another's.
    public async Task<TabAcquisition> AcquireTabForBrowseAsync(
        string sessionId, string url, IBrowserContext context, CancellationToken ct = default)
    {
        var session = await GetOrCreateAsync(sessionId, ct);

        await session.PoolGate.WaitAsync(ct);
        BrowserTab? evicted = null;
        try
        {
            session.DropClosedTabs();

            if (session.FindByUrl(url) is { } match)
            {
                // The address the model just asked with becomes the one the tab is known by, so
                // a later closed-wall names the URL the model most recently used.
                match.RequestedUrl = url;
                Touch(session, match);
                return new TabAcquisition(match, Reused: true);
            }

            // The page first, the eviction after: a transient failure to open one must not have
            // already killed an innocent tab and its refs.
            var page = await context.NewPageAsync();
            evicted = session.EvictLruIfAtCap(_tabCap);
            return new TabAcquisition(Admit(session, sessionId, page, url), Reused: false);
        }
        finally
        {
            session.PoolGate.Release();
            if (evicted is not null)
            {
                await CloseEvictedAsync(evicted);
            }
        }
    }

    // A page the site itself opened — target=_blank, window.open — is adopted into the pool
    // instead of leaking: it counts against the cap (evicting under it), becomes current, and is
    // left pending so the action whose click spawned it can answer from it.
    public async Task<BrowserTab?> AdoptPopupAsync(
        string sessionId, IPage popup, BrowserTab? opener = null, CancellationToken ct = default)
    {
        var session = _sessions.GetValueOrDefault(sessionId);
        if (session is null)
        {
            // Nothing owns the page and nothing will: close it rather than reviving the leak
            // adoption exists to close.
            if (!popup.IsClosed)
            {
                try
                {
                    await popup.CloseAsync();
                }
                catch (PlaywrightException)
                {
                    // Already gone.
                }
            }

            return null;
        }

        await session.PoolGate.WaitAsync(ct);
        BrowserTab? evicted = null;
        try
        {
            session.DropClosedTabs();
            evicted = session.EvictLruIfAtCap(_tabCap);
            var tab = Admit(session, sessionId, popup, SafeUrl(popup));

            // Pending on the tab whose click spawned it, so the popup answers that click's action
            // and no other — a session-global handoff let a parallel action on another tab swallow
            // it and claim its URL and snapshot as its own answer.
            if (opener is not null)
            {
                opener.PendingPopup = tab;
            }

            return tab;
        }
        finally
        {
            session.PoolGate.Release();
            if (evicted is not null)
            {
                await CloseEvictedAsync(evicted);
            }
        }
    }

    // An evicted tab may be mid-snapshot or mid-fetch; closing under its own lock lets the
    // in-flight call finish instead of having its page pulled out from under it. The pool gate is
    // already released here, so the gate-before-tab-lock order holds.
    private async Task CloseEvictedAsync(BrowserTab evicted)
    {
        using (await LockTabAsync(evicted))
        {
            await CloseTabPageAsync(evicted);
        }
    }

    private BrowserTab Admit(BrowserSession session, string sessionId, IPage page, string url)
    {
        var tab = new BrowserTab(page, url, _timeProvider.GetUtcNow());
        HandlePageEvents(sessionId, tab);
        session.AddTab(tab);
        Touch(session, tab);
        return tab;
    }

    private void HandlePageEvents(string sessionId, BrowserTab tab)
    {
        // Why: Playwright blocks the page until dialogs are handled
        tab.Page.Dialog += async (_, dialog) => await dialog.AcceptAsync();

        // A page this page opens joins the pool too — popups of popups included — so nothing the
        // site creates outlives the session unowned. The opening tab is the popup's opener, which
        // is what routes it to the action that spawned it.
        tab.Page.Popup += async (_, popup) =>
        {
            try
            {
                await AdoptPopupAsync(sessionId, popup, tab);
            }
            catch
            {
                // An adoption race with a dying session must not take the event loop down.
            }
        };
    }

    // The popup the tab's last adoption left for the action that spawned it, handed over exactly
    // once — and only to an action on the tab whose click opened it.
    public BrowserTab? TakePendingPopup(string sessionId, BrowserTab tab)
    {
        if (!_sessions.ContainsKey(sessionId))
        {
            return null;
        }

        var pending = tab.PendingPopup;
        tab.PendingPopup = null;
        return pending;
    }

    internal static string SafeUrl(IPage page)
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
        // A popup adopted before its navigation committed is known only as about:blank; once it
        // lands somewhere real, that address is the one its walls can name — "Browse about:blank
        // again" is a recovery nobody can use.
        if (tab.RequestedUrl == "about:blank" && url is not ("" or "about:blank"))
        {
            tab.RequestedUrl = url;
        }

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

        // supersede: a restamping pass replaced every number the tab held, so earlier ranges stop
        // routing. An additive pass — a non-navigating action stamping only what appeared —
        // leaves them routing, or a multi-step flow dies after its first action.
        public void Commit(int count, bool supersede = true)
        {
            if (_session is not { } session)
            {
                return;
            }

            session.Counters.Advance(_namespace, Start + count);
            if (_tab is not null && count > 0)
            {
                session.RegisterRange(_namespace, Start, Start + count - 1, _tab, supersede);
            }
            else if (_tab is not null && supersede)
            {
                // A restamp wiped every old stamp off the document before counting; finding
                // nothing to stamp does not put them back, so the old refs stop routing here too.
                session.SupersedeRangesOf(_namespace, _tab);
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
        // TryParse because the shape predicates accept any digit run: a number past int.MaxValue
        // is an unknown ref, not an exception out of routing.
        if (ElementRef.IsElementRef(refString)
            && int.TryParse(refString[ElementRef.Prefix.Length..], out var element))
        {
            return (RefNamespace.Element, element);
        }

        if (ImageRef.IsImageRef(refString)
            && int.TryParse(refString[ImageRef.Prefix.Length..], out var image))
        {
            return (RefNamespace.Image, image);
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
            foreach (var tab in session.Tabs)
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

        foreach (var tab in sessions.SelectMany(s => s.Tabs))
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

// What a run through the tab protocol answers: the work's result, or a named wall. One arm per
// way the protocol can refuse, so a caller maps walls to its own statuses in one switch and can
// never meet an unnamed refusal.
public abstract record TabOutcome<T>
{
    public sealed record Ran(T Result) : TabOutcome<T>;

    public sealed record NoSession : TabOutcome<T>;

    // The ref's tab is open; a later stamp renumbered its refs. Url is the address to browse or
    // snapshot for fresh ones.
    public sealed record Superseded(string Url) : TabOutcome<T>;

    // The tab is gone — evicted, or closed by the site. Url names the page to browse again.
    public sealed record Closed(string Url) : TabOutcome<T>;

    // The ref's range aged out of the bounded registry and no current tab remains to answer the
    // ordinary not-found.
    public sealed record Unknown : TabOutcome<T>;

    // The session replaced tabs faster than the acquisition could hold one — retryable, not an
    // error.
    public sealed record AcquireExhausted : TabOutcome<T>;

    // The tab addressed by URL (the composed-snapshot exception) is no longer in the session.
    public sealed record AddressedTabGone(string Url) : TabOutcome<T>;
}

// What an intent's work is allowed to stamp, and how a restamp differs from an additive pass. The
// namespace and mode travel here so the lease's supersede flag and the capture's augment choice
// cannot come apart.
public abstract record StampPolicy
{
    public static readonly StampPolicy None = new NoStamp();

    public static StampPolicy Restamp(RefNamespace ns) => new Restamping(ns);

    public static StampPolicy AugmentUnlessNavigated(RefNamespace ns) => new Augmenting(ns);

    internal sealed record NoStamp : StampPolicy;

    internal sealed record Restamping(RefNamespace Ns) : StampPolicy;

    internal sealed record Augmenting(RefNamespace Ns) : StampPolicy;

    internal RefNamespace? Namespace => this switch
    {
        Restamping r => r.Ns,
        Augmenting a => a.Ns,
        _ => null
    };
}

// What the work sees: the page, the address the tab held before the work, and — where the
// intent's stamping policy says so — the stamp start. The lease itself never crosses the seam:
// the module opens it when the work asks and commits it in the pipeline's tail, so a work
// callback cannot hold the counters wrong.
public sealed class TabWorkContext
{
    private readonly BrowserSessionManager _manager;
    private readonly BrowserSession _session;
    private readonly BrowserTab _tab;
    private readonly StampPolicy _stamping;
    private readonly CancellationToken _ct;
    private BrowserSessionManager.RefStampLease? _lease;
    private int _stampedCount;
    private bool? _augment;

    internal TabWorkContext(
        BrowserSessionManager manager,
        BrowserSession session,
        BrowserTab tab,
        StampPolicy stamping,
        string urlBefore,
        CancellationToken ct)
    {
        _manager = manager;
        _session = session;
        _tab = tab;
        _stamping = stamping;
        UrlBefore = urlBefore;
        _ct = ct;
    }

    // The raw Playwright page. Convention, not the type system, keeps it from escaping the lock.
    public IPage Page => _tab.Page;

    // The tab's address as it was when the work started, read under the lock — a navigation
    // queued ahead of this call already moved the tab, and a URL captured outside the queue would
    // compare against a page that is no longer there.
    public string UrlBefore { get; }

    // Whether a capture should augment (keep existing refs, number only what appeared) rather
    // than restamp. Latched on first read so the capture's choice and the lease's supersede flag
    // answer "did the work navigate" the same way.
    public bool AugmentRefs => _augment ??=
        _stamping is StampPolicy.Augmenting && BrowserSessionManager.SafeUrl(_tab.Page) == UrlBefore;

    // Runs the work's stamping script under the session's lease for this intent's namespace: the
    // script receives the start number and answers how many refs it wrote. The module commits the
    // count after the work, with the supersede flag the intent decided.
    public async Task StampAsync(Func<int, Task<int>> stamp)
    {
        if (_stamping.Namespace is not { } ns)
        {
            throw new InvalidOperationException("This intent's stamping policy is None.");
        }

        _lease = await _manager.BeginStampAsync(_session.SessionId, ns, _tab, _ct);
        _stampedCount = await stamp(_lease.Start);
    }

    internal void CommitLease()
    {
        if (_lease is null)
        {
            return;
        }

        _lease.Commit(_stampedCount, supersede: !AugmentRefs);
        _lease.Dispose();
        _lease = null;
    }

    internal void ReleaseLease()
    {
        _lease?.Dispose();
        _lease = null;
    }
}

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

    // The popup this tab's last click spawned, waiting for that click's action to answer from it.
    internal BrowserTab? PendingPopup { get; set; }

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

    // Serializes this session's pool mutation — create, reuse, evict, adopt — and nothing else's.
    internal SemaphoreSlim PoolGate { get; } = new(1, 1);

    // Mutated only under PoolGate, but read by routing and teardown on other threads, so every
    // access goes through _ranges' monitor — the pool gate alone cannot make a List safe to read
    // while it resizes.
    private readonly List<BrowserTab> _tabs = [];

    public IReadOnlyList<BrowserTab> Tabs
    {
        get
        {
            lock (_ranges)
            {
                return [.. _tabs];
            }
        }
    }

    internal void AddTab(BrowserTab tab)
    {
        lock (_ranges)
        {
            _tabs.Add(tab);
        }
    }

    internal BrowserTab? FindByUrl(string url)
    {
        lock (_ranges)
        {
            return _tabs.FirstOrDefault(t => t.RequestedUrl == url || t.FinalUrl == url);
        }
    }

    internal void DropClosedTabs()
    {
        lock (_ranges)
        {
            foreach (var dead in _tabs.Where(t => t.Page.IsClosed).ToList())
            {
                _tabs.Remove(dead);
                CloseRangesOf(dead);
            }

            if (CurrentTab is { Page.IsClosed: true })
            {
                CurrentTab = _tabs.LastOrDefault();
            }
        }
    }

    internal BrowserTab? EvictLruIfAtCap(int cap)
    {
        lock (_ranges)
        {
            if (_tabs.Count < cap)
            {
                return null;
            }

            var evicted = _tabs.MinBy(t => t.LastTouchedAt)!;
            _tabs.Remove(evicted);
            CloseRangesOf(evicted);
            return evicted;
        }
    }

    internal void RegisterRange(RefNamespace ns, int start, int end, BrowserTab tab, bool supersede = true)
    {
        lock (_ranges)
        {
            // A restamping pass supersedes everything that namespace stamped on the tab before —
            // the old numbers are no longer on the document. An additive pass leaves them be.
            foreach (var range in _ranges.Where(r =>
                         supersede && r.Tab == tab && r.Ns == ns && r.State == RefRangeState.Active))
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

    internal void SupersedeRangesOf(RefNamespace ns, BrowserTab tab)
    {
        lock (_ranges)
        {
            foreach (var range in _ranges.Where(r =>
                         r.Ns == ns && r.Tab == tab && r.State == RefRangeState.Active))
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
                { State: RefRangeState.Active } when _tabs.Contains(entry.Tab) && !entry.Tab.Page.IsClosed
                    => new RefRouting.Routed(entry.Tab),
                { State: RefRangeState.Superseded } when _tabs.Contains(entry.Tab)
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