using Infrastructure.Clients.Browser;
using Microsoft.Playwright;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// The ordering facts of the tab protocol, asserted through the per-intent methods against faked
// pages. These are the sequencing bugs the old verb-driven suite could never see, because that
// suite performed the sequencing itself.
public class TabProtocolTests
{
    private static Task<TabOutcome<IPage>> RouteAsync(
        BrowserSessionManager manager, string sessionId, string refString) =>
        manager.OnRefAsync(sessionId, refString, StampPolicy.None, cx => Task.FromResult(cx.Page));

    private static async Task StampElementsAsync(BrowserSessionManager manager, string sessionId, int count)
    {
        var stamped = await manager.OnCurrentTabAsync(
            sessionId, StampPolicy.Restamp(RefNamespace.Element),
            async cx =>
            {
                await cx.StampAsync(_ => Task.FromResult(count));
                return true;
            });
        stamped.ShouldBeOfType<TabOutcome<bool>.Ran>();
    }

    // Holds the current tab's lock through the pipeline itself, so a sibling call queues exactly
    // where the historical races landed: between routing (or acquisition) and locking.
    private static async Task<(TaskCompletionSource Release, Task Holder)> HoldCurrentTabAsync(
        BrowserSessionManager manager, string sessionId)
    {
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var holder = manager.OnCurrentTabAsync(sessionId, StampPolicy.None, async _ =>
        {
            entered.SetResult();
            await release.Task;
            return true;
        });
        await entered.Task;
        return (release, holder);
    }

