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

        await page.Locator(".hearth-new:visible").ClickAsync();

        await Assertions.Expect(chatInput).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 5_000 });
        await chatInput.FillAsync("Topic two message for E2E");
        await chatInput.PressAsync("Enter");

        await page.Locator(".message-content").First.WaitForAsync(new LocatorWaitForOptions { Timeout = 60_000 });

        // Can't rely on position — other tests' topics may also be visible.
        var topic1 = page.Locator(".topic-item", new PageLocatorOptions { HasText = "Topic one" });
        await topic1.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await topic1.ClickAsync();

        var messageContent = page.Locator(".message-content", new PageLocatorOptions { HasText = "Topic one message for E2E" });
        await messageContent.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
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
        await headerName.ClickAsync();

        var editor = page.Locator(".header-conversation-edit");
        await Assertions.Expect(editor).ToBeFocusedAsync(new LocatorAssertionsToBeFocusedOptions { Timeout = 5_000 });
        await editor.FillAsync("Renamed in E2E test");
        await editor.PressAsync("Enter");

        await Assertions.Expect(headerName).ToHaveTextAsync(
            "Renamed in E2E test", new LocatorAssertionsToHaveTextOptions { Timeout = 10_000 });

        var renamedRow = page.Locator(".topic-item", new PageLocatorOptions { HasText = "Renamed in E2E test" });
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

        await ourTopic.Locator(".delete-btn").ClickAsync();

        await page.Locator(".confirm-delete-btn").ClickAsync();

        await Assertions.Expect(ourTopic).ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 10_000 });
    }
}