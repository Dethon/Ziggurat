using Infrastructure.Clients.Browser;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Playwright;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// The ordering facts of the tab protocol, asserted through the per-intent methods against faked
// pages. These are the sequencing bugs the old verb-driven suite could never see, because that
// suite performed the sequencing itself.
public class TabProtocolTests
{
    private sealed class FakePage
    {
        public required Mock<IPage> Mock { get; init; }
        public bool Closed { get; set; }
        public string Url { get; set; } = "about:blank";
    }

    // A context that hands out a fresh page per NewPageAsync call, recorded in order, with a
    // per-page closed flag the test can flip mid-protocol.
    private static (Mock<IBrowserContext> Context, List<FakePage> Pages) CreateContext(
        bool pagesStartClosed = false)
    {
        var pages = new List<FakePage>();
        var ctx = new Mock<IBrowserContext>();
        ctx.Setup(c => c.NewPageAsync()).ReturnsAsync(() =>
        {
            var page = new Mock<IPage>();
            var fake = new FakePage { Mock = page, Closed = pagesStartClosed };
            page.SetupGet(p => p.IsClosed).Returns(() => fake.Closed);
            page.SetupGet(p => p.Url).Returns(() => fake.Url);
            page.Setup(p => p.CloseAsync(It.IsAny<PageCloseOptions?>()))
                .Returns(Task.CompletedTask);
            pages.Add(fake);
            return page.Object;
        });
        return (ctx, pages);
    }

    [Fact]
    public async Task ABrowse_RunsItsWorkOnTheAcquiredPage_AndAnswersItsResult()
    {
        var (ctx, pages) = CreateContext();
        await using var manager = new BrowserSessionManager();

        IPage? seenPage = null;
        string? seenUrlBefore = null;
        var outcome = await manager.BrowseAsync("s1", "https://a.test/", ctx.Object, cx =>
        {
            seenPage = cx.Page;
            seenUrlBefore = cx.UrlBefore;
            return Task.FromResult("answer");
        });

        outcome.ShouldBeOfType<TabOutcome<string>.Ran>().Result.ShouldBe("answer");
        seenPage.ShouldBeSameAs(pages[0].Mock.Object);
        seenUrlBefore.ShouldBe("about:blank");
    }

    [Fact]
    public async Task ASameUrlReBrowse_SupersedesTheRefsTheTabHeld()
    {
        // A re-browse of the same URL reloads the DOM in place, so the refs stamped on the old
        // document must die even though the URL never moved.
        var (ctx, pages) = CreateContext();
        await using var manager = new BrowserSessionManager();

        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object,
            async cx =>
            {
                await cx.StampAsync(_ => Task.FromResult(3));
                return "first";
            });

        var again = await manager.BrowseAsync("s1", "https://a.test/", ctx.Object,
            _ => Task.FromResult("second"));

