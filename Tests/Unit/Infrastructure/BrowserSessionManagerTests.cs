using Infrastructure.Clients.Browser;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Playwright;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// The tab-pool policy facts — LRU, reuse-before-open, ranges, tombstones, counters, idle pruning —
// asserted through the four per-intent methods and the pool lifecycle, against faked pages. The
// old suite drove the protocol verbs directly, which made it the caller: it performed the
// sequencing instead of checking it, and stayed green through eleven sequencing bugs.
public class BrowserSessionManagerTests
{
    private static Task<TabOutcome<IPage>> BrowseAsync(
        BrowserSessionManager manager, Mock<IBrowserContext> ctx, string sessionId, string url) =>
        manager.BrowseAsync(sessionId, url, ctx.Object, cx => Task.FromResult(cx.Page));

    private static IPage RanOn(TabOutcome<IPage> outcome) =>
        outcome.ShouldBeOfType<TabOutcome<IPage>.Ran>().Result;

    // Stamps a range on the current tab, the way a snapshot or the browse's annotate pass does.
    private static async Task StampAsync(
        BrowserSessionManager manager, string sessionId, RefNamespace ns, int count)
    {
        var outcome = await manager.OnCurrentTabAsync(
            sessionId, StampPolicy.Restamp(ns),
            async cx =>
            {
                await cx.StampAsync(_ => Task.FromResult(count));
                return true;
            });
        outcome.ShouldBeOfType<TabOutcome<bool>.Ran>();
    }

    // Where a ref lands, observed as the model would: the wall, or the page the work ran on.
    private static Task<TabOutcome<IPage>> RouteAsync(
        BrowserSessionManager manager, string sessionId, string refString) =>
        manager.OnRefAsync(sessionId, refString, StampPolicy.None, cx => Task.FromResult(cx.Page));

    // The page a ref-less call lands on — the pool's idea of the current tab.
    private static async Task<IPage> CurrentPageAsync(BrowserSessionManager manager, string sessionId)
    {
        var outcome = await manager.OnCurrentTabAsync(
            sessionId, StampPolicy.None, cx => Task.FromResult(cx.Page));
        return RanOn(outcome);
    }

    [Fact]
    public async Task BrowsingASecondUrl_LeavesTheFirstTabAlive()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await BrowseAsync(manager, ctx, "s1", "https://b.test/");