    [Fact]
    public async Task ABrowse_RunsItsWorkOnTheAcquiredPage_AndAnswersItsResult()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
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
        var (ctx, pages) = TabPoolFakes.CreateContext();
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
        (await RouteAsync(manager, "s1", "i-1"))
            .ShouldBe(new TabOutcome<IPage>.Superseded("https://a.test/"));
    }

    [Fact]
    public async Task TheLease_CommitsWithTheWorkReportedCount_AndNumbersContinue()
    {
        var (ctx, _) = TabPoolFakes.CreateContext();
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
        (await RouteAsync(manager, "s1", "i-2")).ShouldBeOfType<TabOutcome<IPage>.Superseded>();
        (await RouteAsync(manager, "s1", "i-4")).ShouldBeOfType<TabOutcome<IPage>.Ran>();
    }

    [Fact]
    public async Task ATabEvictedBetweenAcquisitionAndLocking_GetsAFreshTab_NotAnException()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object, _ => Task.FromResult(true));

        // The tab's lock is held, so the second browse queues between acquiring and locking it.
        var (release, holder) = await HoldCurrentTabAsync(manager, "s1");
        IPage? ranOn = null;
        var second = manager.BrowseAsync("s1", "https://a.test/", ctx.Object, cx =>
        {
            ranOn = cx.Page;
            return Task.FromResult(true);
        });

        // The eviction lands while the browse waits on the lock it will never usefully get.
        await Eventually.Settle();
        pages[0].Closed = true;
        release.SetResult();
        await holder;

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
        var (ctx, pages) = TabPoolFakes.CreateContext(pagesStartClosed: true);
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
        var (ctx, pages) = TabPoolFakes.CreateContext();
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
        var (ctx, pages) = TabPoolFakes.CreateContext();
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
        var (ctx, _) = TabPoolFakes.CreateContext();
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
        var (ctx, pages) = TabPoolFakes.CreateContext();
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
        var (ctx, _) = TabPoolFakes.CreateContext();
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
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        // Asked for a.test, landed on b.test — so a later browse of b.test reuses this tab.
        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object, _ =>
        {
            pages[0].Url = "https://b.test/";
            return Task.FromResult(true);
        });

        var (release, holder) = await HoldCurrentTabAsync(manager, "s1");
        var ran = false;
        var queued = manager.OnUrlAsync("s1", "https://a.test/", StampPolicy.None, _ =>
        {
            ran = true;
            return Task.FromResult(true);
        });
        await Eventually.Settle();

        // While the call queues on the lock, a browse of b.test reuses the tab: its acquisition
        // re-addresses the tab under the pool gate, so neither the address it was asked with nor
        // the one it landed on says a.test any more.
        var rebrowse = manager.BrowseAsync("s1", "https://b.test/", ctx.Object, _ => Task.FromResult(true));
        await Eventually.Settle();
        release.SetResult();
        await holder;
        (await rebrowse).ShouldBeOfType<TabOutcome<bool>.Ran>();

        (await queued).ShouldBe(new TabOutcome<bool>.AddressedTabGone("https://a.test/"));
        ran.ShouldBeFalse();
    }

    [Fact]
    public async Task ANonNavigatingWork_LeavesTheOtherNamespacesRangesRouting()
    {
        // A snapshot restamps its own namespace, but a work that does not move the URL must not
        // unmake the tab's other handles — the image refs stay routable.
        var (ctx, _) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object,
            async cx =>
            {
                await cx.StampAsync(_ => Task.FromResult(2));
                return true;
            });

        await StampElementsAsync(manager, "s1", 3);

        (await RouteAsync(manager, "s1", "i-1")).ShouldBeOfType<TabOutcome<IPage>.Ran>();
        (await RouteAsync(manager, "s1", "e-1")).ShouldBeOfType<TabOutcome<IPage>.Ran>();

        // A second restamp renumbers its own namespace — and only its own.
        await StampElementsAsync(manager, "s1", 3);

        (await RouteAsync(manager, "s1", "e-1")).ShouldBeOfType<TabOutcome<IPage>.Superseded>();
        (await RouteAsync(manager, "s1", "i-1")).ShouldBeOfType<TabOutcome<IPage>.Ran>();
    }

    [Fact]
    public async Task AWorkThatMovesTheUrl_SupersedesEverythingTheTabStamped()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
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

        (await RouteAsync(manager, "s1", "i-1")).ShouldBeOfType<TabOutcome<IPage>.Superseded>();
    }

    [Fact]
    public async Task ARef_RunsItsWorkOnTheTabThatStampedIt_NotTheCurrentOne()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object, _ => Task.FromResult(true));
        await StampElementsAsync(manager, "s1", 2);
        await manager.BrowseAsync("s1", "https://b.test/", ctx.Object, _ => Task.FromResult(true));

        IPage? ranOn = null;
        var outcome = await manager.OnRefAsync("s1", "e-1", StampPolicy.None, cx =>
        {
            ranOn = cx.Page;
            return Task.FromResult(true);
        });

        outcome.ShouldBeOfType<TabOutcome<bool>.Ran>();
        ranOn.ShouldBeSameAs(pages[0].Mock.Object);
    }

    [Fact]
    public async Task ASupersededRef_AnswersItsWall_WithoutRunningTheWork()
    {
        var (ctx, _) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object, _ => Task.FromResult(true));
        await StampElementsAsync(manager, "s1", 2);
        await StampElementsAsync(manager, "s1", 2);

        var ran = false;
        var outcome = await manager.OnRefAsync("s1", "e-1", StampPolicy.None, _ =>
        {
            ran = true;
            return Task.FromResult(true);
        });

        outcome.ShouldBe(new TabOutcome<bool>.Superseded("https://a.test/"));
        ran.ShouldBeFalse();
    }

    [Fact]
    public async Task ARefWhoseTabWasEvictedBetweenRoutingAndLocking_AnswersTheClosedWall()
    {
        // The historical race: routing found a live tab, and the eviction landed before the lock.
        // The under-lock re-check answers the closed wall — never a crash, never a wrong page.
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object, _ => Task.FromResult(true));
        await StampElementsAsync(manager, "s1", 2);

        var (release, holder) = await HoldCurrentTabAsync(manager, "s1");
        var ran = false;
        var queued = manager.OnRefAsync("s1", "e-1", StampPolicy.None, _ =>
        {
            ran = true;
            return Task.FromResult(true);
        });

        await Eventually.Settle();
        pages[0].Closed = true;
        release.SetResult();
        await holder;

        (await queued).ShouldBe(new TabOutcome<bool>.Closed("https://a.test/"));
        ran.ShouldBeFalse();
    }

    private static Func<TabWorkContext, Task<string?>> ActThat(Func<TabWorkContext, Task>? effect = null) =>
        async cx =>
        {
            if (effect is not null)
            {
                await effect(cx);
            }

            return null;
        };

    [Fact]
    public async Task AnActionThatDoesNotMoveTheUrl_LeavesEarlierRangesRouting()
    {
        // The augment pass: same-URL DOM churn keeps the refs the model is mid-flow with alive,
        // so a four-field form survives its first fill.
        var (ctx, _) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object, _ => Task.FromResult(true));
        await StampElementsAsync(manager, "s1", 3);

        var outcome = await manager.OnRefAsync(
            "s1", "e-1", StampPolicy.AugmentUnlessNavigated(RefNamespace.Element),
            ActThat(),
            answer: async cx =>
            {
                cx.AugmentRefs.ShouldBeTrue();
                await cx.StampAsync(_ => Task.FromResult(2));
                return "acted";
            },
            popup: new PopupAnswer<string>(
                StampPolicy.Restamp(RefNamespace.Element), _ => Task.FromResult("popup")));

        outcome.ShouldBeOfType<TabOutcome<string>.Ran>().PopupAnswered.ShouldBeFalse();
        (await RouteAsync(manager, "s1", "e-1")).ShouldBeOfType<TabOutcome<IPage>.Ran>();
        (await RouteAsync(manager, "s1", "e-4")).ShouldBeOfType<TabOutcome<IPage>.Ran>();
    }

    [Fact]
    public async Task AnActionThatMovesTheUrl_SupersedesTheTabsEarlierRanges()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object, _ => Task.FromResult(true));
        await StampElementsAsync(manager, "s1", 3);

        var outcome = await manager.OnRefAsync(
            "s1", "e-1", StampPolicy.AugmentUnlessNavigated(RefNamespace.Element),
            ActThat(_ =>
            {
                pages[0].Url = "https://a.test/next";
                return Task.CompletedTask;
            }),
            answer: async cx =>
            {
                cx.AugmentRefs.ShouldBeFalse();
                await cx.StampAsync(_ => Task.FromResult(2));
                return "acted";
            },
            popup: new PopupAnswer<string>(
                StampPolicy.Restamp(RefNamespace.Element), _ => Task.FromResult("popup")));

        outcome.ShouldBeOfType<TabOutcome<string>.Ran>();
        (await RouteAsync(manager, "s1", "e-1")).ShouldBeOfType<TabOutcome<IPage>.Superseded>();
        (await RouteAsync(manager, "s1", "e-4")).ShouldBeOfType<TabOutcome<IPage>.Ran>();
    }

    [Fact]
    public async Task AnActionThatOpenedAPopup_AnswersFromThePopup()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object, _ => Task.FromResult(true));
        await StampElementsAsync(manager, "s1", 1);
        var popupPage = FakePage.Create("https://popup.test/window");

        IPage? answeredFrom = null;
        var outcome = await manager.OnRefAsync(
            "s1", "e-1", StampPolicy.AugmentUnlessNavigated(RefNamespace.Element),
            ActThat(_ =>
            {
                // The click's popup event fires mid-act, adopting the page against the acting tab.
                pages[0].RaisePopup(popupPage);
                return Task.CompletedTask;
            }),
            answer: _ => Task.FromResult("from the acting tab"),
            popup: new PopupAnswer<string>(StampPolicy.Restamp(RefNamespace.Element), cx =>
            {
                answeredFrom = cx.Page;
                return Task.FromResult("from the popup");
            }));

        var ran = outcome.ShouldBeOfType<TabOutcome<string>.Ran>();
        ran.PopupAnswered.ShouldBeTrue();
        ran.Result.ShouldBe("from the popup");
        answeredFrom.ShouldBeSameAs(popupPage.Mock.Object);
    }

    [Fact]
    public async Task AStalePendingPopupFromAnEarlierCall_DoesNotDivertTheAction()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object, _ => Task.FromResult(true));
        await StampElementsAsync(manager, "s1", 1);

        // A popup adopted by an earlier, unrelated call is still pending on the tab.
        pages[0].RaisePopup(FakePage.Create("https://popup.test/window"));

        var outcome = await manager.OnRefAsync(
            "s1", "e-1", StampPolicy.AugmentUnlessNavigated(RefNamespace.Element),
            ActThat(),
            answer: _ => Task.FromResult("from the acting tab"),
            popup: new PopupAnswer<string>(
                StampPolicy.Restamp(RefNamespace.Element), _ => Task.FromResult("from the popup")));

        var ran = outcome.ShouldBeOfType<TabOutcome<string>.Ran>();
        ran.PopupAnswered.ShouldBeFalse();
        ran.Result.ShouldBe("from the acting tab");
    }

    [Fact]
    public async Task APopupClosedBeforeItCouldAnswer_FallsBackToTheActingTab()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object, _ => Task.FromResult(true));
        await StampElementsAsync(manager, "s1", 1);
        var popupPage = FakePage.Create("https://popup.test/window");

        var outcome = await manager.OnRefAsync(
            "s1", "e-1", StampPolicy.AugmentUnlessNavigated(RefNamespace.Element),
            ActThat(_ =>
            {
                pages[0].RaisePopup(popupPage);
                popupPage.Closed = true;
                return Task.CompletedTask;
            }),
            answer: _ => Task.FromResult("from the acting tab"),
            popup: new PopupAnswer<string>(
                StampPolicy.Restamp(RefNamespace.Element), _ => Task.FromResult("from the popup")));

        var ran = outcome.ShouldBeOfType<TabOutcome<string>.Ran>();
        ran.PopupAnswered.ShouldBeFalse();
        ran.Result.ShouldBe("from the acting tab");
    }

    [Fact]
    public async Task OneTabDyingMidAction_IsThatTabsClosedWall_NeverTheConnectionDying()
    {
        var (ctx, pages) = TabPoolFakes.CreateContext();
        await using var manager = new BrowserSessionManager();

        await manager.BrowseAsync("s1", "https://a.test/", ctx.Object, _ => Task.FromResult(true));
        await StampElementsAsync(manager, "s1", 1);
        await manager.BrowseAsync("s1", "https://b.test/", ctx.Object, _ => Task.FromResult(true));

        var outcome = await manager.OnRefAsync<string>(
            "s1", "e-1", StampPolicy.AugmentUnlessNavigated(RefNamespace.Element),
            act: _ =>
            {
                pages[0].Closed = true;
                throw new PlaywrightException("Target page, context or browser has been closed");
            },
            answer: _ => Task.FromResult("never"),
            popup: new PopupAnswer<string>(
                StampPolicy.Restamp(RefNamespace.Element), _ => Task.FromResult("never")));

        outcome.ShouldBe(new TabOutcome<string>.Closed("https://a.test/"));

        // The session's other tab lives on and still answers.
        (await manager.OnUrlAsync("s1", "https://b.test/", StampPolicy.None, _ => Task.FromResult("alive")))
            .ShouldBeOfType<TabOutcome<string>.Ran>();
    }

    [Fact]
    public async Task AStampAbandonedByAThrowingWork_DoesNotWedgeTheNextStamp()
    {
        // The lease's lock is released by the pipeline even when the work dies after opening it.
        var (ctx, _) = TabPoolFakes.CreateContext();
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