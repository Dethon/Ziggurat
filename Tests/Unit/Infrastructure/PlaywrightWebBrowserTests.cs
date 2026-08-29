using System.Text.Json;
using Domain.Contracts;
using Infrastructure.Clients.Browser;
using Microsoft.Playwright;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class PlaywrightWebBrowserTests : IAsyncLifetime
{
    private PlaywrightWebBrowser _browser = null!;

    public Task InitializeAsync()
    {
        _browser = new PlaywrightWebBrowser(wsEndpoint: "ws://dummy:9377/browser");
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
    }

    [Theory]
    [InlineData("not-a-valid-url", "Invalid URL")]
    [InlineData("", "Invalid URL")]
    [InlineData("ftp://example.com/file", "http")]
    [InlineData("file:///etc/passwd", "http")]
    public async Task NavigateAsync_WithInvalidUrl_ReturnsError(string url, string expectedErrorSubstring)
    {
        // Act
        var request = new BrowseRequest(
            SessionId: "test",
            Url: url);
        var result = await _browser.NavigateAsync(request);

        // Assert
        result.Status.ShouldBe(BrowseStatus.Error);
        result.ErrorMessage!.ShouldContain(expectedErrorSubstring);
    }

    [Fact]
    public async Task GetCurrentPageAsync_WithNoSession_ReturnsSessionNotFound()
    {
        // Act
        var result = await _browser.GetCurrentPageAsync("non-existent-session");

        // Assert
        result.Status.ShouldBe(BrowseStatus.SessionNotFound);
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task CloseSessionAsync_WithNoSession_DoesNotThrow()
    {
        // Act & Assert
        await _browser.CloseSessionAsync("non-existent-session");
    }

    [Fact]
    public async Task NavigateAsync_WithValidUrl_ButNoWsEndpoint_ThrowsInvalidOperation()
    {
        // Arrange
        await using var browser = new PlaywrightWebBrowser(wsEndpoint: null);
        var request = new BrowseRequest(
            SessionId: "test",
            Url: "https://example.com");

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(
            () => browser.NavigateAsync(request));
    }

    [Fact]
    public async Task NavigateAsync_ConcurrentRequestsForOneUrl_ShareTheTabWithoutClobbering()
    {
        // Reproduces "Navigation to X is interrupted by another navigation to Y": two browses of
        // the same URL reuse one tab, so their GotoAsync calls must queue on it, not interleave.
        var firstGotoEntered = new TaskCompletionSource();
        var releaseFirstGoto = new TaskCompletionSource();
        var gotoCallCount = 0;

        var page = new Mock<IPage>();
        page.SetupGet(p => p.Url).Returns("https://a.test/");
        page.SetupGet(p => p.IsClosed).Returns(false);
        page
            .Setup(p => p.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions?>()))
            .Returns(async () =>
            {
                var n = Interlocked.Increment(ref gotoCallCount);
                if (n == 1)
                {
                    firstGotoEntered.TrySetResult();
                    await releaseFirstGoto.Task;
                }

                return Mock.Of<IResponse>();
            });
        // Abort the post-navigation pipeline immediately so the test stays fast and focused.
        page.Setup(p => p.ContentAsync()).ThrowsAsync(new InvalidOperationException("stop"));

        var context = new Mock<IBrowserContext>();
        context.Setup(c => c.NewPageAsync()).ReturnsAsync(page.Object);

        var browserMock = new Mock<IBrowser>();
        browserMock.SetupGet(b => b.IsConnected).Returns(true);
        browserMock
            .Setup(b => b.NewContextAsync(It.IsAny<BrowserNewContextOptions?>()))
            .ReturnsAsync(context.Object);

        await using var browser = new PlaywrightWebBrowser(
            wsEndpoint: "ws://dummy:9377/browser",
            browserFactory: () => Task.FromResult(browserMock.Object));

        var nav1 = browser.NavigateAsync(new BrowseRequest(SessionId: "shared", Url: "https://a.test/"));
        await firstGotoEntered.Task;

        var nav2 = browser.NavigateAsync(new BrowseRequest(SessionId: "shared", Url: "https://a.test/"));
        var secondStarted = await Task.WhenAny(nav2, Eventually.Settle()) == nav2;

        // While the first navigation holds the tab, the second must not have navigated.
        gotoCallCount.ShouldBe(1);
        secondStarted.ShouldBeFalse();

        releaseFirstGoto.TrySetResult();
        await nav1;
        await nav2;
        gotoCallCount.ShouldBe(2);
    }

    [Fact]
    public async Task NavigateAsync_ConcurrentRequestsForDifferentUrls_RunOnTwoTabsInParallel()
    {
        // The reason tabs exist at all: two browses of different URLs must not queue behind one
        // another the way one page forced them to.
        var firstGotoEntered = new TaskCompletionSource();
        var releaseFirstGoto = new TaskCompletionSource();

        var pages = new List<Mock<IPage>>();
        var context = new Mock<IBrowserContext>();
        context.Setup(c => c.NewPageAsync()).ReturnsAsync(() =>
        {
            var page = new Mock<IPage>();
            var isFirst = pages.Count == 0;
            page.SetupGet(p => p.Url).Returns(isFirst ? "https://a.test/" : "https://b.test/");
            page.SetupGet(p => p.IsClosed).Returns(false);
            page.Setup(p => p.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions?>()))
                .Returns(async () =>
                {
                    if (isFirst)
                    {
                        firstGotoEntered.TrySetResult();
                        await releaseFirstGoto.Task;
                    }

                    return Mock.Of<IResponse>();
                });
            page.Setup(p => p.ContentAsync()).ThrowsAsync(new InvalidOperationException("stop"));
            pages.Add(page);
            return page.Object;
        });

        var browserMock = new Mock<IBrowser>();
        browserMock.SetupGet(b => b.IsConnected).Returns(true);
        browserMock
            .Setup(b => b.NewContextAsync(It.IsAny<BrowserNewContextOptions?>()))
            .ReturnsAsync(context.Object);

        await using var browser = new PlaywrightWebBrowser(
            wsEndpoint: "ws://dummy:9377/browser",
            browserFactory: () => Task.FromResult(browserMock.Object));

        var nav1 = browser.NavigateAsync(new BrowseRequest(SessionId: "shared", Url: "https://a.test/"));
        await firstGotoEntered.Task;

        var nav2 = browser.NavigateAsync(new BrowseRequest(SessionId: "shared", Url: "https://b.test/"));

        // The second browse lands on its own tab and finishes while the first still navigates.
        (await Task.WhenAny(nav2, Task.Delay(2000))).ShouldBe(nav2);

        releaseFirstGoto.TrySetResult();
        await nav1;
        pages.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SnapshotAsync_RacingANavigationOnTheSameTab_WaitsForTheDocument()
    {
        // A mid-navigation snapshot answering a half-replaced DOM is the bug; waiting is the fix.
        var gotoEntered = new TaskCompletionSource();
        var releaseGoto = new TaskCompletionSource();

        var page = new Mock<IPage>();
        page.SetupGet(p => p.Url).Returns("https://a.test/");
        page.SetupGet(p => p.IsClosed).Returns(false);
        page.Setup(p => p.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions?>()))
            .Returns(async () =>
            {
                gotoEntered.TrySetResult();
                await releaseGoto.Task;
                return Mock.Of<IResponse>();
            });
        page.Setup(p => p.ContentAsync()).ThrowsAsync(new InvalidOperationException("stop"));
        var snapshotRan = false;
        page.Setup(p => p.EvaluateAsync<JsonElement>(It.IsAny<string>(), It.IsAny<object?>()))
            .Callback(() => snapshotRan = true)
            .ThrowsAsync(new InvalidOperationException("snapshot-stop"));

        var context = new Mock<IBrowserContext>();
        context.Setup(c => c.NewPageAsync()).ReturnsAsync(page.Object);

        var browserMock = new Mock<IBrowser>();
        browserMock.SetupGet(b => b.IsConnected).Returns(true);
        browserMock
            .Setup(b => b.NewContextAsync(It.IsAny<BrowserNewContextOptions?>()))
            .ReturnsAsync(context.Object);

        await using var browser = new PlaywrightWebBrowser(
            wsEndpoint: "ws://dummy:9377/browser",
            browserFactory: () => Task.FromResult(browserMock.Object));

        var nav = browser.NavigateAsync(new BrowseRequest(SessionId: "s", Url: "https://a.test/"));
        await gotoEntered.Task;

        var snapshot = browser.SnapshotAsync(new SnapshotRequest("s"));
        await Eventually.Settle();
        snapshotRan.ShouldBeFalse();

        releaseGoto.TrySetResult();
        await nav;
        await snapshot;
        snapshotRan.ShouldBeTrue();
    }

    [Fact]
    public async Task EnsureInitializedAsync_AfterBrowserDisconnects_ReconnectsToNewBrowser()
    {
        // Arrange: a connection factory that hands out a fresh mock browser each call,
        // mirroring how Camoufox is reached over a WebSocket that can drop at any time.
        var connections = new List<Mock<IBrowser>>();
        Func<Task<IBrowser>> factory = () =>
        {
            var browser = new Mock<IBrowser>();
            browser.SetupGet(b => b.IsConnected).Returns(true);
            browser
                .Setup(b => b.NewContextAsync(It.IsAny<BrowserNewContextOptions?>()))
                .ReturnsAsync(new Mock<IBrowserContext>().Object);
            connections.Add(browser);
            return Task.FromResult(browser.Object);
        };

        await using var browser = new PlaywrightWebBrowser(
            wsEndpoint: "ws://dummy:9377/browser", browserFactory: factory);

        await browser.EnsureInitializedAsync();
        connections.Count.ShouldBe(1);

        // Act: the underlying WebSocket drops — the live browser now reports disconnected.
        connections[0].SetupGet(b => b.IsConnected).Returns(false);
        await browser.EnsureInitializedAsync();

        // Assert: a new connection was established instead of reusing the dead one forever.
        connections.Count.ShouldBe(2);
    }

    [Fact]
    public async Task NavigateAsync_WhenConnectionClosedMidNavigation_ReconnectsAndRetries()
    {
        // The Camoufox WebSocket can drop while a navigation is in flight. The first attempt
        // throws "Target page, context or browser has been closed"; instead of surfacing that
        // to the caller, the browser should reconnect and re-navigate on a fresh page.
        var closedEx = new PlaywrightException(
            "Target page, context or browser has been closed");

        var page1 = new Mock<IPage>();
        page1.SetupGet(p => p.IsClosed).Returns(false);
        page1.SetupGet(p => p.Url).Returns("about:blank");
        page1.Setup(p => p.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions?>()))
            .ThrowsAsync(closedEx);

        // Second attempt runs on a fresh connection. A non-closed sentinel cuts the
        // post-navigation pipeline short so the test stays focused on the retry.
        var page2 = new Mock<IPage>();
        page2.SetupGet(p => p.IsClosed).Returns(false);
        page2.SetupGet(p => p.Url).Returns("https://a.test/");
        page2.Setup(p => p.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions?>()))
            .ThrowsAsync(new InvalidOperationException("reached-second-attempt"));

        var connections = new List<Mock<IBrowser>>();
        var pages = new Queue<Mock<IPage>>([page1, page2]);
        Func<Task<IBrowser>> factory = () =>
        {
            var context = new Mock<IBrowserContext>();
            context.Setup(c => c.NewPageAsync()).ReturnsAsync(pages.Dequeue().Object);
            var browser = new Mock<IBrowser>();
            browser.SetupGet(b => b.IsConnected).Returns(true);
            browser.Setup(b => b.NewContextAsync(It.IsAny<BrowserNewContextOptions?>()))
                .ReturnsAsync(context.Object);
            connections.Add(browser);
            return Task.FromResult(browser.Object);
        };

        await using var browser = new PlaywrightWebBrowser(
            wsEndpoint: "ws://dummy:9377/browser", browserFactory: factory);

        var result = await browser.NavigateAsync(new BrowseRequest(SessionId: "s", Url: "https://a.test/"));

        connections.Count.ShouldBe(2);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldNotContain("has been closed");
        result.ErrorMessage.ShouldContain("reached-second-attempt");
    }

    [Fact]
    public async Task NavigateAsync_WhenConnectionStaysClosedAfterReconnect_ReturnsCleanMessageNotRawError()
    {
        // Some pages crash the Camoufox/Playwright server process on every load, so even the
        // post-reconnect retry hits a dead connection and throws "Target page, context or browser
        // has been closed" again. The caller must get a clean, actionable message — never the raw
        // Playwright text, which is meaningless to the agent.
        var closedEx = new PlaywrightException("Target page, context or browser has been closed");

        var connections = new List<Mock<IBrowser>>();
        Func<Task<IBrowser>> factory = () =>
        {
            var page = new Mock<IPage>();
            page.SetupGet(p => p.IsClosed).Returns(false);
            page.SetupGet(p => p.Url).Returns("about:blank");
            page.Setup(p => p.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions?>()))
                .ThrowsAsync(closedEx);
            var context = new Mock<IBrowserContext>();
            context.Setup(c => c.NewPageAsync()).ReturnsAsync(page.Object);
            var browser = new Mock<IBrowser>();
            browser.SetupGet(b => b.IsConnected).Returns(true);
            browser.Setup(b => b.NewContextAsync(It.IsAny<BrowserNewContextOptions?>()))
                .ReturnsAsync(context.Object);
            connections.Add(browser);
            return Task.FromResult(browser.Object);
        };

        await using var browser = new PlaywrightWebBrowser(
            wsEndpoint: "ws://dummy:9377/browser", browserFactory: factory);

        var result = await browser.NavigateAsync(new BrowseRequest(SessionId: "s", Url: "https://a.test/"));

        // It reconnected (so it genuinely retried) but the retry still failed closed.
        connections.Count.ShouldBe(2);
        result.Status.ShouldBe(BrowseStatus.Error);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldNotContain("has been closed");
    }

    [Fact]
    public async Task SnapshotAsync_WhenConnectionClosed_ReturnsSessionNotFoundInsteadOfRawError()
    {
        // A dropped connection makes the cached page dead — its content is gone, so reconnecting
        // would only yield a blank page. The honest result is a clean session-not-found that tells
        // the caller to web_browse again, not the raw "has been closed" Playwright message.
        var (browser, page) = await CreateBrowserWithCachedSessionAsync("s", "https://a.test/");
        await using var _ = browser;

        page.Setup(p => p.EvaluateAsync<JsonElement>(It.IsAny<string>(), It.IsAny<object?>()))
            .ThrowsAsync(new PlaywrightException("Target page, context or browser has been closed"));

        var result = await browser.SnapshotAsync(new SnapshotRequest("s", null));

        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldNotContain("has been closed");
        result.ErrorMessage.ShouldContain("not found");
    }

    [Fact]
    public async Task ActionAsync_WhenConnectionClosed_ReturnsSessionNotFoundInsteadOfRawError()
    {
        var (browser, page) = await CreateBrowserWithCachedSessionAsync("s", "https://a.test/");
        await using var _ = browser;

        page.Setup(p => p.GoBackAsync(It.IsAny<PageGoBackOptions?>()))
            .ThrowsAsync(new PlaywrightException("Target page, context or browser has been closed"));

        var result = await browser.ActionAsync(new WebActionRequest("s", null, WebActionType.Back));

        result.Status.ShouldBe(WebActionStatus.SessionNotFound);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldNotContain("has been closed");
    }

    [Fact]
    public async Task SnapshotAsync_NamingAPageNoTabHolds_RefusesInsteadOfReadingTheCurrentTab()
    {
        // A snapshot composed with a browse names the page the browse landed on. If that tab is
        // already gone — evicted, or reused by another browse — falling back to the current tab
        // would attach another page's refs to this page's text.
        var (browser, page) = await CreateBrowserWithCachedSessionAsync("s", "https://a.test/");
        await using var _ = browser;
        var evaluated = false;
        page.Setup(p => p.EvaluateAsync<JsonElement>(It.IsAny<string>(), It.IsAny<object?>()))
            .Callback(() => evaluated = true)
            .ThrowsAsync(new InvalidOperationException("stop"));

        var result = await browser.SnapshotAsync(new SnapshotRequest("s", null, ForUrl: "https://gone.test/"));

        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("https://gone.test/");
        evaluated.ShouldBeFalse();
    }

    [Fact]
    public async Task ActionAsync_WhenTheTabWasEvictedMidCall_AnswersTheClosedWallNotALostSession()
    {
        // The page dying is not the connection dying: an eviction that lands while the action is
        // in flight throws the same "has been closed" text, but the session's other tabs live on.
        // Tearing the whole session down here was the bug.
        var closed = false;
        var (browser, page) = await CreateBrowserWithCachedSessionAsync("s", "https://a.test/");
        await using var _ = browser;

        page.SetupGet(p => p.IsClosed).Returns(() => closed);
        page.Setup(p => p.GoBackAsync(It.IsAny<PageGoBackOptions?>()))
            .Returns(() =>
            {
                closed = true;
                throw new PlaywrightException("Target page, context or browser has been closed");
            });

        var result = await browser.ActionAsync(new WebActionRequest("s", null, WebActionType.Back));

        result.Status.ShouldBe(WebActionStatus.RefClosed);
        result.RefUrl.ShouldBe("https://a.test/");
    }

    [Fact]
    public async Task FetchImagesAsync_WhenTheTabClosedMidFetch_AnswersTheClosedWallNotTheSitesRefusal()
    {
        // An eviction landing mid-fetch surfaces as a Playwright error the fetch reads as the
        // site refusing; the closed page says which wall it truly was.
        var closed = false;
        var (browser, page) = await CreateBrowserWithCachedSessionAsync("s", "https://a.test/");
        await using var _ = browser;

        page.SetupGet(p => p.IsClosed).Returns(() => closed);
        page.Setup(p => p.EvaluateAsync<string?>(It.IsAny<string>(), It.IsAny<object?>()))
            .Returns(() =>
            {
                closed = true;
                throw new PlaywrightException("Target page, context or browser has been closed");
            });

        var result = await browser.FetchImagesAsync(new ImageFetchRequest("s", ["i-1"]));

        result.SessionMissing.ShouldBeFalse();
        var image = result.Images.ShouldHaveSingleItem();
        image.Status.ShouldBe(ImageFetchStatus.RefClosed);
        image.Url.ShouldBe("https://a.test/");
    }

    // Primes a browser so a session is cached for sessionId. The navigation deliberately
    // aborts in the post-goto pipeline (ContentAsync throws), which still caches the session.
    private static async Task<(PlaywrightWebBrowser browser, Mock<IPage> page)>
        CreateBrowserWithCachedSessionAsync(string sessionId, string url)
    {
        var page = new Mock<IPage>();
        page.SetupGet(p => p.IsClosed).Returns(false);
        page.SetupGet(p => p.Url).Returns(url);
        page.Setup(p => p.GotoAsync(It.IsAny<string>(), It.IsAny<PageGotoOptions?>()))
            .ReturnsAsync(Mock.Of<IResponse>());
        page.Setup(p => p.ContentAsync()).ThrowsAsync(new InvalidOperationException("prime-stop"));

        var context = new Mock<IBrowserContext>();
        context.Setup(c => c.NewPageAsync()).ReturnsAsync(page.Object);

        var browserMock = new Mock<IBrowser>();
        browserMock.SetupGet(b => b.IsConnected).Returns(true);
        browserMock.Setup(b => b.NewContextAsync(It.IsAny<BrowserNewContextOptions?>()))
            .ReturnsAsync(context.Object);

        var browser = new PlaywrightWebBrowser(
            wsEndpoint: "ws://dummy:9377/browser",
            browserFactory: () => Task.FromResult(browserMock.Object));

        await browser.NavigateAsync(new BrowseRequest(SessionId: sessionId, Url: url));
        return (browser, page);
    }

    // The diff compares lines with their refs stripped, and the stamp is spelled e-1 — the strip
    // rule moves in step with it, or every restamped line reads as removed-and-added noise.
    [Theory]
    [InlineData("- button \"Buy\" [ref=e-3]", "- button \"Buy\"")]
    [InlineData("- link \"Docs\" [ref=e-127] [expanded]", "- link \"Docs\" [expanded]")]
    [InlineData("- heading \"No refs here\"", "- heading \"No refs here\"")]
    public void StripRefs_RemovesTheDashedRefTag(string line, string expected)
    {
        PlaywrightWebBrowser.StripRefs(line).ShouldBe(expected);
    }

    // A DataDome loader must still be detected, but the probe used to be a bare "dd.js" substring
    // test, which matches any content-hashed bundle whose name happens to end in "dd" — github.com
    // ships marketing-navigation-ece1017251744ddd.js, and that alone made the whole site report
    // CaptchaRequired and return no content on every browse.
    [Theory]
    [InlineData("""<script src="/dd.js"></script>""", true)]
    [InlineData("""<script src="https://cdn.example.com/dd.js"></script>""", true)]
    [InlineData("""<script src='/static/dd.js?v=3'></script>""", true)]
    [InlineData("""<img src="https://geo.captcha-delivery.com/captcha/?initialCid=x">""", true)]
    [InlineData("""<script>window.datadome = {};</script>""", true)]
    [InlineData(
        """<script src="/assets/marketing-navigation-ece1017251744ddd.js" fetchpriority="low"></script>""",
        false)]
    [InlineData("""<link href="/a/bundle-4fdd.js"><script src="/b/chunk.add.js"></script>""", false)]
    [InlineData("<html><body><p>An ordinary page</p></body></html>", false)]
    public void ContainsCaptcha_MatchesDataDomeLoaderButNotHashedBundleNames(string html, bool expected)
    {
        PlaywrightWebBrowser.ContainsCaptcha(html).ShouldBe(expected);
    }

    // JSON-LD allows "@type" to be either a string or an array of strings, and Shopify-style
    // product pages routinely ship the array form (@graph[0] on gpufanreplacement.com is
    // ["Organization","Brand"]). Calling GetString() on that element throws
    // InvalidOperationException: "The requested operation requires an element of type 'String',
    // but the target element has type 'Array'." Inside @graph the throw escaped the surrounding
    // try/catch entirely, because Select() is lazy and was only enumerated by ToList() after the
    // catch had gone out of scope -- so a whole browse of the page failed with internal_error.
    [Theory]
    [InlineData(
        """<script type="application/ld+json">{"@type":["Product","Thing"],"name":"GPU"}</script>""",
        "Product")]
    [InlineData(
        """<script type="application/ld+json">{"@graph":[{"@type":["Organization","Brand"],"name":"X"}]}</script>""",
        "Organization")]
    [InlineData(
        """<script type="application/ld+json">{"@type":"Product","name":"GPU"}</script>""",
        "Product")]
    [InlineData(
        """<script type="application/ld+json">{"@graph":[{"@type":"WebSite","name":"X"}]}</script>""",
        "WebSite")]
    [InlineData(
        """<script type="application/ld+json">{"@type":[],"name":"GPU"}</script>""",
        "Unknown")]
    [InlineData(
        """<script type="application/ld+json">{"@type":[{"nested":1}],"name":"GPU"}</script>""",
        "Unknown")]
    [InlineData(
        """<script type="application/ld+json">{"name":"GPU"}</script>""",
        "Unknown")]
    public void ExtractStructuredData_ReadsTypeWhetherItIsAStringOrAnArray(string html, string expectedType)
    {
        var result = PlaywrightWebBrowser.ExtractStructuredData(html);

        result.ShouldHaveSingleItem().Type.ShouldBe(expectedType);
    }

    // The array-typed entry must not take its siblings down with it: the page that failed in
    // production had one array @type followed by six well-formed string ones.
    [Fact]
    public void ExtractStructuredData_WithArrayTypeInGraph_StillReturnsEverySibling()
    {
        const string html = """
            <script type="application/ld+json">
            {"@graph":[
                {"@type":["Organization","Brand"],"name":"A"},
                {"@type":"ImageObject","name":"B"},
                {"@type":"WebSite","name":"C"}
            ]}
            </script>
            """;

        var result = PlaywrightWebBrowser.ExtractStructuredData(html);

        result.Select(s => s.Type).ShouldBe(["Organization", "ImageObject", "WebSite"]);
    }

    // Malformed JSON-LD is not worth failing a browse over -- it must still be swallowed.
    [Fact]
    public void ExtractStructuredData_WithUnparseableJson_ReturnsNothingInsteadOfThrowing()
    {
        var result = PlaywrightWebBrowser.ExtractStructuredData(
            """<script type="application/ld+json">{ this is not json }</script>""");

        result.ShouldBeEmpty();
    }
    // Sites that block automated traffic surface as raw Gecko network codes, which the agent then
    // reported verbatim as "Browser error: NS_ERROR_NET_ERROR_RESPONSE". Production showed three
    // of these (idealo.es twice, arctic.de, elecan3d.com) and in each case the agent could not tell
    // a block from an outage, so it retried a URL that would never work. Each code means something
    // different and must keep its own advice.
    [Theory]
    [InlineData("NS_ERROR_NET_ERROR_RESPONSE", "blocking automated")]
    [InlineData("NS_ERROR_REDIRECT_LOOP", "redirect loop")]
    [InlineData("NS_ERROR_UNKNOWN_HOST", "does not resolve")]
    [InlineData("NS_ERROR_CONNECTION_REFUSED", "refused")]
    [InlineData("NS_ERROR_NET_TIMEOUT", "did not respond")]
    public void DescribeNavigationError_ExplainsGeckoNetworkCodes(string code, string expectedPhrase)
    {
        var message = PlaywrightWebBrowser.DescribeNavigationError(
            $"{code}\nCall log:\n  - navigating to \"https://example.com/x\"");

        message.ShouldContain(expectedPhrase, Case.Insensitive);
        message.ShouldNotContain("Call log");
    }

    // A block must say plainly that retrying will not help, so the agent stops hammering it.
    [Fact]
    public void DescribeNavigationError_ForABlockedRequest_SaysRetryingWillNotHelp()
    {
        var message = PlaywrightWebBrowser.DescribeNavigationError("NS_ERROR_NET_ERROR_RESPONSE");

        message.ShouldContain("web_search", Case.Insensitive);
    }

    // Anything unrecognised must still reach the caller rather than being flattened into a
    // generic string that hides what actually happened.
    [Fact]
    public void DescribeNavigationError_ForAnUnrecognisedError_KeepsTheOriginalMessage()
    {
        const string raw = "Something entirely unexpected happened";

        PlaywrightWebBrowser.DescribeNavigationError(raw).ShouldContain(raw);
    }
}