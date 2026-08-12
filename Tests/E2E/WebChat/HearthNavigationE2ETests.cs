using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// What the drawer shows and where it puts things: the rail, the peek bar, the header, and the
// taps that land on a sheet which is already still.
[Collection(WebChatE2ECollections.Hearth)]
[Trait("Category", "E2E")]
public sealed class HearthNavigationE2ETests(WebChatE2EFixture fixture) : HearthE2EBase
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

        // A pending approval leaked by a sibling test can be replayed onto this fresh page at any
        // moment, and .approval-modal-overlay covers the whole viewport: a plain click then spends
        // its entire timeout being intercepted, which is how a loaded run failed here. The same
        // dismiss-and-retry guard the other suites use.
        await WebChatE2ETests.ClickThroughApprovalsAsync(page, page.Locator(".hearth-peek .agent-chip"));
        var menu = page.Locator(".hearth-peek .agent-combo-menu");
        await Assertions.Expect(menu).ToBeVisibleAsync();

        // Tap the chat area well above the sheet. The dismiss backdrop has to reach up there,
        // so click by coordinate instead of by locator — the backdrop covers what we aim at.
        // An approval overlay would cover it too, and a coordinate click has no locator to retry
        // through, so clear it first.
        await WebChatE2ETests.DismissApprovalOverlayAsync(page);
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

    // The drag wrote the sheet's transform through requestAnimationFrame while the release
    // settled synchronously, so the final move's deferred write could land after the settle and
    // stick: an inline --sheet-offset that overrides every later detent change. The sheet then
    // sits where the drag left it while the state underneath moves on — a topic tap selects the
    // topic invisibly and the drawer refuses to close until an unrelated tap on the chrome
    // settles again and clears the stale style. Releasing mid-move is exactly a flick, so this
    // is the common gesture, not a corner case.
    [SkippableFact]
    public async Task MobileViewport_ReleasingADragMidMove_LeavesNoStaleSheetOffset()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync(hasTouch: true);
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        var staleOffset = await page.EvaluateAsync<string>(
            """
            () => new Promise(resolve => {
                const peek = document.querySelector('.hearth-peek');
                const p = (y, id) => ({ bubbles: true, cancelable: true, pointerId: id, isPrimary: true, clientX: 195, clientY: y });
                peek.dispatchEvent(new PointerEvent('pointerdown', p(800, 1)));
                let y = 800;
                const step = () => {
                    y -= 60;
                    document.dispatchEvent(new PointerEvent('pointermove', p(y, 1)));
                    if (y > 380) { requestAnimationFrame(step); return; }
                    // Release in the same task as the final move: the settle must win over any
                    // write that move deferred, or the sheet is stuck at the drag position.
                    document.dispatchEvent(new PointerEvent('pointerup', p(y, 1)));
                    requestAnimationFrame(() => requestAnimationFrame(() =>
                        resolve(document.querySelector('.hearth').style.getPropertyValue('--sheet-offset'))));
                };
                requestAnimationFrame(step);
            })
            """);

        staleOffset.ShouldBe("", "the drag's deferred style write must not outlive the settle");
    }

    // A tap whose finger drifts a few pixels must stay a tap. On a list too short to scroll both
    // edge flags are true, so the pull-to-collapse handler used to convert any ≥8px drift into a
    // sheet gesture and swallow the click — browsers deliver touchmove for drifts well below
    // their own click slop, so real thumb taps died. Drift below the tap slop must not engage
    // the sheet nor swallow the click that follows.
    [SkippableFact]
    public async Task MobileViewport_ATapThatDriftsAFewPixelsStillSelectsTheTopic()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync(hasTouch: true);
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        // Named per run: an earlier run's rows are still in the list under this same user, and a
        // literal would find whichever of them the list put first. The tag leads because
        // CreateTopicAsync matches on the first sixteen characters.
        var tag = Guid.NewGuid().ToString("N")[..4];
        await CreateTopicAsync(page, $"{tag} target drift tap");
        await CreateTopicAsync(page, $"{tag} decoy drift tap");

        await TapHearthHandleAsync(page);
        await TapHearthHandleAsync(page);
        await Assertions.Expect(page.Locator(".hearth-search-input")).ToBeVisibleAsync();

        var switched = await page.EvaluateAsync<bool>(
            """
            target => new Promise(resolve => {
                const row = [...document.querySelectorAll('.topic-item')]
                    .find(r => r.textContent.includes(target));
                const rect = row.getBoundingClientRect();
                const x = rect.x + rect.width / 2;
                const y0 = rect.y + rect.height / 2;
                const touch = y => new Touch({ identifier: 1, target: row, clientX: x, clientY: y });
                const ev = (type, y) => new TouchEvent(type, {
                    bubbles: true, cancelable: true,
                    touches: type === 'touchend' ? [] : [touch(y)],
                    changedTouches: [touch(y)]
                });
                row.dispatchEvent(ev('touchstart', y0));
                row.dispatchEvent(ev('touchmove', y0 + 6));
                row.dispatchEvent(ev('touchmove', y0 + 12));
                row.dispatchEvent(ev('touchend', y0 + 12));
                setTimeout(() => {
                    const p = { bubbles: true, cancelable: true, pointerId: 3, isPrimary: true, clientX: x, clientY: y0 + 12 };
                    row.dispatchEvent(new PointerEvent('pointerdown', p));
                    row.dispatchEvent(new PointerEvent('pointerup', p));
                    row.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
                    // Poll rather than a fixed delay: the class arrives on the next Blazor
                    // render, which under full-suite load can take well over half a second.
                    const deadline = performance.now() + 5000;
                    const settled = () => row.classList.contains('selected') ? resolve(true)
                        : performance.now() > deadline ? resolve(false)
                        : setTimeout(settled, 100);
                    settled();
                }, 80);
            })
            """, $"{tag} target drift tap");

        switched.ShouldBeTrue("a 12px-drift tap must still select the topic, not become a sheet gesture");
    }
}