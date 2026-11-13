using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

[Collection(WebChatE2ECollections.Chat)]
[Trait("Category", "E2E")]
public class WebChatE2ETests(WebChatE2EFixture fixture)
{
    // DOMContentLoaded rather than NetworkIdle. Every case here goes on to wait for something the
    // app can only show once it has booted and connected — the composer being enabled, a row, the
    // microphone — so waiting for the network to fall quiet first buys nothing and costs its own
    // half-second idle window plus the tail of the WASM payload, on every page the suite opens.
    // The dashboard's pages deliberately keep NetworkIdle: they read counts straight off the
    // document with no locator to wait on.
    internal static Task GotoWebChatAsync(IPage page, string url) =>
        page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

    // The page is signed in before it opens (WebChatE2EFixture.CreateSignedInPageAsync), so there is
    // nobody to pick: what is left is the agent and a fresh topic.
    internal static async Task SelectAgentAsync(IPage page)
    {
        // Dismiss any approval-modal-overlay left by the StreamResumeService.
        // When a new page connects, the server may push pending approval state from
        // a previous test's session, showing the overlay and blocking all clicks.
        await DismissApprovalOverlayAsync(page);

        var chatInput = page.Locator("textarea.chat-input");

        // The chat input becomes enabled once the app has booted, SignalR is connected and an
        // agent is selected. The boot is in this wait too, which in a full-suite run is the
        // container serving every other test at the same time, so it is given what the boot and
        // the connect used to be given apiece.
        try
        {
            await E2ETrace.TimeAsync("page:composer-enabled", () =>
                Assertions.Expect(chatInput).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 60_000 }));
        }
        catch (TimeoutException)
        {
            // Input still disabled — initialization may not have auto-selected an agent;
            // fall through to manually select the first agent in the switcher.
            var firstAgent = page.Locator(".agent-seg:visible").First;
            if (await firstAgent.IsVisibleAsync())
            {
                await firstAgent.ClickAsync();
            }

            await Assertions.Expect(chatInput).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 10_000 });
        }

        // Start a fresh topic so previous test messages don't pollute context.
        // The approval overlay is pushed by StreamResumeService via a fire-and-forget
        // SignalR chain (InitializationEffect → LoadTopicHistoryAsync → TryResumeStreamAsync),
        // so it can arrive at any point — including the window between the wait above and
        // this click. Guard the click with a dismiss-and-retry.
        var newTopicBtn = page.Locator(".hearth-new:visible");
        if (await newTopicBtn.IsVisibleAsync())
        {
            try
            {
                await newTopicBtn.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
            }
            catch (TimeoutException)
            {
                await DismissApprovalOverlayAsync(page);
                await newTopicBtn.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
            }
        }
    }

    internal static Task DismissApprovalOverlayAsync(IPage page) =>
        RejectEveryVisibleApprovalAsync(page, TimeSpan.FromSeconds(15));

    // The one guard for any single click a leaked approval overlay can intercept: dismiss
    // whatever is showing, click, and on interception dismiss and try again. The overlay rides
    // a fire-and-forget resume chain, so it can arrive between the dismissal and the click.
    internal static async Task ClickThroughApprovalsAsync(IPage page, ILocator target)
    {
        for (var attempt = 0; ; attempt++)
        {
            await DismissApprovalOverlayAsync(page);
            try
            {
                await target.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                return;
            }
            catch (TimeoutException) when (attempt < 2)
            {
            }
        }
    }

    // Rows are ordered by LastMessageAt desc and re-render while replies stream, so they reorder
    // between an elementFromPoint aim and the tap that follows — the tap then lands on whichever
    // conversation slid under the coordinate. Wait until the order stops moving before aiming at
    // anything by coordinate, and keep rejecting approval prompts, because .approval-modal-overlay
    // (z-index 1000) covers the whole viewport and swallows the gesture.
    //
    // The rule is unchanged — two readings a second apart that agree, with none of the caller's own
    // replies still streaming — but the wait now falls between them rather than before the first, so
    // a caller whose replies landed long ago is told so on its first look instead of after a second
    // of sleeping. Requiring more than one agreeing pair was tried and reverted: rows that reorder
    // while two replies stream rarely agree twice running, and the case that used to settle in five
    // seconds spent the whole cap.
    //
    // The caller's tag is what says which rows are its own; RowSettle holds why the question is
    // narrowed to them. Waiting on every row in the space instead was the suite's single largest
    // source of wall-clock variance: one sibling's reply that never completes leaves a row pulsing,
    // and every later wait in that space then spent its whole cap before passing anyway.
    internal static Task<string> WaitForRowsToStopMovingAsync(
        IPage page, string tag, [CallerMemberName] string caller = "") =>
        E2ETrace.TimeAsync($"rows-settle:{caller}", () => WaitForRowsToStopMovingCoreAsync(page, tag));

    // Long enough to cover a reply that has to be waited out, short enough that a wait which cannot
    // be satisfied is a cost the run can absorb. Settling is normally under ten seconds.
    private static readonly TimeSpan _rowsSettleCap = TimeSpan.FromSeconds(30);

    internal static async Task<string> WaitForRowsToStopMovingCoreAsync(IPage page, string tag)
    {
        var deadline = DateTime.UtcNow + _rowsSettleCap;
        var previous = "";
        while (DateTime.UtcNow < deadline)
        {
            await DismissApprovalOverlayAsync(page);
            var snapshot = await page.EvaluateAsync<string>(
                """
                () => [...document.querySelectorAll('.topic-item')]
                    .map(r => (r.classList.contains('is-streaming') ? '*' : '')
                        + r.querySelector('.topic-name').textContent.trim()).join('|')
                """);
            if (RowSettle.HasSettled(previous, snapshot, tag))
            {
                return snapshot;
            }

            previous = snapshot;
            await page.WaitForTimeoutAsync(1_000);
        }

        return $"{previous} (STILL MOVING at {_rowsSettleCap.TotalSeconds:0}s cap)";
    }

    // The prompt on screen is the oldest request still waiting (ApprovalState.Pending is a
    // queue), so answering one surfaces the next in the same instant and the overlay never
    // hides in between. Rejecting once and expecting a clear screen fails whenever the agent
    // has more than one request outstanding — reject until nothing is left instead.
    //
    // This is cleanup, not an assertion: at the deadline it gives up quietly and leaves the
    // caller's own step to report what a stuck overlay broke.
    internal static async Task RejectEveryVisibleApprovalAsync(IPage page, TimeSpan budget)
    {
        var overlay = page.Locator(".approval-modal-overlay");
        var rejectBtn = page.Locator(".btn-reject");

        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline && await overlay.IsVisibleAsync())
        {
            var showing = await CurrentApprovalIdAsync(page);
            if (showing is null)
            {
                // Nothing is on screen any more — the prompt went away while this was asking, which
                // is the goal rather than a state to keep working at.
                return;
            }

            if (!await rejectBtn.IsVisibleAsync())
            {
                // An overlay is up and carries an id, but is not answerable yet: the modal is
                // mid-render, or its buttons are still disabled while a previous answer is in
                // flight. Returning here left the caller with a covered viewport and called it
                // dismissed, which is how a leaked overlay survived to fail an assertion that had
                // already checked for one. It is a moment in the overlay's life, not a state to
                // give up on, so look again — the deadline above is what bounds this.
                await page.WaitForTimeoutAsync(100);
                continue;
            }

            // The buttons stay disabled while an answer is in flight, so the click waits for
            // the modal on screen to be answerable — this one's, or the next one's.
            try
            {
                await rejectBtn.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
            }
            catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
            {
                // The modal this click aimed at was replaced by the next request's, or answered
                // from under it: the button goes unstable and then detaches, and Playwright's own
                // retries run out against a target that is no longer the one on screen. That is a
                // change of state and not a failure to dismiss, so re-read what is showing now and
                // answer that instead — the deadline is what bounds this, not one click's patience.
                continue;
            }

            await WaitUntilApprovalAnsweredAsync(page, showing, TimeSpan.FromSeconds(10));
        }
    }

    // The id of the request the modal is showing, or null when nothing is on screen — including
    // the case where the prompt goes away while this is asking.
    internal static async Task<string?> CurrentApprovalIdAsync(IPage page)
    {
        try
        {
            return await page.Locator(".approval-modal")
                .GetAttributeAsync("data-approval-id", new LocatorGetAttributeOptions { Timeout = 2_000 });
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    // Answered means this request is off the screen — either nothing is prompting any more, or
    // the next request in the queue has taken its place. Waiting for "no modal" instead would
    // fail whenever another request was already waiting behind this one.
    internal static async Task WaitUntilApprovalAnsweredAsync(IPage page, string approvalId, TimeSpan budget)
    {
        var answered = page.Locator($".approval-modal[data-approval-id='{approvalId}']");
        await Assertions.Expect(answered).ToBeHiddenAsync(
            new LocatorAssertionsToBeHiddenOptions { Timeout = (float)budget.TotalMilliseconds });
    }

    // Drives the conversation to a fully quiesced state before the test exits.
    //
    // Pending approvals live in ApprovalService._pendingApprovals — an in-memory,
    // per-container dictionary keyed by topic that survives across pages for the whole
    // suite run. It is cleared ONLY by an explicit approve/reject (RespondToApprovalAsync)
    // or DeleteTopic; the Cancel button (ChatHub.CancelTopic) does NOT clear it. If a test
    // exits while the agent has issued a (possibly follow-up) request_approval, that entry
    // leaks and StreamResumeService re-raises the overlay on every later test's page.
    //
    // Rejecting calls RespondToApprovalAsync server-side, removing the entry. The agent may
    // then issue another tool call, and several requests can be waiting at once, so loop until
    // no overlay reappears and nothing is streaming instead of relying on the next test to
    // dismiss a stale overlay.
    internal static async Task DrainPendingApprovalsAsync(IPage page)
    {
        var overlay = page.Locator(".approval-modal-overlay");
        var cancelButton = page.Locator("[data-testid=composer-cancel]");

        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (await overlay.IsVisibleAsync())
            {
                try
                {
                    await RejectEveryVisibleApprovalAsync(page, TimeSpan.FromSeconds(20));
                }
                catch (Exception e) when (e is TimeoutException or PlaywrightException)
                {
                    // A reject that will not click leaves the entry for the next test's
                    // dismissal to clear. This runs in a finally, so it must never replace the
                    // failure the test itself is reporting. A click timeout surfaces as
                    // TimeoutException, but the Expect assertion inside
                    // WaitUntilApprovalAnsweredAsync throws PlaywrightException on ITS timeout,
                    // so both are given up on quietly.
                    return;
                }

                continue;
            }

            // No modal. While the stream is still active the agent may still emit another
            // approval, so keep polling. Once nothing is streaming and no modal reappears
            // within a stable window, the conversation is quiesced server-side.
            if (await cancelButton.IsVisibleAsync())
            {
                await page.WaitForTimeoutAsync(1_000);
                continue;
            }

            await page.WaitForTimeoutAsync(2_000);
            if (!await overlay.IsVisibleAsync() && !await cancelButton.IsVisibleAsync())
            {
                return;
            }
        }
    }

    // A space is its household's configuration: its name and colour on the page and in the manifest an
    // install reads, and the agent it opens on when the person picked none there.
    [SkippableFact]
    public async Task ASpace_OpensWithItsHouseholdsNameColourAndAgent()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        var kitchen = WebChatStack.Kitchen;

        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.DefaultSpaceUrl + kitchen.Slug);

        await Assertions.Expect(page.Locator(".agent-seg.on:visible").First)
            .ToContainTextAsync("Vision Agent", new LocatorAssertionsToContainTextOptions { Timeout = 60_000 });
        await Assertions.Expect(page).ToHaveTitleAsync(new Regex($"{kitchen.Name}$"));
        await page.WaitForFunctionAsync(
            "color => getComputedStyle(document.documentElement).getPropertyValue('--space-accent').trim() === color",
            kitchen.AccentColor);
        var manifestHref = await page.Locator("link[rel=manifest]").GetAttributeAsync("href");
        var manifest = await page.APIRequest.GetAsync(new Uri(new Uri(fixture.WebChatUrl), manifestHref).ToString());
        var installed = JsonNode.Parse(await manifest.TextAsync())!.AsObject();
        installed["short_name"]!.GetValue<string>().ShouldBe(kitchen.Name);
        installed["start_url"]!.GetValue<string>().ShouldBe($"/{kitchen.Slug}");
        installed["theme_color"]!.GetValue<string>().ShouldBe(kitchen.AccentColor);
    }

    // The picker is the household's agents, in its configuration's order and under the names it gives
    // them: the home's, for the people of the home this stack signs in.
    [SkippableFact]
    public async Task ThePicker_ShowsTheHouseholdsAgentsUnderItsNames()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);
        await SelectAgentAsync(page);

        await Assertions.Expect(page.Locator(".agent-seg:visible .agent-seg-name"))
            .ToHaveTextAsync([.. WebChatStack.Agents.Agents.Select(agent => agent.Definition!.Name)]);
    }

}