        again.ShouldBeOfType<TabOutcome<string>.Ran>();
        pages.Count.ShouldBe(1);
        manager.RouteRef("s1", "i-1").ShouldBe(new RefRouting.Superseded("https://a.test/"));
    }

    [Fact]
    public async Task TheLease_CommitsWithTheWorkReportedCount_AndNumbersContinue()
    {
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();

        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object,
            async cx =>
            {
                await cx.StampAsync(start =>
                {
                    start.ShouldBe(1);
                    return Task.FromResult(3);
                });
                return true;
            });

        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object,
            async cx =>
            {
                await cx.StampAsync(start =>
                {
                    // The session's numbers continue where the first stamp stopped.
                    start.ShouldBe(4);
                    return Task.FromResult(2);
                });
                return true;
            });

        // The re-browse superseded the old range before registering its own.
        manager.RouteRef("s1", "i-2").ShouldBeOfType<RefRouting.Superseded>();
        manager.RouteRef("s1", "i-4").ShouldBeOfType<RefRouting.Routed>();
    }

    [Fact]
    public async Task ATabEvictedBetweenAcquisitionAndLocking_GetsAFreshTab_NotAnException()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (ctx, pages) = CreateContext();
        await using var manager = new BrowserSessionManager(timeProvider: time);

        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object, _ => Task.FromResult(true));
        var tab = manager.Get("s1")!.CurrentTab!;
        var touchedAt = tab.LastTouchedAt;
        time.Advance(TimeSpan.FromSeconds(1));

        // Hold the tab's lock so the second browse queues between acquiring and locking it.
        var hold = await manager.LockTabAsync(tab);
        IPage? ranOn = null;
        var second = manager.BrowseAsync("s1", "https://a.test/", ctx.Object, cx =>
        {
            ranOn = cx.Page;
            return Task.FromResult(true);
        });

        // The acquisition has handed the (still open) tab back once its touch lands.
        await Eventually.Until(() => tab.LastTouchedAt > touchedAt, "the reused tab to be touched");
        pages[0].Closed = true;
        hold.Dispose();

        var outcome = await second;

        // The dead tab was dropped and the browse ran on a fresh page instead of throwing.
        outcome.ShouldBeOfType<TabOutcome<bool>.Ran>();
        pages.Count.ShouldBe(2);
        ranOn.ShouldBeSameAs(pages[1].Mock.Object);
    }

    [Fact]
    public async Task AcquireExhaustion_AnswersItsRetryableWall_NotAnException()
    {
        // A session churning tabs faster than a browse can hold one: every acquired page is
        // already closed by the time the lock lands. Saying so beats joining the churn.
        var (ctx, pages) = CreateContext(pagesStartClosed: true);
        await using var manager = new BrowserSessionManager();

        var ran = false;
        var outcome = await manager.BrowseAsync("s1", "https://a.test/", ctx.Object, _ =>
        {
            ran = true;
            return Task.FromResult(true);
        });

        outcome.ShouldBeOfType<TabOutcome<bool>.AcquireExhausted>();
        ran.ShouldBeFalse();
        pages.Count.ShouldBe(3);
    }

    [Fact]
    public async Task TheClosedWall_WinsOverTheWorksOwnAnswer()
    {
        var (ctx, pages) = CreateContext();
        await using var manager = new BrowserSessionManager();

        var outcome = await manager.BrowseAsync("s1", "https://a.test/", ctx.Object, _ =>
        {
            pages[0].Closed = true;
            return Task.FromResult("the work's own answer");
        });

        outcome.ShouldBe(new TabOutcome<string>.Closed("https://a.test/"));
    }

    [Fact]
    public async Task AWorkThrowingClosedText_WithTheTabDead_AnswersTheClosedWall()
    {
        var (ctx, pages) = CreateContext();
        await using var manager = new BrowserSessionManager();

        var outcome = await manager.BrowseAsync<bool>("s1", "https://a.test/", ctx.Object, _ =>
        {
            pages[0].Closed = true;
            throw new PlaywrightException("Target page, context or browser has been closed");
        });

        outcome.ShouldBe(new TabOutcome<bool>.Closed("https://a.test/"));
    }

    [Fact]
    public async Task AWorkThrowingClosedText_WithTheTabAlive_LetsTheConnectionDeathEscape()
    {
        // The same exception text with the page still open is the whole connection dying — the
        // browser above owns reconnecting, so it must see the exception, not a wall.
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();

        await Should.ThrowAsync<PlaywrightException>(() =>
            manager.BrowseAsync<bool>("s1", "https://a.test/", ctx.Object,
                _ => throw new PlaywrightException("Target page, context or browser has been closed")));
    }

    [Fact]
    public async Task ARefLessCallWithNoSession_AnswersNoSession()
    {
        await using var manager = new BrowserSessionManager();

        var outcome = await manager.OnCurrentTabAsync(
            "nobody", StampPolicy.None, _ => Task.FromResult(true));

        outcome.ShouldBe(new TabOutcome<bool>.NoSession());
    }

    [Fact]
    public async Task ACurrentTabAlreadyClosed_AnswersTheClosedWall_NotNoSession()
    {
        // The session is alive; only the tab died. Answering "no session" here was one of the
        // three drifted spellings — the wall names the page to browse again instead.
        var (ctx, pages) = CreateContext();
        await using var manager = new BrowserSessionManager();

        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object, _ => Task.FromResult(true));
        pages[0].Closed = true;

        var outcome = await manager.OnCurrentTabAsync(
            "s1", StampPolicy.None, _ => Task.FromResult(true));

        outcome.ShouldBe(new TabOutcome<bool>.Closed("https://a.test/"));
    }

    [Fact]
    public async Task ACallAddressedToAUrlNoTabHolds_AnswersAddressedTabGone()
    {
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();

        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object, _ => Task.FromResult(true));

        var ran = false;
        var outcome = await manager.OnUrlAsync("s1", "https://gone.test/", StampPolicy.None, _ =>
        {
            ran = true;
            return Task.FromResult(true);
        });

        // Never silently built from another page: the work must not have run anywhere.
        outcome.ShouldBe(new TabOutcome<bool>.AddressedTabGone("https://gone.test/"));
        ran.ShouldBeFalse();
    }

    [Fact]
    public async Task ACallAddressedToATabReNavigatedWhileQueued_RefusesRatherThanAnsweringTheWrongPage()
    {
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();

        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object, _ => Task.FromResult(true));
        var tab = manager.Get("s1")!.CurrentTab!;

        var hold = await manager.LockTabAsync(tab);
        var ran = false;
        var queued = manager.OnUrlAsync("s1", "https://a.test/", StampPolicy.None, _ =>
        {
            ran = true;
            return Task.FromResult(true);
        });

        // While the call queues on the lock, the tab is re-navigated away from the named page —
        // both the address it was asked with and the one it landed on now say something else.
        await Eventually.Settle();
        tab.RequestedUrl = "https://b.test/";
        tab.FinalUrl = "https://b.test/";
        hold.Dispose();

        (await queued).ShouldBe(new TabOutcome<bool>.AddressedTabGone("https://a.test/"));
        ran.ShouldBeFalse();
    }

    [Fact]
    public async Task ANonNavigatingWork_LeavesTheOtherNamespacesRangesRouting()
    {
        // A snapshot restamps its own namespace, but a work that does not move the URL must not
        // unmake the tab's other handles — the image refs stay routable.
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();

        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object,
            async cx =>
            {
                await cx.StampAsync(_ => Task.FromResult(2));
                return true;
            });

        var snapshot = await manager.OnCurrentTabAsync(
            "s1", StampPolicy.Restamp(RefNamespace.Element),
            async cx =>
            {
                await cx.StampAsync(_ => Task.FromResult(3));
                return true;
            });

        snapshot.ShouldBeOfType<TabOutcome<bool>.Ran>();
        manager.RouteRef("s1", "i-1").ShouldBeOfType<RefRouting.Routed>();
        manager.RouteRef("s1", "e-1").ShouldBeOfType<RefRouting.Routed>();

        // A second restamp renumbers its own namespace — and only its own.
        await manager.OnCurrentTabAsync(
            "s1", StampPolicy.Restamp(RefNamespace.Element),
            async cx =>
            {
                await cx.StampAsync(_ => Task.FromResult(3));
                return true;
            });

        manager.RouteRef("s1", "e-1").ShouldBeOfType<RefRouting.Superseded>();
        manager.RouteRef("s1", "i-1").ShouldBeOfType<RefRouting.Routed>();
    }

    [Fact]
    public async Task AWorkThatMovesTheUrl_SupersedesEverythingTheTabStamped()
    {
        var (ctx, pages) = CreateContext();
        await using var manager = new BrowserSessionManager();

        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object,
            async cx =>
            {
                await cx.StampAsync(_ => Task.FromResult(2));
                return true;
            });

        await manager.OnCurrentTabAsync("s1", StampPolicy.None, _ =>
        {
            pages[0].Url = "https://a.test/other";
            return Task.FromResult(true);
        });

        manager.RouteRef("s1", "i-1").ShouldBeOfType<RefRouting.Superseded>();
    }

    [Fact]
    public async Task AStampAbandonedByAThrowingWork_DoesNotWedgeTheNextStamp()
    {
        // The lease's lock is released by the pipeline even when the work dies after opening it.
        var (ctx, _) = CreateContext();
        await using var manager = new BrowserSessionManager();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            manager.BrowseAsync<bool>("s1", "https://a.test/", ctx.Object, async cx =>
            {
                await cx.StampAsync(_ => Task.FromResult(1));
                throw new InvalidOperationException("work died");
            }));

        var next = await manager.BrowseAsync("s1", "https://a.test/", ctx.Object,
            async cx =>
            {
                await cx.StampAsync(_ => Task.FromResult(1));
                return true;
            });

        next.ShouldBeOfType<TabOutcome<bool>.Ran>();
    }
}