using Infrastructure.Clients.Browser;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Playwright;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class BrowserSessionManagerTests
{
    // A context that hands out a fresh page per NewPageAsync call, recorded in order, so a test
    // can say which tab's page was closed and which stayed alive.
    private static (Mock<IBrowserContext> Context, List<Mock<IPage>> Pages) CreateContext()
    {
        var pages = new List<Mock<IPage>>();
        var ctx = new Mock<IBrowserContext>();
        ctx.Setup(c => c.NewPageAsync()).ReturnsAsync(() =>
        {
            var page = new Mock<IPage>();
            page.SetupGet(p => p.IsClosed).Returns(false);
            page.Setup(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()))
                .Returns(Task.CompletedTask);
            pages.Add(page);
            return page.Object;
        });
        return (ctx, pages);
    }

    private static Task<TabAcquisition> BrowseAsync(
        BrowserSessionManager manager, Mock<IBrowserContext> ctx, string sessionId, string url) =>
        manager.AcquireTabForBrowseAsync(sessionId, url, ctx.Object);

    [Fact]
    public async Task BrowsingASecondUrl_LeavesTheFirstTabAlive()
    {
        var (ctx, pages) = CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await BrowseAsync(manager, ctx, "s1", "https://b.test/");

        manager.Get("s1")!.Tabs.Count.ShouldBe(2);
        pages.Count.ShouldBe(2);
        pages[0].Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Never);
    }

    [Fact]
    public async Task BrowsingAUrlAlreadyOpen_ReusesItsTabRatherThanOpeningAnother()
    {
        var (ctx, pages) = CreateContext();
        await using var manager = new BrowserSessionManager();

        var first = await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        var again = await BrowseAsync(manager, ctx, "s1", "https://a.test/");

        again.Reused.ShouldBeTrue();
        again.Tab.ShouldBeSameAs(first.Tab);
        pages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task ReuseMatchesAcrossARedirect_ByEitherAddress()
    {
        var (ctx, pages) = CreateContext();
        await using var manager = new BrowserSessionManager();

        // Asked for a.test, landed on b.test — a redirect splits the two addresses.
        var first = await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        manager.NoteFinalUrl("s1", first.Tab, "https://b.test/landing");

        var byRequested = await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        var byFinal = await BrowseAsync(manager, ctx, "s1", "https://b.test/landing");

        byRequested.Reused.ShouldBeTrue();
        byRequested.Tab.ShouldBeSameAs(first.Tab);
        byFinal.Reused.ShouldBeTrue();
        byFinal.Tab.ShouldBeSameAs(first.Tab);
        pages.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AFourthDistinctPage_ClosesTheLeastRecentlyTouchedTab_NotTheOldestCreated()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx, pages) = CreateContext();
        await using var manager = new BrowserSessionManager(timeProvider: time);

        var a = await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        time.Advance(TimeSpan.FromSeconds(1));
        await BrowseAsync(manager, ctx, "s1", "https://b.test/");
        time.Advance(TimeSpan.FromSeconds(1));
        await BrowseAsync(manager, ctx, "s1", "https://c.test/");
        time.Advance(TimeSpan.FromSeconds(1));

        // The oldest-created tab is touched again, so the least-recently-touched is now b.
        manager.TouchTab("s1", a.Tab);
        time.Advance(TimeSpan.FromSeconds(1));

        await BrowseAsync(manager, ctx, "s1", "https://d.test/");

        manager.Get("s1")!.Tabs.Count.ShouldBe(3);
        pages[1].Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Once);
        pages[0].Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Never);
    }

    [Fact]
    public async Task TheTabCap_IsASettingOnTheManager()
    {
        var (ctx, pages) = CreateContext();
        await using var manager = new BrowserSessionManager(tabCap: 2);

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await BrowseAsync(manager, ctx, "s1", "https://b.test/");
        await BrowseAsync(manager, ctx, "s1", "https://c.test/");

        manager.Get("s1")!.Tabs.Count.ShouldBe(2);
        pages[0].Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Once);
    }

    [Fact]
    public async Task AnyRoutedCall_MakesItsTabTheCurrentOne()
    {
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();

        var a = await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        var b = await BrowseAsync(manager, ctx, "s1", "https://b.test/");

        manager.Get("s1")!.CurrentTab.ShouldBeSameAs(b.Tab);

        // A routed call to the older tab — a view_image, an action — refocuses it.
        manager.TouchTab("s1", a.Tab);

        manager.Get("s1")!.CurrentTab.ShouldBeSameAs(a.Tab);
    }

    [Fact]
    public async Task TwoParallelBrowsesOfDifferentUrls_LandOnTwoTabs()
    {
        var (ctx, pages) = CreateContext();
        await using var manager = new BrowserSessionManager();

        var results = await Task.WhenAll(
            BrowseAsync(manager, ctx, "s1", "https://a.test/"),
            BrowseAsync(manager, ctx, "s1", "https://b.test/"));

        results[0].Tab.ShouldNotBeSameAs(results[1].Tab);
        manager.Get("s1")!.Tabs.Count.ShouldBe(2);
        pages.Count.ShouldBe(2);
    }

    private static async Task StampAsync(
        BrowserSessionManager manager, string sessionId, BrowserTab tab, RefNamespace ns, int count)
    {
        using var lease = await manager.BeginStampAsync(sessionId, ns, tab);
        lease.Commit(count);
    }

    [Fact]
    public async Task ARef_RoutesToTheTabThatStampedIt()
    {
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();

        var a = await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await StampAsync(manager, "s1", a.Tab, RefNamespace.Element, 3);
        var b = await BrowseAsync(manager, ctx, "s1", "https://b.test/");
        await StampAsync(manager, "s1", b.Tab, RefNamespace.Element, 2);

        manager.RouteRef("s1", "e-2").ShouldBe(new RefRouting.Routed(a.Tab));
        manager.RouteRef("s1", "e-5").ShouldBe(new RefRouting.Routed(b.Tab));
    }

    [Fact]
    public async Task AnImageRef_RoutesThroughItsOwnNamespace()
    {
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();

        var a = await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await StampAsync(manager, "s1", a.Tab, RefNamespace.Image, 4);
        var b = await BrowseAsync(manager, ctx, "s1", "https://b.test/");
        await StampAsync(manager, "s1", b.Tab, RefNamespace.Element, 4);

        // The same number lives in both namespaces; each ref finds its own.
        manager.RouteRef("s1", "i-3").ShouldBe(new RefRouting.Routed(a.Tab));
        manager.RouteRef("s1", "e-3").ShouldBe(new RefRouting.Routed(b.Tab));
    }

    [Fact]
    public async Task ARefWhoseNumberOverflows_IsUnknownRatherThanAnException()
    {
        // The shape predicates accept any digit run, so a model-invented ref past int.MaxValue
        // must fall to the unknown wall, not throw out of routing as a retryable internal error.
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");

        manager.RouteRef("s1", "e-99999999999999999999").ShouldBe(new RefRouting.Unknown());
        manager.RouteRef("s1", "i-99999999999999999999").ShouldBe(new RefRouting.Unknown());
    }

    [Fact]
    public async Task AnAdditiveStamp_LeavesTheEarlierRefsRouting()
    {
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();

        // A non-navigating action stamps only the elements that appeared — the refs the model is
        // mid-flow with keep resolving, or a four-field form dies after its first fill.
        var a = await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await StampAsync(manager, "s1", a.Tab, RefNamespace.Element, 3);
        using (var lease = await manager.BeginStampAsync("s1", RefNamespace.Element, a.Tab))
        {
            lease.Commit(2, supersede: false);
        }

        manager.RouteRef("s1", "e-2").ShouldBe(new RefRouting.Routed(a.Tab));
        manager.RouteRef("s1", "e-5").ShouldBe(new RefRouting.Routed(a.Tab));
    }

    [Fact]
    public async Task ARefRenumberedByALaterStamp_AnswersSuperseded()
    {
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();

        var a = await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await StampAsync(manager, "s1", a.Tab, RefNamespace.Element, 3);
        await StampAsync(manager, "s1", a.Tab, RefNamespace.Element, 3);

        // The tab is open; only the numbers moved on.
        manager.RouteRef("s1", "e-2").ShouldBe(new RefRouting.Superseded("https://a.test/"));
        manager.RouteRef("s1", "e-5").ShouldBe(new RefRouting.Routed(a.Tab));
    }

    [Fact]
    public async Task AnInTabNavigation_SupersedesEverythingTheTabStamped()
    {
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();

        var a = await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await StampAsync(manager, "s1", a.Tab, RefNamespace.Image, 2);
        await StampAsync(manager, "s1", a.Tab, RefNamespace.Element, 2);

        manager.MarkTabNavigated("s1", a.Tab);

        manager.RouteRef("s1", "i-1").ShouldBe(new RefRouting.Superseded("https://a.test/"));
        manager.RouteRef("s1", "e-1").ShouldBe(new RefRouting.Superseded("https://a.test/"));
    }

    [Fact]
    public async Task ARefFromAnEvictedTab_AnswersClosed_NamingTheUrlItBelongedTo()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager(timeProvider: time);

        var a = await BrowseAsync(manager, ctx, "s1", "https://a.test/product");
        await StampAsync(manager, "s1", a.Tab, RefNamespace.Image, 2);
        time.Advance(TimeSpan.FromSeconds(1));
        await BrowseAsync(manager, ctx, "s1", "https://b.test/");
        time.Advance(TimeSpan.FromSeconds(1));
        await BrowseAsync(manager, ctx, "s1", "https://c.test/");
        time.Advance(TimeSpan.FromSeconds(1));
        await BrowseAsync(manager, ctx, "s1", "https://d.test/");

        // The tombstone survives the tab: the wall names the page to browse again.
        manager.RouteRef("s1", "i-1").ShouldBe(new RefRouting.Closed("https://a.test/product"));
    }

    [Fact]
    public async Task TheRegistry_HoldsNothingAfterItsSessionCloses()
    {
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();

        var a = await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await StampAsync(manager, "s1", a.Tab, RefNamespace.Element, 3);

        await manager.CloseAsync("s1");

        // Session death is not a third wall: the existing dead-session refusal answers.
        manager.RouteRef("s1", "e-1").ShouldBe(new RefRouting.NoSession());
    }

    [Fact]
    public async Task TheRegistry_IsBounded_AndAnAgedOutRefRoutesNowhere()
    {
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();

        var a = await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        foreach (var _ in Enumerable.Range(0, 200))
        {
            await StampAsync(manager, "s1", a.Tab, RefNamespace.Element, 1);
        }

        // The newest entry still routes; the oldest fell off rather than growing without limit.
        manager.RouteRef("s1", "e-200").ShouldBe(new RefRouting.Routed(a.Tab));
        manager.RouteRef("s1", "e-1").ShouldBe(new RefRouting.Unknown());
    }

    [Fact]
    public async Task ARefNeverIssued_RoutesNowhere()
    {
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();

        var a = await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await StampAsync(manager, "s1", a.Tab, RefNamespace.Element, 2);

        manager.RouteRef("s1", "e-99").ShouldBe(new RefRouting.Unknown());
        manager.RouteRef("s1", "not-a-ref").ShouldBe(new RefRouting.Unknown());
    }

    [Fact]
    public async Task ARefFromAMissingSession_SaysSo()
    {
        await using var manager = new BrowserSessionManager();

        manager.RouteRef("nobody", "e-1").ShouldBe(new RefRouting.NoSession());
    }

    private static Mock<IPage> CreatePopupPage()
    {
        var page = new Mock<IPage>();
        page.SetupGet(p => p.IsClosed).Returns(false);
        page.SetupGet(p => p.Url).Returns("https://popup.test/window");
        page.Setup(p => p.CloseAsync(It.IsAny<PageCloseOptions?>())).Returns(Task.CompletedTask);
        return page;
    }

    [Fact]
    public async Task AnAdoptedPopup_JoinsThePoolAndBecomesCurrent()
    {
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        var popup = CreatePopupPage();

        var adopted = await manager.AdoptPopupAsync("s1", popup.Object);

        var session = manager.Get("s1")!;
        session.Tabs.Count.ShouldBe(2);
        session.CurrentTab.ShouldBeSameAs(adopted);
        adopted!.RequestedUrl.ShouldBe("https://popup.test/window");
    }

    [Fact]
    public async Task AdoptingAPopup_AtTheCap_EvictsTheLeastRecentlyTouched()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx, pages) = CreateContext();
        await using var manager = new BrowserSessionManager(timeProvider: time);

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        time.Advance(TimeSpan.FromSeconds(1));
        await BrowseAsync(manager, ctx, "s1", "https://b.test/");
        time.Advance(TimeSpan.FromSeconds(1));
        await BrowseAsync(manager, ctx, "s1", "https://c.test/");
        time.Advance(TimeSpan.FromSeconds(1));

        await manager.AdoptPopupAsync("s1", CreatePopupPage().Object);

        manager.Get("s1")!.Tabs.Count.ShouldBe(3);
        pages[0].Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Once);
    }

    [Fact]
    public async Task AnAdoptedPopup_IsPendingForTheActionThatSpawnedIt_ExactlyOnce()
    {
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        var adopted = await manager.AdoptPopupAsync("s1", CreatePopupPage().Object);

        manager.TakePendingPopup("s1").ShouldBeSameAs(adopted);
        manager.TakePendingPopup("s1").ShouldBeNull();
    }

    [Fact]
    public async Task AnAdoptedPopup_DiesWithItsSession()
    {
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        var popup = CreatePopupPage();
        await manager.AdoptPopupAsync("s1", popup.Object);

        await manager.CloseAsync("s1");

        popup.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Once);
    }

    [Fact]
    public async Task ATouchJustBeforeTheThreshold_ResetsTheIdleClock()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx, pages) = CreateContext();
        await using var manager = new BrowserSessionManager(
            timeProvider: time,
            idleTimeout: TimeSpan.FromMinutes(30));

        var tab = await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        time.Advance(TimeSpan.FromMinutes(25));

        // Re-touch just before threshold
        manager.TouchTab("s1", tab.Tab);
        time.Advance(TimeSpan.FromMinutes(10));

        await manager.PruneIdleAsync();

        // Total elapsed since last touch = 10 min < 30 min threshold
        manager.Get("s1").ShouldNotBeNull();
        pages[0].Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Never);
    }

    [Fact]
    public async Task OnlyIdleSessions_ArePruned()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx, pages) = CreateContext();
        await using var manager = new BrowserSessionManager(
            timeProvider: time,
            idleTimeout: TimeSpan.FromMinutes(30));

        await BrowseAsync(manager, ctx, "idle", "https://a.test/");
        time.Advance(TimeSpan.FromMinutes(25));
        await BrowseAsync(manager, ctx, "active", "https://b.test/");
        time.Advance(TimeSpan.FromMinutes(10));

        await manager.PruneIdleAsync();

        manager.Get("idle").ShouldBeNull();
        manager.Get("active").ShouldNotBeNull();
        pages[0].Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Once);
        pages[1].Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Never);
    }

    [Fact]
    public async Task ClearingTheManager_DropsEverySession()
    {
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await BrowseAsync(manager, ctx, "s2", "https://b.test/");

        manager.Clear();

        manager.Get("s1").ShouldBeNull();
        manager.Get("s2").ShouldBeNull();
    }

    [Fact]
    public async Task ASessionDoingOnlyRoutedCalls_IsNeverIdlePruned()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager(
            timeProvider: time,
            idleTimeout: TimeSpan.FromMinutes(30));

        var tab = await BrowseAsync(manager, ctx, "s1", "https://a.test/");

        // Fifty minutes of actions, snapshots and image fetches — every one a touch — with no
        // navigation anywhere. The one clock is refreshed by all of them.
        foreach (var _ in Enumerable.Range(0, 5))
        {
            time.Advance(TimeSpan.FromMinutes(10));
            manager.TouchTab("s1", tab.Tab);
        }

        time.Advance(TimeSpan.FromMinutes(10));
        await manager.PruneIdleAsync();

        manager.Get("s1").ShouldNotBeNull();
    }

    [Fact]
    public async Task TwoCallsOnOneTab_SerializeOnItsLock()
    {
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();
        var a = await BrowseAsync(manager, ctx, "s1", "https://a.test/");

        var first = await manager.LockTabAsync(a.Tab);
        var second = manager.LockTabAsync(a.Tab);

        // A snapshot or fetch racing a navigation on the same tab waits its turn.
        (await Task.WhenAny(second, Eventually.Settle())).ShouldNotBe(second);

        first.Dispose();

        (await Task.WhenAny(second, Task.Delay(1000))).ShouldBe(second);
        (await second).Dispose();
    }

    [Fact]
    public async Task TwoCallsOnDifferentTabsOfOneSession_DoNotSerialize()
    {
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();
        var a = await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        var b = await BrowseAsync(manager, ctx, "s1", "https://b.test/");

        using var first = await manager.LockTabAsync(a.Tab);
        var second = manager.LockTabAsync(b.Tab);

        (await Task.WhenAny(second, Task.Delay(1000))).ShouldBe(second);
        (await second).Dispose();
    }

    [Fact]
    public async Task ASecondStamp_ContinuesWhereTheFirstStopped()
    {
        await using var manager = new BrowserSessionManager();
        await manager.GetOrCreateAsync("s1");

        using (var first = await manager.BeginStampAsync("s1", RefNamespace.Element))
        {
            first.Start.ShouldBe(1);
            first.Commit(5);
        }

        using var second = await manager.BeginStampAsync("s1", RefNamespace.Element);
        second.Start.ShouldBe(6);
    }

    [Fact]
    public async Task AStampThatCommitsNothing_MovesNoNumber()
    {
        await using var manager = new BrowserSessionManager();
        await manager.GetOrCreateAsync("s1");

        using (var first = await manager.BeginStampAsync("s1", RefNamespace.Image))
        {
            first.Start.ShouldBe(1);
        }

        using var second = await manager.BeginStampAsync("s1", RefNamespace.Image);
        second.Start.ShouldBe(1);
    }

    [Fact]
    public async Task TheTwoNamespaces_CountIndependently()
    {
        await using var manager = new BrowserSessionManager();
        await manager.GetOrCreateAsync("s1");

        using (var images = await manager.BeginStampAsync("s1", RefNamespace.Image))
        {
            images.Commit(7);
        }

        using var elements = await manager.BeginStampAsync("s1", RefNamespace.Element);
        elements.Start.ShouldBe(1);

        using var moreImages = await manager.BeginStampAsync("s1", RefNamespace.Image);
        moreImages.Start.ShouldBe(8);
    }

    [Fact]
    public async Task TwoStampsOnOneSession_NeverShareNumbers()
    {
        await using var manager = new BrowserSessionManager();
        await manager.GetOrCreateAsync("s1");

        var first = await manager.BeginStampAsync("s1", RefNamespace.Element);
        var second = manager.BeginStampAsync("s1", RefNamespace.Element);

        // The second stamp must wait: handing both the same start would reissue numbers.
        (await Task.WhenAny(second, Eventually.Settle())).ShouldNotBe(second);

        first.Commit(3);
        first.Dispose();

        (await Task.WhenAny(second, Task.Delay(1000))).ShouldBe(second);
        using var released = await second;
        released.Start.ShouldBe(4);
    }

    [Fact]
    public async Task TheBackgroundTimer_PrunesOnItsInterval()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx, pages) = CreateContext();
        await using var manager = new BrowserSessionManager(
            timeProvider: time,
            idleTimeout: TimeSpan.FromMinutes(30),
            pruneInterval: TimeSpan.FromMinutes(5));

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        time.Advance(TimeSpan.FromMinutes(31));

        // The prune runs from a timer callback, so a hundred milliseconds was a bet that the
        // callback got a thread inside them rather than a statement about pruning.
        await Eventually.Until(() => manager.Get("s1") is null, "the idle session to be pruned");
        pages[0].Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Once);
    }
}