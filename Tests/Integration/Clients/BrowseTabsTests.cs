using Domain.Contracts;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// The honestly browser-dependent claims of the tab pool: real pages side by side, each holding its
// own DOM while the other is browsed.
[Collection(PlaywrightCollections.SharedBrowser)]
public class BrowseTabsTests(PlaywrightWebBrowserFixture fixture)
{
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task BrowsingASecondUrl_KeepsTwoLiveDomsSideBySide()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            (await fixture.Browser.NavigateAsync(new BrowseRequest(sessionId, "https://example.com/")))
                .Status.ShouldBe(BrowseStatus.Success);
            (await fixture.Browser.NavigateAsync(new BrowseRequest(sessionId, "https://example.org/")))
                .Status.ShouldBe(BrowseStatus.Success);

            var session = fixture.Browser.Sessions.Get(sessionId).ShouldNotBeNull();
            session.Tabs.Count.ShouldBe(2);

            // Both DOMs answer — the first page was not navigated away by the second browse.
            foreach (var tab in session.Tabs)
            {
                tab.Page.IsClosed.ShouldBeFalse();
                var text = await tab.Page.EvaluateAsync<string>("() => document.body.textContent ?? ''");
                text.ShouldContain("Example Domain");
            }
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
                <button id="mark" onclick="document.title='clicked-on-first-tab'">Mark</button>
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
            result.Url.ShouldContain("example.com");

            var title = await fixture.Browser.EvaluateOnSessionAsync<string>(
                sessionId, "() => document.title");
            title.ShouldBe("clicked-on-first-tab");
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
                .Url.ShouldContain("example.org");

            await fixture.Browser.FetchImagesAsync(new ImageFetchRequest(sessionId, [refA]));

            (await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId)))
                .Url.ShouldContain("example.com");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    private async Task BrowseAsync(string sessionId, string url)
    {
        var nav = await fixture.Browser.NavigateAsync(new BrowseRequest(sessionId, url));
        nav.Status.ShouldBe(BrowseStatus.Success);
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
    // ref the page stamped for it.
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

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task BrowsingAUrlAlreadyOpen_ReloadsItsTabInPlace()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            await fixture.Browser.NavigateAsync(new BrowseRequest(sessionId, "https://example.com/"));
            await fixture.Browser.NavigateAsync(new BrowseRequest(sessionId, "https://example.org/"));
            await fixture.Browser.NavigateAsync(new BrowseRequest(sessionId, "https://example.com/"));

            var session = fixture.Browser.Sessions.Get(sessionId).ShouldNotBeNull();
            session.Tabs.Count.ShouldBe(2);
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }
}