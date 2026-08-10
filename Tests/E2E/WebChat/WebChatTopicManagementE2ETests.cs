using Microsoft.Playwright;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

[Collection("WebChatE2E")]
[Trait("Category", "E2E")]
public class WebChatTopicManagementE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task SelectTopic_LoadsMessages()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("Topic one message for E2E");
        await chatInput.PressAsync("Enter");

        await page.Locator(".message-content").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

        await WebChatE2ETests.ClickThroughApprovalsAsync(page, page.Locator(".hearth-new:visible"));

        await Assertions.Expect(chatInput).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 5_000 });
        await chatInput.FillAsync("Topic two message for E2E");
        await chatInput.PressAsync("Enter");

        await page.Locator(".message-content").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

        // The wait above only proves the typed bubble rendered — topic two's reply is still
        // streaming, and every chunk bumps its LastMessageAt, which is the key the rows are
        // ordered by. A click aimed at "Topic one" then lands on whichever row slid under it,
        // and when that row is topic two, HandleTopicClick sees the selection is unchanged and
        // dispatches nothing: the click "succeeds" and the messages never switch. Wait for the
        // order to stop moving first.
        await WebChatE2ETests.WaitForRowsToStopMovingAsync(page);

        // Can't rely on position — other tests' topics may also be visible.
        var topic1 = page.Locator(".topic-item", new PageLocatorOptions { HasText = "Topic one" });
        await topic1.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // .First, because the agent often quotes the message back and then two bubbles carry the
        // text — a strict-mode violation, which fails outright instead of waiting.
        //
        // A row can still move under a late approval or a resumed stream, and a miss is silent,
        // so the click is retried when the messages didn't switch.
        var messageContent = page
            .Locator(".message-content", new PageLocatorOptions { HasText = "Topic one message for E2E" }).First;
        for (var attempt = 0; ; attempt++)
        {
            await WebChatE2ETests.ClickThroughApprovalsAsync(page, topic1);
            try
            {
                await messageContent.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
                break;
            }
            catch (TimeoutException) when (attempt < 2)
            {
            }
        }
    }

    // The title only shows in the top bar on a phone, and that is where it is renamed: tap it,
    // type over it, press Enter. The conversation list is the proof the new name stuck.
    [SkippableFact]
    public async Task RenameTopic_FromTheMobileHeader_RenamesTheConversation()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("Topic to rename in E2E test");
        await chatInput.PressAsync("Enter");

        // Renaming while the reply is still streaming races the read-marker save over the same
        // topic key, so let the turn finish first — the race is the stack's, not this feature's.
        await page.Locator(".message-content").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

        var headerName = page.Locator(".header-conversation-name");
        await Assertions.Expect(headerName).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
        await WebChatE2ETests.ClickThroughApprovalsAsync(page, headerName);

        var editor = page.Locator(".header-conversation-edit");
        await Assertions.Expect(editor).ToBeFocusedAsync(new LocatorAssertionsToBeFocusedOptions { Timeout = 5_000 });
        await editor.FillAsync("Renamed in E2E test");
        await editor.PressAsync("Enter");

        await Assertions.Expect(headerName).ToHaveTextAsync(
            "Renamed in E2E test", new LocatorAssertionsToHaveTextOptions { Timeout = 10_000 });

        var renamedRow = page.Locator(".topic-item", new PageLocatorOptions { HasText = "Renamed in E2E test" });
        await renamedRow.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
    }

    // The same title, in the same place, on a wide screen: the rail says which conversation is
    // selected, but only the top bar lets it be renamed.
    [SkippableFact]
    public async Task RenameTopic_FromTheDesktopHeader_RenamesTheConversation()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(1280, 900);
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("Topic to rename on the desktop E2E test");
        await chatInput.PressAsync("Enter");

        await page.Locator(".message-content").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

        var headerName = page.Locator(".header-conversation-name");
        await Assertions.Expect(headerName).ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
        await WebChatE2ETests.ClickThroughApprovalsAsync(page, headerName);

        var editor = page.Locator(".header-conversation-edit");
        await Assertions.Expect(editor).ToBeFocusedAsync(new LocatorAssertionsToBeFocusedOptions { Timeout = 5_000 });
        await editor.FillAsync("Renamed on the desktop");
        await editor.PressAsync("Enter");

        await Assertions.Expect(headerName).ToHaveTextAsync(
            "Renamed on the desktop", new LocatorAssertionsToHaveTextOptions { Timeout = 10_000 });

        var renamedRow = page.Locator(".topic-item", new PageLocatorOptions { HasText = "Renamed on the desktop" });
        await renamedRow.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
    }

    [SkippableFact]
    public async Task DeleteTopic_RemovesFromSidebar()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("Topic to delete in E2E test");
        await chatInput.PressAsync("Enter");

        var ourTopic = page.Locator(".topic-item", new PageLocatorOptions { HasText = "Topic to delete" });
        await ourTopic.WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });

        // The same dismiss-and-retry guard as the other helpers: a pending approval leaked by a
        // sibling test can raise the full-viewport overlay at any moment and intercept these
        // clicks. The confirm button only exists while its row is in confirm mode, so both
        // clicks retry together.
        for (var attempt = 0; ; attempt++)
        {
            await WebChatE2ETests.DismissApprovalOverlayAsync(page);
            try
            {
                await ourTopic.Locator(".delete-btn").ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                await page.Locator(".confirm-delete-btn").ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                break;
            }
            catch (TimeoutException) when (attempt < 2)
            {
                // Overlay re-armed between dismissal and a click; loop to dismiss and retry.
            }
        }

        await Assertions.Expect(ourTopic).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 10_000 });
    }
}