        pages.Count.ShouldBe(2);
        pages[0].Mock.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Never);

        // The first page still answers a call addressed to its URL.
        var onFirst = await manager.OnUrlAsync(
            "s1", "https://a.test/", StampPolicy.None, cx => Task.FromResult(cx.Page));
        RanOn(onFirst).ShouldBeSameAs(pages[0].Mock.Object);
    }

    [Fact]
    public async Task BrowsingAUrlAlreadyOpen_ReusesItsTabRatherThanOpeningAnother()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        var first = await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        var again = await BrowseAsync(manager, ctx, "s1", "https://a.test/");

        pages.Count.ShouldBe(1);
        RanOn(again).ShouldBeSameAs(RanOn(first));
    }

    [Fact]
    public async Task ReuseMatchesAcrossARedirect_ByEitherAddress()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        // Asked for a.test, landed on b.test — a redirect splits the two addresses.
        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object, cx =>
        {
            pages[0].Url = "https://b.test/landing";
            return Task.FromResult(true);
        });

        var byRequested = await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        var byFinal = await BrowseAsync(manager, ctx, "s1", "https://b.test/landing");

        pages.Count.ShouldBe(1);
        RanOn(byRequested).ShouldBeSameAs(pages[0].Mock.Object);
        RanOn(byFinal).ShouldBeSameAs(pages[0].Mock.Object);
    }

    [Fact]
    public async Task AFourthDistinctPage_ClosesTheLeastRecentlyTouchedTab_NotTheOldestCreated()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager(timeProvider: time);

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        time.Advance(TimeSpan.FromSeconds(1));
        await BrowseAsync(manager, ctx, "s1", "https://b.test/");
        time.Advance(TimeSpan.FromSeconds(1));
        await BrowseAsync(manager, ctx, "s1", "https://c.test/");
        time.Advance(TimeSpan.FromSeconds(1));

        // The oldest-created tab is touched again by a routed call, so the least-recently-touched
        // is now b.
        await manager.OnUrlAsync("s1", "https://a.test/", StampPolicy.None, _ => Task.FromResult(true));
        time.Advance(TimeSpan.FromSeconds(1));

        await BrowseAsync(manager, ctx, "s1", "https://d.test/");

        pages[1].Mock.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Once);
        pages[0].Mock.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Never);
    }

    [Fact]
    public async Task TheTabCap_IsASettingOnTheManager()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager(tabCap: 2);

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await BrowseAsync(manager, ctx, "s1", "https://b.test/");
        await BrowseAsync(manager, ctx, "s1", "https://c.test/");

        pages[0].Mock.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Once);
        pages[1].Mock.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Never);
    }

    [Fact]
    public async Task AnyRoutedCall_MakesItsTabTheCurrentOne()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await BrowseAsync(manager, ctx, "s1", "https://b.test/");

        (await CurrentPageAsync(manager, "s1")).ShouldBeSameAs(pages[1].Mock.Object);

        // A routed call to the older tab — a view_image, an action — refocuses it.
        await manager.OnUrlAsync("s1", "https://a.test/", StampPolicy.None, _ => Task.FromResult(true));

        (await CurrentPageAsync(manager, "s1")).ShouldBeSameAs(pages[0].Mock.Object);
    }

    [Fact]
    public async Task TwoParallelBrowsesOfDifferentUrls_LandOnTwoTabs()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        var results = await Task.WhenAll(
            BrowseAsync(manager, ctx, "s1", "https://a.test/"),
            BrowseAsync(manager, ctx, "s1", "https://b.test/"));

        pages.Count.ShouldBe(2);
        RanOn(results[0]).ShouldNotBeSameAs(RanOn(results[1]));
    }

    [Fact]
    public async Task ARef_RoutesToTheTabThatStampedIt()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await StampAsync(manager, "s1", RefNamespace.Element, 3);
        await BrowseAsync(manager, ctx, "s1", "https://b.test/");
        await StampAsync(manager, "s1", RefNamespace.Element, 2);

        RanOn(await RouteAsync(manager, "s1", "e-2")).ShouldBeSameAs(pages[0].Mock.Object);
        RanOn(await RouteAsync(manager, "s1", "e-5")).ShouldBeSameAs(pages[1].Mock.Object);
    }

    [Fact]
    public async Task AnImageRef_RoutesThroughItsOwnNamespace()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await StampAsync(manager, "s1", RefNamespace.Image, 4);
        await BrowseAsync(manager, ctx, "s1", "https://b.test/");
        await StampAsync(manager, "s1", RefNamespace.Element, 4);

        // The same number lives in both namespaces; each ref finds its own.
        RanOn(await RouteAsync(manager, "s1", "i-3")).ShouldBeSameAs(pages[0].Mock.Object);
        RanOn(await RouteAsync(manager, "s1", "e-3")).ShouldBeSameAs(pages[1].Mock.Object);
    }

    [Fact]
    public async Task ARefWhoseNumberOverflows_FallsToTheCurrentTab_NotAnException()
    {
        // The shape predicates accept any digit run, so a model-invented ref past int.MaxValue
        // must land on the current tab's ordinary not-found, not throw out of routing.
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");

        RanOn(await RouteAsync(manager, "s1", "e-99999999999999999999"))
            .ShouldBeSameAs(pages[0].Mock.Object);
        RanOn(await RouteAsync(manager, "s1", "i-99999999999999999999"))
            .ShouldBeSameAs(pages[0].Mock.Object);
    }

    [Fact]
    public async Task ARefRenumberedByALaterStamp_AnswersSuperseded()
    {
        var (ctx, _) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await StampAsync(manager, "s1", RefNamespace.Element, 3);
        await StampAsync(manager, "s1", RefNamespace.Element, 3);

        // The tab is open; only the numbers moved on.
        (await RouteAsync(manager, "s1", "e-2"))
            .ShouldBe(new TabOutcome<IPage>.Superseded("https://a.test/"));
        (await RouteAsync(manager, "s1", "e-5")).ShouldBeOfType<TabOutcome<IPage>.Ran>();
    }

    [Fact]
    public async Task AnInTabNavigation_SupersedesEverythingTheTabStamped()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await StampAsync(manager, "s1", RefNamespace.Image, 2);
        await StampAsync(manager, "s1", RefNamespace.Element, 2);

        // A work that moves the URL is an in-tab navigation: the document was replaced.
        await manager.OnCurrentTabAsync("s1", StampPolicy.None, _ =>
        {
            pages[0].Url = "https://a.test/next";
            return Task.FromResult(true);
        });

        (await RouteAsync(manager, "s1", "i-1"))
            .ShouldBe(new TabOutcome<IPage>.Superseded("https://a.test/"));
        (await RouteAsync(manager, "s1", "e-1"))
            .ShouldBe(new TabOutcome<IPage>.Superseded("https://a.test/"));
    }

    [Fact]
    public async Task ARefFromAnEvictedTab_AnswersClosed_NamingTheUrlItBelongedTo()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx, _) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager(timeProvider: time);

        await BrowseAsync(manager, ctx, "s1", "https://a.test/product");
        await StampAsync(manager, "s1", RefNamespace.Image, 2);
        time.Advance(TimeSpan.FromSeconds(1));
        await BrowseAsync(manager, ctx, "s1", "https://b.test/");
        time.Advance(TimeSpan.FromSeconds(1));
        await BrowseAsync(manager, ctx, "s1", "https://c.test/");
        time.Advance(TimeSpan.FromSeconds(1));
        await BrowseAsync(manager, ctx, "s1", "https://d.test/");

        // The tombstone survives the tab: the wall names the page to browse again.
        (await RouteAsync(manager, "s1", "i-1"))
            .ShouldBe(new TabOutcome<IPage>.Closed("https://a.test/product"));
    }

    [Fact]
    public async Task TheRegistry_HoldsNothingAfterItsSessionCloses()
    {
        var (ctx, _) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await StampAsync(manager, "s1", RefNamespace.Element, 3);

        await manager.CloseAsync("s1");

        // Session death is not a third wall: the existing dead-session refusal answers.
        (await RouteAsync(manager, "s1", "e-1")).ShouldBe(new TabOutcome<IPage>.NoSession());
    }

    [Fact]
    public async Task TheRegistry_IsBounded_AndAnAgedOutRefFallsToTheCurrentTab()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        foreach (var _ in Enumerable.Range(0, 200))
        {
            await StampAsync(manager, "s1", RefNamespace.Element, 1);
        }

        await BrowseAsync(manager, ctx, "s1", "https://b.test/");

        // The oldest entry fell off rather than growing without limit, so it lands on the current
        // tab's ordinary not-found; the newest still routes to its own tab. The aged-out ref is
        // asked first, because routing e-200 touches its tab and would move "current".
        RanOn(await RouteAsync(manager, "s1", "e-1")).ShouldBeSameAs(pages[1].Mock.Object);
        RanOn(await RouteAsync(manager, "s1", "e-200")).ShouldBeSameAs(pages[0].Mock.Object);
    }

    [Fact]
    public async Task ARefNeverIssued_FallsToTheCurrentTab()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await StampAsync(manager, "s1", RefNamespace.Element, 2);
        await BrowseAsync(manager, ctx, "s1", "https://b.test/");

        RanOn(await RouteAsync(manager, "s1", "e-99")).ShouldBeSameAs(pages[1].Mock.Object);
        RanOn(await RouteAsync(manager, "s1", "not-a-ref")).ShouldBeSameAs(pages[1].Mock.Object);
    }

    [Fact]
    public async Task ARefFromAMissingSession_SaysSo()
    {
        await using var manager = new BrowserSessionManager();

        (await RouteAsync(manager, "nobody", "e-1")).ShouldBe(new TabOutcome<IPage>.NoSession());
    }

    [Fact]
    public async Task AnAdoptedPopup_JoinsThePoolAndBecomesCurrent()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        var popup = FakePage.Create("https://popup.test/window");

        pages[0].RaisePopup(popup);

        // It became the tab the next ref-less call addresses, and it answers by its URL.
        (await CurrentPageAsync(manager, "s1")).ShouldBeSameAs(popup.Mock.Object);
        var byUrl = await manager.OnUrlAsync(
            "s1", "https://popup.test/window", StampPolicy.None, cx => Task.FromResult(cx.Page));
        RanOn(byUrl).ShouldBeSameAs(popup.Mock.Object);
    }

    [Fact]
    public async Task AdoptingAPopup_AtTheCap_EvictsTheLeastRecentlyTouched()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager(timeProvider: time);

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        time.Advance(TimeSpan.FromSeconds(1));
        await BrowseAsync(manager, ctx, "s1", "https://b.test/");
        time.Advance(TimeSpan.FromSeconds(1));
        await BrowseAsync(manager, ctx, "s1", "https://c.test/");
        time.Advance(TimeSpan.FromSeconds(1));

        pages[2].RaisePopup(FakePage.Create("https://popup.test/window"));

        await Eventually.Until(
            () => pages[0].Mock.Invocations.Any(i => i.Method.Name == nameof(IPage.CloseAsync)),
            "the least-recently-touched tab to be evicted for the popup");
        pages[1].Mock.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Never);
    }

    [Fact]
    public async Task APopupPendingOnAnotherTab_DoesNotAnswerThisTabsAction()
    {
        // Two tabs of one session: the popup tab A's click spawned must answer A's action, never
        // one on B — a session-global handoff let B swallow A's popup and claim its URL and
        // snapshot as its own answer.
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await StampAsync(manager, "s1", RefNamespace.Element, 1);
        await BrowseAsync(manager, ctx, "s1", "https://b.test/");
        await StampAsync(manager, "s1", RefNamespace.Element, 1);

        // A popup lands pending on tab a outside any action.
        pages[0].RaisePopup(FakePage.Create("https://popup.test/window"));

        // The action on tab b answers from its own tab.
        var onB = await manager.OnRefAsync(
            "s1", "e-2", StampPolicy.AugmentUnlessNavigated(RefNamespace.Element),
            act: _ => Task.FromResult<string?>(null),
            answer: _ => Task.FromResult("from b"),
            popup: new PopupAnswer<string>(
                StampPolicy.Restamp(RefNamespace.Element), _ => Task.FromResult("from the popup")));

        var ran = onB.ShouldBeOfType<TabOutcome<string>.Ran>();
        ran.PopupAnswered.ShouldBeFalse();
        ran.Result.ShouldBe("from b");
    }

    [Fact]
    public async Task APopupAdoptedBeforeItsNavigation_BecomesKnownByWhereItLanded()
    {
        // window.open('') adopts at about:blank; the walls must name the address the popup landed
        // on, because "Browse about:blank again" is a recovery nobody can use.
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await StampAsync(manager, "s1", RefNamespace.Element, 1);
        var popup = FakePage.Create();

        var outcome = await manager.OnRefAsync(
            "s1", "e-1", StampPolicy.AugmentUnlessNavigated(RefNamespace.Element),
            act: _ =>
            {
                pages[0].RaisePopup(popup);
                return Task.FromResult<string?>(null);
            },
            answer: _ => Task.FromResult("from the acting tab"),
            popup: new PopupAnswer<string>(StampPolicy.Restamp(RefNamespace.Element), _ =>
            {
                // The popup's navigation lands while it is being read.
                popup.Url = "https://popup.test/landed";
                return Task.FromResult("from the popup");
            }));

        outcome.ShouldBeOfType<TabOutcome<string>.Ran>().PopupAnswered.ShouldBeTrue();

        // The landed address is the one the popup is now known by.
        var byUrl = await manager.OnUrlAsync(
            "s1", "https://popup.test/landed", StampPolicy.None, cx => Task.FromResult(cx.Page));
        RanOn(byUrl).ShouldBeSameAs(popup.Mock.Object);
    }

    [Fact]
    public async Task AnAdoptedPopup_DiesWithItsSession()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        var popup = FakePage.Create("https://popup.test/window");
        pages[0].RaisePopup(popup);

        await manager.CloseAsync("s1");

        popup.Mock.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Once);
    }

    [Fact]
    public async Task ARestampThatFoundNothing_StillSupersedesTheOldRefs()
    {
        // A restamping pass wipes every stamp off the document before it counts; finding nothing
        // to stamp does not put them back. The old refs must refuse as superseded, not route to a
        // page that no longer carries them.
        var (ctx, _) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await StampAsync(manager, "s1", RefNamespace.Element, 3);
        await StampAsync(manager, "s1", RefNamespace.Element, 0);

        (await RouteAsync(manager, "s1", "e-1"))
            .ShouldBe(new TabOutcome<IPage>.Superseded("https://a.test/"));
    }

    [Fact]
    public async Task AFailedPageCreation_DoesNotEvictTheTabItWouldHaveReplaced()
    {
        // Eviction is committed only once the replacement page exists: a transient failure to open
        // a page must not permanently kill an innocent tab and its refs.
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager(tabCap: 1);

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await StampAsync(manager, "s1", RefNamespace.Element, 1);
        ctx.Setup(c => c.NewPageAsync()).ThrowsAsync(new PlaywrightException("boom"));

        await Should.ThrowAsync<PlaywrightException>(
            () => BrowseAsync(manager, ctx, "s1", "https://b.test/"));

        pages[0].Mock.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Never);
        RanOn(await RouteAsync(manager, "s1", "e-1")).ShouldBeSameAs(pages[0].Mock.Object);
    }

    [Fact]
    public async Task ATouchJustBeforeTheThreshold_ResetsTheIdleClock()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager(
            timeProvider: time,
            idleTimeout: TimeSpan.FromMinutes(30));

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        time.Advance(TimeSpan.FromMinutes(25));

        // Any routed call just before the threshold resets the one clock.
        await manager.OnCurrentTabAsync("s1", StampPolicy.None, _ => Task.FromResult(true));
        time.Advance(TimeSpan.FromMinutes(10));

        await manager.PruneIdleAsync();

        // Total elapsed since the last touch = 10 min < 30 min threshold.
        manager.Get("s1").ShouldNotBeNull();
        pages[0].Mock.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Never);
    }

    [Fact]
    public async Task OnlyIdleSessions_ArePruned()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx, pages) = TabPoolFakes.CreateContext();
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
        pages[0].Mock.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Once);
        pages[1].Mock.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Never);
    }

    [Fact]
    public async Task ClearingTheManager_DropsEverySession()
    {
        var (ctx, _) = TabPoolFakes.CreateContext();
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
        var (ctx, _) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager(
            timeProvider: time,
            idleTimeout: TimeSpan.FromMinutes(30));

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");

        // Fifty minutes of actions, snapshots and image fetches — every one a touch — with no
        // navigation anywhere. The one clock is refreshed by all of them.
        foreach (var _ in Enumerable.Range(0, 5))
        {
            time.Advance(TimeSpan.FromMinutes(10));
            await manager.OnCurrentTabAsync("s1", StampPolicy.None, _ => Task.FromResult(true));
        }

        time.Advance(TimeSpan.FromMinutes(10));
        await manager.PruneIdleAsync();

        manager.Get("s1").ShouldNotBeNull();
    }

    [Fact]
    public async Task TwoCallsOnOneTab_SerializeOnItsLock()
    {
        var (ctx, _) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();
        await BrowseAsync(manager, ctx, "s1", "https://a.test/");

        var firstEntered = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var first = manager.OnCurrentTabAsync("s1", StampPolicy.None, async _ =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return true;
        });
        await firstEntered.Task;

        var secondEntered = false;
        var second = manager.OnCurrentTabAsync("s1", StampPolicy.None, _ =>
        {
            secondEntered = true;
            return Task.FromResult(true);
        });

        // A snapshot or fetch racing a navigation on the same tab waits its turn.
        await Eventually.Settle();
        secondEntered.ShouldBeFalse();

        releaseFirst.SetResult();
        await first;
        await second;
        secondEntered.ShouldBeTrue();
    }

    [Fact]
    public async Task TwoCallsOnDifferentTabsOfOneSession_DoNotSerialize()
    {
        var (ctx, _) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();
        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await BrowseAsync(manager, ctx, "s1", "https://b.test/");

        var releaseFirst = new TaskCompletionSource();
        var first = manager.OnUrlAsync("s1", "https://a.test/", StampPolicy.None, async _ =>
        {
            await releaseFirst.Task;
            return true;
        });

        var second = manager.OnUrlAsync("s1", "https://b.test/", StampPolicy.None, _ => Task.FromResult(true));

        (await Task.WhenAny(second, Task.Delay(1000))).ShouldBe(second);

        releaseFirst.SetResult();
        await first;
    }

    private static async Task<int> StampStartAsync(
        BrowserSessionManager manager, string sessionId, RefNamespace ns, int count)
    {
        var start = 0;
        var outcome = await manager.OnCurrentTabAsync(
            sessionId, StampPolicy.Restamp(ns),
            async cx =>
            {
                await cx.StampAsync(s =>
                {
                    start = s;
                    return Task.FromResult(count);
                });
                return true;
            });
        outcome.ShouldBeOfType<TabOutcome<bool>.Ran>();
        return start;
    }

    [Fact]
    public async Task ASecondStamp_ContinuesWhereTheFirstStopped()
    {
        var (ctx, _) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();
        await BrowseAsync(manager, ctx, "s1", "https://a.test/");

        (await StampStartAsync(manager, "s1", RefNamespace.Element, 5)).ShouldBe(1);
        (await StampStartAsync(manager, "s1", RefNamespace.Element, 0)).ShouldBe(6);
    }

    [Fact]
    public async Task AStampThatCommitsNothing_MovesNoNumber()
    {
        var (ctx, _) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();
        await BrowseAsync(manager, ctx, "s1", "https://a.test/");

        (await StampStartAsync(manager, "s1", RefNamespace.Image, 0)).ShouldBe(1);
        (await StampStartAsync(manager, "s1", RefNamespace.Image, 0)).ShouldBe(1);
    }

    [Fact]
    public async Task TheTwoNamespaces_CountIndependently()
    {
        var (ctx, _) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();
        await BrowseAsync(manager, ctx, "s1", "https://a.test/");

        (await StampStartAsync(manager, "s1", RefNamespace.Image, 7)).ShouldBe(1);
        (await StampStartAsync(manager, "s1", RefNamespace.Element, 0)).ShouldBe(1);
        (await StampStartAsync(manager, "s1", RefNamespace.Image, 0)).ShouldBe(8);
    }

    [Fact]
    public async Task TwoStampsOnOneSession_NeverShareNumbers()
    {
        // Two tabs stamping the one namespace: the second lease must wait for the first to commit,
        // or both would peek the same start and reissue every number the slower one writes.
        var (ctx, _) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();
        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        await BrowseAsync(manager, ctx, "s1", "https://b.test/");

        var firstStamping = new TaskCompletionSource();
        var releaseFirst = new TaskCompletionSource();
        var first = manager.OnUrlAsync(
            "s1", "https://a.test/", StampPolicy.Restamp(RefNamespace.Element),
            async cx =>
            {
                await cx.StampAsync(async _ =>
                {
                    firstStamping.SetResult();
                    await releaseFirst.Task;
                    return 3;
                });
                return true;
            });
        await firstStamping.Task;

        var secondStart = 0;
        var second = manager.OnUrlAsync(
            "s1", "https://b.test/", StampPolicy.Restamp(RefNamespace.Element),
            async cx =>
            {
                await cx.StampAsync(s =>
                {
                    secondStart = s;
                    return Task.FromResult(2);
                });
                return true;
            });

        (await Task.WhenAny(second, Eventually.Settle())).ShouldNotBe(second);

        releaseFirst.SetResult();
        await first;
        await second;
        secondStart.ShouldBe(4);
    }

    [Fact]
    public async Task TheBackgroundTimer_PrunesOnItsInterval()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager(
            timeProvider: time,
            idleTimeout: TimeSpan.FromMinutes(30),
            pruneInterval: TimeSpan.FromMinutes(5));

        await BrowseAsync(manager, ctx, "s1", "https://a.test/");
        time.Advance(TimeSpan.FromMinutes(31));

        // The prune runs from a timer callback, so a hundred milliseconds was a bet that the
        // callback got a thread inside them rather than a statement about pruning.
        await Eventually.Until(() => manager.Get("s1") is null, "the idle session to be pruned");
        pages[0].Mock.Verify(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()), Times.Once);
    }
}