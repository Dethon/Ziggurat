using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;
using static Tests.E2E.WebChat.WebChatE2ETests;

namespace Tests.E2E.WebChat;

// The chat's turns against the stack's real model: a reply, a stop, an approval either way, and a
// message drawn as text. They change nothing of the household, so they run beside the chat cases that
// read its name and agents rather than behind them; each waits on the provider, which is what made the
// one collection that held them all the run's longest.
[Collection(WebChatE2ECollections.Conversation)]
[Trait("Category", "E2E")]
public class WebChatConversationE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task CancelStreaming_StopsResponse()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);

        await SelectAgentAsync(page);

        try
        {
            var chatInput = page.Locator("textarea.chat-input");
            await chatInput.FillAsync("Write a very long and detailed story about a space adventure. Do not call any tools, just write the story.");
            await chatInput.PressAsync("Enter");

            // Wait for Cancel button to appear (signals streaming has started)
            var cancelButton = page.Locator("[data-testid=composer-cancel]");
            await cancelButton.WaitForAsync(new LocatorWaitForOptions { Timeout = 40_000 });

            // Cancel replaces Send while the reply runs: one button in that spot, always the one
            // the user can act on.
            var sendButton = page.Locator("[data-testid=composer-send]");
            await Assertions.Expect(sendButton).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 5_000 });
            // The composer is empty once the message has gone, so the control that comes back when
            // the stream ends is the microphone rather than Send — one control in that spot, always
            // the one the person is about to use.
            var microphone = page.Locator("[data-testid=dictation-mic]");

            // .approval-modal-overlay covers the whole viewport, so a prompt on screen swallows
            // this click for as long as it is up. It can belong to this turn (the agent asked for
            // a tool despite the prompt) or to another topic whose approval leaked and is being
            // re-raised by StreamResumeService — either way the click has to clear it first.
            try
            {
                await cancelButton.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
            }
            catch (TimeoutException)
            {
                await DismissApprovalOverlayAsync(page);

                // Rejecting a prompt that belonged to this turn ends the stream by itself, which
                // is the state this test is asking for; only click when there is still a stream.
                if (await cancelButton.IsVisibleAsync())
                {
                    await cancelButton.ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                }
            }

            await Assertions.Expect(cancelButton).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 10_000 });
            await Assertions.Expect(microphone).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
        }
        finally
        {
            // Cancelling does not clear a pending approval server-side (ChatHub.CancelTopic leaves
            // ApprovalService._pendingApprovals alone), so anything this turn raised has to be
            // answered here or it re-raises on every later test's page.
            await DrainPendingApprovalsAsync(page);
        }
    }

    // A message is drawn as Markdown, never as markup: whoever wrote it — another member of a shared
    // topic, or a model talked into echoing a page — would otherwise run a script as the reader, on the
    // origin the admin API shares (pass 18: Óscar's `<img onerror>` ran in Nuria's page). Live, and
    // again from the history after a reload.
    [SkippableFact]
    public async Task AMessageCarryingMarkup_IsShownAsText_AndRunsNothing()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        var tag = Guid.NewGuid().ToString("N")[..6];
        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);
        await SelectAgentAsync(page);

        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync(
            $"markup {tag} <img src=\"x\" onerror=\"document.body.dataset.xss='ran'\"> [pulsa](javascript:document.body.dataset.xss='ran')");
        await chatInput.PressAsync("Enter");

        var bubble = page.Locator(".message-content", new PageLocatorOptions { HasText = $"markup {tag} <img" }).First;
        await bubble.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        (await bubble.Locator("a").CountAsync()).ShouldBe(0);
        (await page.EvaluateAsync<string?>("() => document.body.dataset.xss ?? null")).ShouldBeNull();
        (await bubble.Locator("img").CountAsync()).ShouldBe(0);

        // The reply is the proof the turn's record was written; reloading before it races the history
        // read against the agent's own persistence (as the attachment test waits).
        // A message this odd is sometimes answered with a tool call, whose approval holds the reply
        // until it is answered; the wait answers it as it comes, as SendMessage_AppearsInChat's does.
        await WaitForAssistantTextAsync(page);
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await DismissApprovalOverlayAsync(page);
        await page.Locator(".topic-item", new PageLocatorOptions { HasText = $"markup {tag}" })
            .First.ClickAsync(new LocatorClickOptions { Timeout = 30_000 });
        var kept = page.Locator(".message-content", new PageLocatorOptions { HasText = $"markup {tag} <img" }).First;
        await kept.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
        (await page.EvaluateAsync<string?>("() => document.body.dataset.xss ?? null")).ShouldBeNull();
        (await kept.Locator("img").CountAsync()).ShouldBe(0);
    }

    [SkippableFact]
    public async Task SendMessage_AppearsInChat()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreateSignedInPageAsync(fixture.NextUserIndex());
        await GotoWebChatAsync(page, fixture.WebChatUrl);

        await SelectAgentAsync(page);

        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("Hello, this is an E2E test message");
        await chatInput.PressAsync("Enter");

        // .First, because the agent often quotes the message back and then two bubbles carry the
        // text — a strict-mode violation, which fails outright instead of waiting. The user's own
        // bubble is the earlier of the two in document order.
        var userMessage = page
            .Locator(".message-content", new PageLocatorOptions { HasText = "Hello, this is an E2E test message" })
            .First;
        await userMessage.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // Wait for agent response — the assistant message element may appear early (with empty
        // content during "thinking"), so poll until it has non-empty text. The model sometimes
        // answers even a greeting with a tool call, and that raises an approval prompt which
        // holds the reply hostage until it is answered — reject whatever appears between waits,
        // or no amount of timeout produces text.
        // The reply comes from a real model over the network, so what bounds this is the provider's
        // worst case rather than the app's. Three minutes: a queued or slow generation ran past the
        // old ninety seconds and reported it as an empty bubble, which reads as a broken client
        // rather than as the wait it was. The loop still returns the moment text appears, so a
        // normal run pays nothing for the larger cap.
        await WaitForAssistantTextAsync(page);
    }

    // The reply comes from a real model over the network, so what bounds this is the provider's
    // worst case rather than the app's. The model sometimes answers with a tool call, which raises an
    // approval prompt that holds the reply hostage until it is answered — rejected between waits, or
    // no amount of timeout produces text. Returns the moment text appears.
    internal static async Task WaitForAssistantTextAsync(IPage page)
    {
        var assistantMessage = page.Locator(".chat-message.assistant .message-content");
        var deadline = DateTime.UtcNow.AddMinutes(3);
        while (true)
        {
            await DismissApprovalOverlayAsync(page);
            try
            {
                await Assertions.Expect(assistantMessage.First)
                    .Not.ToBeEmptyAsync(new LocatorAssertionsToBeEmptyOptions { Timeout = 10_000 });
                break;
            }
            catch (PlaywrightException) when (DateTime.UtcNow < deadline)
            {
                // An approval may have arrived mid-wait; loop to reject it and keep waiting.
            }
            catch (PlaywrightException ex)
            {
                // Past the cap. Say which wait ran out, because "expected not to be empty" on its
                // own has been read as the client failing to render a reply it did receive.
                throw new TimeoutException(
                    "No assistant text after 3 minutes. The reply comes from a real model here, so "
                    + "this is a slow or failed generation rather than the chat client.", ex);
            }
        }
    }
}