using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

[Collection("WebChatE2E")]
[Trait("Category", "E2E")]
public sealed class HearthNavigationE2ETests(WebChatE2EFixture fixture)
{
    [SkippableFact]
    public async Task DesktopViewport_ShowsTheRail()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(1280, 900);
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        var rail = page.Locator(".hearth .agent-segmented");
        await rail.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000, State = WaitForSelectorState.Visible });
        (await rail.IsVisibleAsync()).ShouldBeTrue();
    }

    [SkippableFact]
    public async Task MobileViewport_ShowsPeekBarAndExpandsOnHandleTap()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await page.Locator(".hearth-peek").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // Tapping the handle cycles to half/full and reveals the search field.
        await TapHearthHandleAsync(page);
        await TapHearthHandleAsync(page);
        await Assertions.Expect(page.Locator(".hearth-search-input")).ToBeVisibleAsync();
    }

    [SkippableFact]
    public async Task MobileViewport_ShowsConversationTitleInHeaderAndStatusDotInDrawer()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await page.Locator(".hearth-peek").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });

        // Nothing selected yet, so the top bar carries no conversation block at all.
        await Assertions.Expect(page.Locator(".header .header-conversation")).Not.ToBeAttachedAsync();
        await Assertions.Expect(page.Locator(".hearth-peek-current")).ToBeHiddenAsync();

        // The connection indicator is a bare dot in the drawer, between the agent and model selectors.
        await Assertions.Expect(page.Locator(".hearth-peek .status-dot")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".header .connection-status")).ToBeHiddenAsync();

        // Once a conversation exists, its name and time show in the top bar.
        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());
        var chatInput = page.Locator("textarea.chat-input");
        await chatInput.FillAsync("Header title E2E check");
        await chatInput.PressAsync("Enter");

        await Assertions.Expect(page.Locator(".header .header-conversation-name"))
            .Not.ToBeEmptyAsync(new LocatorAssertionsToBeEmptyOptions { Timeout = 30_000 });
        await Assertions.Expect(page.Locator(".header .header-conversation-time")).ToBeVisibleAsync();
    }

    [SkippableFact]
    public async Task MobileViewport_TapOutsideTheAgentDropdownClosesIt()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await page.Locator(".hearth-peek").WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        await page.Locator(".hearth-peek .agent-chip").ClickAsync();
        var menu = page.Locator(".hearth-peek .agent-combo-menu");
        await Assertions.Expect(menu).ToBeVisibleAsync();

        // Tap the chat area well above the sheet. The dismiss backdrop has to reach up there,
        // so click by coordinate instead of by locator — the backdrop covers what we aim at.
        await page.Mouse.ClickAsync(195, 300);

        await Assertions.Expect(menu).Not.ToBeVisibleAsync();
    }

    [SkippableFact]
    public async Task DesktopViewport_KeepsConnectionStatusInHeader()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync();
        await page.SetViewportSizeAsync(1280, 900);
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await Assertions.Expect(page.Locator(".header .connection-status")).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator(".header .header-conversation-name")).ToBeHiddenAsync();
    }

    // A pending approval leaked by a sibling test (the approval-flow tests in WebChatE2ETests)
    // can be replayed onto this fresh page by StreamResumeService, raising a full-viewport
    // .approval-modal-overlay (z-index 1000) that intercepts the handle tap and fails the click
    // with "<div class=\"approval-modal-overlay\">…</div> intercepts pointer events". The overlay
    // arrives via a fire-and-forget SignalR chain, so it can show up before the first tap or
    // between taps — dismiss it and retry, the same guard the sibling tests use.
    private static async Task TapHearthHandleAsync(IPage page)
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