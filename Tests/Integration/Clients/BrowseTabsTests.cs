using Domain.Contracts;
using Infrastructure.Clients.Browser;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// The honestly browser-dependent claims of the tab pool: real pages side by side, each holding its
// own DOM while the other is browsed. Everything here observes the pool the way the model would —
// through walls and results — never by reaching past the browser contract.
[Collection(PlaywrightCollections.IsolatedSessions)]
public class BrowseTabsTests(IsolatedSessionBrowserFixture fixture)
{
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task BrowsingASecondUrl_KeepsTwoLiveDomsSideBySide()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            // Partial counts here and in BrowseAsync below: these are two blank anchors chosen for
            // being different origins, not for what they say. A DOMContentLoaded that never arrives
            // spends the production 30s budget and returns Partial with a usable page.
            await BrowseAsync(sessionId, "https://example.com/");
            await BrowseAsync(sessionId, "https://example.org/");

            // Both pages still answer a snapshot addressed by URL — the first was neither
            // navigated away by the second browse nor evicted.
            var first = await fixture.Browser.SnapshotAsync(
                new SnapshotRequest(sessionId, ForUrl: "https://example.com/"));
            first.ErrorMessage.ShouldBeNull();
            first.Url.ShouldNotBeNull().ShouldContain("example.com");
            first.Snapshot.ShouldNotBeNull().ShouldContain("Example Domain");

            var second = await fixture.Browser.SnapshotAsync(
                new SnapshotRequest(sessionId, ForUrl: "https://example.org/"));
            second.ErrorMessage.ShouldBeNull();
            second.Url.ShouldNotBeNull().ShouldContain("example.org");
            second.Snapshot.ShouldNotBeNull().ShouldContain("Example Domain");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task ImageRefsFromTwoTabs_AnswerEachFromTheirOwnPage_InOneCall()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            var refA = await OpenTabWithImageAsync(sessionId, "https://example.com/", "First tab picture");
            var refB = await OpenTabWithImageAsync(sessionId, "https://example.org/", "Second tab picture");

            var fetched = await fixture.Browser.FetchImagesAsync(
                new ImageFetchRequest(sessionId, [refA, refB]));

