using System.Diagnostics;
using Domain.Contracts;
using Infrastructure.Clients.Browser;

namespace Tests.Integration.Fixtures;

// One collection's view of a Camoufox. The container and the browser server live in
// CamoufoxBackend and are refcounted across every collection naming the same pool; a fixture
// instance owns nothing of its own.
public class PlaywrightWebBrowserFixture : IAsyncLifetime
{
    private CamoufoxBackend? _backend;

    protected virtual string Pool => CamoufoxBackend.SharedPool;

    private CamoufoxBackend Backend =>
        _backend ?? throw new InvalidOperationException("Backend not acquired. Call InitializeAsync first.");

    public PlaywrightWebBrowser Browser => Backend.Browser;
    public bool IsAvailable => true;
    public string? InitializationError => null;

    // The Camoufox WebSocket the Browser is connected to. Exposed so tests that need a raw
    // Playwright connection (e.g. verifying the server survives a hostile page) can reach the
    // same backend without standing up their own container.
    public string? WsEndpoint => _backend?.WsEndpoint;

    public async Task InitializeAsync() => _backend = await CamoufoxBackend.AcquireAsync(Pool);

    public Task DisposeAsync() => CamoufoxBackend.ReleaseAsync(Pool);

    // Clears cookies on the one page context every fixture.Browser session shares, which is why
    // the classes that call it stay serialised together in SharedBrowser — and why calling it is
    // the marker that keeps them there. See PlaywrightCollectionLayoutTests.
    public Task ClearContextStateAsync() => Browser.ClearCookiesAsync();

    // JS-heavy live pages render form elements client-side after
    // navigation, so asserting on a single snapshot taken right after NavigateAsync is a race.
    // Waits for the actual condition instead of guessing at a fixed delay.
    public async Task<string> WaitForSnapshotAsync(
        string sessionId,
        Func<string, bool> predicate,
        string description,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(15);
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(500);
        var start = Stopwatch.GetTimestamp();

        string? lastError = null;
        while (true)
        {
            var result = await Browser.SnapshotAsync(new SnapshotRequest(sessionId));
            lastError = result.ErrorMessage;
            if (result is { ErrorMessage: null, Snapshot: not null } && predicate(result.Snapshot))
            {
                return result.Snapshot;
            }

            if (Stopwatch.GetElapsedTime(start) >= effectiveTimeout)
            {
                throw new TimeoutException(
                    $"Timed out after {effectiveTimeout.TotalSeconds:0}s waiting for {description}. " +
                    $"Last snapshot error: {lastError ?? "none"}.");
            }

            await Task.Delay(interval);
        }
    }
}

// The classes that time a production code path and assert on the milliseconds take this instead:
// same fixture, a browser server nothing else is driving. What they measure is real latency
// through a real browser, so a server answering three other collections at once is not a slower
// version of the same reading — it is a different one, and the run that asked for it failed every
// budget in both classes.
public sealed class QuietBrowserFixture : PlaywrightWebBrowserFixture
{
    protected override string Pool => CamoufoxBackend.QuietPool;
}

// The classes that only ever drive sessions they opened themselves take this: same fixture, a third
// backend. They share no state any other class can observe — each opens a GUID session and closes
// it — so the only thing that ever forced them to wait for the cookie-clearing classes was being in
// the same collection. On their own backend they run beside that chain, which is where the suite's
// last twenty seconds went.
public sealed class IsolatedSessionBrowserFixture : PlaywrightWebBrowserFixture
{
    protected override string Pool => CamoufoxBackend.IsolatedPool;
}

// Three collections, so the browser-integration classes run at once rather than as a single serial
// chain — which was as long as all their spans added up to, and on a slow run was what the whole
// suite finished behind.
//
// Timing is whether a class asserts on elapsed time: those get a backend to themselves and stay
// serialised internally so the two of them do not measure each other. The rest split on whether a
// class clears the fixture's cookies, which reaches the one context every session on that backend
// shares — those stay serialised together in SharedBrowser. Everything else opens a GUID session
// and closes it, observing nothing another class can touch, so IsolatedSessions gives them a third
// backend to run on beside that chain. PlaywrightCollectionLayoutTests holds all three rules.
[CollectionDefinition(PlaywrightCollections.SharedBrowser)]
public class PlaywrightWebBrowserIntegrationCollection : ICollectionFixture<PlaywrightWebBrowserFixture>;

[CollectionDefinition(PlaywrightCollections.Timing)]
public class PlaywrightTimingCollection : ICollectionFixture<QuietBrowserFixture>;

[CollectionDefinition(PlaywrightCollections.IsolatedSessions)]
public class PlaywrightIsolatedSessionsCollection : ICollectionFixture<IsolatedSessionBrowserFixture>;

public static class PlaywrightCollections
{
    public const string SharedBrowser = "PlaywrightWebBrowserIntegration";
    public const string Timing = "Playwright.Timing";
    public const string IsolatedSessions = "Playwright.IsolatedSessions";
}