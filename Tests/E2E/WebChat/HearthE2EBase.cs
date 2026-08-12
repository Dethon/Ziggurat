using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// These load on NetworkIdle rather than through WebChatE2ETests.GotoWebChatAsync, which is the
// faster default everywhere else. The cases here tap fixed coordinates while the sheet is in
// motion, so what has finished rendering by the time the first tap lands is part of what they
// measure: on the quicker load, the settle case found no row under its finger at all.
//
// Split across two classes because a collection is what xUnit serializes: the ten cases ran as one
// forty-six second chain, and three chains of about that length were finishing together at the end
// of every run. The heavy tap cases — the ones that drive a moving sheet and wait for it to settle
// — are the bulk of it, so they sit on their own.
public abstract class HearthE2EBase
{
    protected static async Task CreateTopicAsync(IPage page, string message)
    {
        var chatInput = page.Locator("textarea.chat-input");

        // The same dismiss-and-retry guard as TapHearthHandleAsync: a pending approval leaked
        // by a sibling test can raise the full-viewport overlay at any moment and intercept
        // this click.
        var newTopic = page.Locator(".hearth-new:visible").First;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await WebChatE2ETests.DismissApprovalOverlayAsync(page);
            try
            {
                await newTopic.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                break;
            }
            catch (TimeoutException) when (attempt < 2)
            {
                // Overlay re-armed between dismissal and the click; loop to dismiss and retry.
            }
        }

        await Assertions.Expect(chatInput).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 10_000 });
        // These rows exist to be tapped; nothing here reads the reply. Asking for a short one keeps
        // the row from being reordered by a long answer nobody is waiting to see.
        await chatInput.FillAsync($"{message} — answer in one short sentence.");
        await chatInput.PressAsync("Enter");
        await page.Locator(".topic-item", new PageLocatorOptions { HasText = message[..16] })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
    }

    // A pending approval leaked by a sibling test (the approval-flow tests in WebChatE2ETests)
    // can be replayed onto this fresh page by StreamResumeService, raising a full-viewport
    // .approval-modal-overlay (z-index 1000) that intercepts the handle tap and fails the click
    // with "<div class=\"approval-modal-overlay\">…</div> intercepts pointer events". The overlay
    // arrives via a fire-and-forget SignalR chain, so it can show up before the first tap or
    // between taps — dismiss it and retry, the same guard the sibling tests use.
    protected static async Task TapHearthHandleAsync(IPage page)
    {
        var handle = page.Locator(".hearth-handle");

        for (var attempt = 0; attempt < 3; attempt++)
        {
            await WebChatE2ETests.DismissApprovalOverlayAsync(page);
            try
            {
                await handle.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                return;
            }
            catch (TimeoutException) when (attempt < 2)
            {
                // Overlay re-armed between dismissal and the click; loop to dismiss and retry.
            }
        }
    }
}