            fetched.Images.Count.ShouldBe(2);
            fetched.Images[0].Status.ShouldBe(ImageFetchStatus.Success);
            fetched.Images[0].Label.ShouldBe("First tab picture");
            fetched.Images[1].Status.ShouldBe(ImageFetchStatus.Success);
            fetched.Images[1].Label.ShouldBe("Second tab picture");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task AnAction_LandsOnTheTabItsRefNames_EvenWhenAnotherIsCurrent()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            await BrowseAsync(sessionId, "https://example.com/");
            await InjectAsync(sessionId,
                """
                <button id="mark" onclick="const b = document.createElement('button');
                    b.textContent = 'clicked-on-first-tab'; document.body.appendChild(b)">Mark</button>
                """);
            var snapshot = await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId));
            var buttonRef = System.Text.RegularExpressions.Regex
                .Match(snapshot.Snapshot!, @"button ""Mark"" \[ref=(e-\d+)\]").Groups[1].Value;
            buttonRef.ShouldNotBeNullOrEmpty();

            // A second tab becomes current; the ref still names the first.
            await BrowseAsync(sessionId, "https://example.org/");

            var result = await fixture.Browser.ActionAsync(new WebActionRequest(
                sessionId, buttonRef, WebActionType.Click));

            result.Status.ShouldBe(WebActionStatus.Success);
            result.Url.ShouldNotBeNull().ShouldContain("example.com");

            // The click landed on the first tab: its DOM change shows in the action's own diff.
            result.Snapshot.ShouldNotBeNull().ShouldContain("clicked-on-first-tab");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task FetchingAnOldTabsPicture_RefocusesThatTabForTheNextSnapshot()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            var refA = await OpenTabWithImageAsync(sessionId, "https://example.com/", "Old tab picture");
            await BrowseAsync(sessionId, "https://example.org/");

            (await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId)))
                .Url.ShouldNotBeNull().ShouldContain("example.org");

            await fixture.Browser.FetchImagesAsync(new ImageFetchRequest(sessionId, [refA]));

            (await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId)))
                .Url.ShouldNotBeNull().ShouldContain("example.com");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task AClickThatOpensANewTab_AnswersFromThePopup()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            await BrowseAsync(sessionId, "https://example.com/");
            await InjectAsync(sessionId,
                """
                <button id="open" onclick="window.open('https://example.org/')">Open elsewhere</button>
                """);
            var snapshot = await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId));
            var buttonRef = System.Text.RegularExpressions.Regex
                .Match(snapshot.Snapshot!, @"button ""Open elsewhere"" \[ref=(e-\d+)\]").Groups[1].Value;
            buttonRef.ShouldNotBeNullOrEmpty();

            var result = await fixture.Browser.ActionAsync(new WebActionRequest(
                sessionId, buttonRef, WebActionType.Click));

            // The action answers from where the site actually took the model, not with a stale
            // diff of the page left behind.
            result.Status.ShouldBe(WebActionStatus.Success);
            result.NavigationOccurred.ShouldBeTrue();
            result.Url.ShouldNotBeNull().ShouldContain("example.org");
            result.Snapshot.ShouldNotBeNull();

            // The popup joined the pool as the tab the next ref-less call addresses…
            (await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId)))
                .Url.ShouldNotBeNull().ShouldContain("example.org");

            // …and the opener lives on beside it, still addressable by its URL.
            (await fixture.Browser.SnapshotAsync(
                    new SnapshotRequest(sessionId, ForUrl: "https://example.com/")))
                .ErrorMessage.ShouldBeNull();
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task BrowsingAUrlAlreadyOpen_ReloadsItsTabInPlace()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            var refA = await OpenTabWithImageAsync(sessionId, "https://example.com/", "Reloaded picture");
            await BrowseAsync(sessionId, "https://example.org/");
            await BrowseAsync(sessionId, "https://example.com/");

            // Reused, not another tab: the reload restamped the tab, so the old ref answers the
            // superseded wall. A second tab for the same URL would have left it routing, and an
            // eviction would answer closed.
            var fetched = await fixture.Browser.FetchImagesAsync(new ImageFetchRequest(sessionId, [refA]));
            var image = fetched.Images.ShouldHaveSingleItem();
            image.Status.ShouldBe(ImageFetchStatus.RefSuperseded);
            image.Url.ShouldNotBeNull().ShouldContain("example.com");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task ABrowsePastTheTabCap_EvictsTheLeastRecentlyTouchedTab()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            // Query-string variants keep every address on the two reliable anchors while staying
            // distinct to the pool's exact-match reuse.
            var refA = await OpenTabWithImageAsync(sessionId, "https://example.com/", "Oldest picture");
            await BrowseAsync(sessionId, "https://example.org/");
            await BrowseAsync(sessionId, "https://example.com/?third");
            await BrowseAsync(sessionId, "https://example.org/?fourth");

            // The fourth distinct page pushed the pool past its cap, and the least-recently
            // touched tab died for it — observable because its ref now answers the closed wall
            // naming the page to browse again.
            var fetched = await fixture.Browser.FetchImagesAsync(new ImageFetchRequest(sessionId, [refA]));
            var image = fetched.Images.ShouldHaveSingleItem();
            image.Status.ShouldBe(ImageFetchStatus.RefClosed);
            image.Url.ShouldNotBeNull().ShouldContain("example.com");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task AnIdlePrunedSession_AnswersNoSession()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        // A private browser on the same backend, with an idle window nothing can stay inside:
        // pruning is wall-clock policy, and the shared fixture's half-hour window cannot elapse
        // inside a test.
        await using var browser = new PlaywrightWebBrowser(
            wsEndpoint: fixture.WsEndpoint,
            idleTimeout: TimeSpan.FromMilliseconds(1));

        var sessionId = $"test-{Guid.NewGuid():N}";
        var nav = await browser.NavigateAsync(new BrowseRequest(sessionId, "https://example.com/"));
        nav.Status.ShouldBeOneOf(BrowseStatus.Success, BrowseStatus.Partial);

        await Eventually.Until(async () =>
        {
            await browser.PruneIdleAsync();
            var current = await browser.GetCurrentPageAsync(sessionId);
            return current.Status == BrowseStatus.SessionNotFound;
        }, "the idle session to answer no-session through the contract");
    }

    private async Task BrowseAsync(string sessionId, string url)
    {
        var nav = await fixture.Browser.NavigateAsync(new BrowseRequest(sessionId, url));
        nav.Status.ShouldBeOneOf(BrowseStatus.Success, BrowseStatus.Partial);
    }

    private async Task InjectAsync(string sessionId, string markup)
    {
        await fixture.Browser.EvaluateOnSessionAsync(
            sessionId,
            $"() => {{ document.body.innerHTML = {System.Text.Json.JsonSerializer.Serialize(markup)}; }}");
    }

    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    // Opens (or reloads) a tab on the URL, plants one real image on it, annotates, and answers the
    // ref the page stamped for it. The evaluate/annotate calls are arrangement — the page being
    // set up — never the assertion.
    private async Task<string> OpenTabWithImageAsync(string sessionId, string url, string label)
    {
        await BrowseAsync(sessionId, url);
        await InjectAsync(sessionId,
            $"""
             <img id="planted" src="data:image/png;base64,{OnePixelPngBase64}"
                  style="width:300px;height:300px" alt="{label}">
             """);
        await fixture.Browser.AnnotateImagesOnSessionAsync(sessionId);
        var stamped = await fixture.Browser.EvaluateOnSessionAsync<string>(
            sessionId, "() => document.getElementById('planted').getAttribute('data-img-ref') ?? ''");
        stamped.ShouldNotBeNullOrEmpty();
        return stamped;
    }
}