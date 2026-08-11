using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// These load on NetworkIdle rather than through WebChatE2ETests.GotoWebChatAsync, which is the
// faster default everywhere else. The cases here tap fixed coordinates while the sheet is in
// motion, so what has finished rendering by the time the first tap lands is part of what they
// measure: on the quicker load, the settle case found no row under its finger at all.
[Collection(WebChatE2ECollections.Hearth)]
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

    // An open menu used to paint a dismiss backdrop across the whole sheet, and the model menu
    // stays open on purpose after a pick — so on a phone the next tap was always spent closing
    // it and the conversation under the finger never opened. That is the two-tap selection.
    // Dismissal now happens on a document-level press that neither preventDefaults nor stops
    // propagation, so the one press both closes the menu and lands on the row.
    //
    // Real mobile emulation, and the agent must expose patchable models or the menu this is
    // about does not render at all.
    [SkippableFact]
    public async Task MobileViewport_ATapOnATopicWhileTheModelMenuIsOpen_SelectsItInThatSameTap()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync(isMobile: true);
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        await CreateTopicAsync(page, "Menu tap target topic message");
        await CreateTopicAsync(page, "Menu tap decoy topic message");

        // The aim below is a coordinate resolved in one round trip and tapped in the next, so the
        // list must not be reordering in between — rows sort by LastMessageAt and jump as replies
        // land, which drops the tap on whichever conversation slid under the point.
        await WebChatE2ETests.WaitForRowsToStopMovingAsync(page);

        await TapHearthHandleAsync(page);
        await TapHearthHandleAsync(page);
        await Assertions.Expect(page.Locator(".hearth-search-input")).ToBeVisibleAsync();
        await page.WaitForTimeoutAsync(800);

        var trigger = page.Locator(".hearth .agent-config-trigger:visible").First;
        await trigger.ClickAsync(new LocatorClickOptions { Timeout = 10_000 });
        var menu = page.Locator(".agent-config-menu:visible");
        await Assertions.Expect(menu).ToBeVisibleAsync();

        // At the full detent the menu opens downwards, over the list — a row beneath it is
        // genuinely the menu's to receive. Aim at a row that is actually exposed, which is what
        // a person tapping a conversation they can see is doing.
        var aim = await page.EvaluateAsync<System.Text.Json.JsonElement>(
            """
            () => {
                for (const row of document.querySelectorAll('.topic-item')) {
                    const r = row.getBoundingClientRect();
                    const x = r.x + r.width / 2, y = r.y + r.height / 2;
                    const hit = document.elementFromPoint(x, y);
                    if (hit && row.contains(hit)) {
                        return {x, y, name: row.querySelector('.topic-name').textContent};
                    }
                }
                return {x: -1, y: -1, name: ''};
            }
            """);

        var aimedAt = aim.GetProperty("name").GetString();
        aimedAt.ShouldNotBeNullOrEmpty();

        // Assert against what elementFromPoint sees at touchstart, not against the row measured
        // above: the aim is already one round trip old when the finger lands, and asserting on it
        // would test the harness's timing rather than the app's behaviour.
        await page.EvaluateAsync(
            """
            () => {
                window.__under = null;
                document.addEventListener('touchstart', e => {
                    const t = e.touches[0];
                    const hit = document.elementFromPoint(t.clientX, t.clientY);
                    const row = hit ? hit.closest('.topic-item') : null;
                    window.__under = row ? row.querySelector('.topic-name').textContent : null;
                }, { capture: true, once: true });
            }
            """);

        await page.Touchscreen.TapAsync(
            (float)aim.GetProperty("x").GetDouble(), (float)aim.GetProperty("y").GetDouble());

        await Assertions.Expect(menu).Not.ToBeVisibleAsync();

        var under = await page.EvaluateAsync<string?>("() => window.__under");
        under.ShouldNotBeNullOrEmpty($"the tap aimed at \"{aimedAt}\" landed on no conversation row at all");

        await Assertions.Expect(
                page.Locator(".topic-item.selected .topic-name", new PageLocatorOptions { HasText = under }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
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

    // Dragging the sheet open arms a trailing-click swallow (app.js _onUp). A genuine tap that
    // follows the drag starts with its own pointerdown, which must disarm the swallow — only the
    // stray click of the drag gesture itself (mouse release, no new pointerdown) may be eaten.
    // The drag and the tap are dispatched synthetically from inside the page so the tap lands a
    // deterministic 120ms after the drag ends, inside the swallow window.
    [SkippableFact]
    public async Task MobileViewport_TapRightAfterDraggingTheSheetOpenSelectsTheTopic()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync(hasTouch: true);
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        await CreateTopicAsync(page, "Drag tap target topic message");
        await CreateTopicAsync(page, "Drag tap decoy topic message");

        var switched = await page.EvaluateAsync<bool>(
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
                    document.dispatchEvent(new PointerEvent('pointerup', p(y, 1)));
                    setTimeout(tap, 120);
                };
                const tap = () => {
                    const row = [...document.querySelectorAll('.topic-item')]
                        .find(r => r.textContent.includes('Drag tap target'));
                    row.dispatchEvent(new PointerEvent('pointerdown', p(300, 2)));
                    row.dispatchEvent(new PointerEvent('pointerup', p(300, 2)));
                    row.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }));
                    // Poll rather than a fixed delay: the class arrives on the next Blazor
                    // render, which under full-suite load can take well over half a second.
                    const deadline = performance.now() + 5000;
                    const settled = () => row.classList.contains('selected') ? resolve(true)
                        : performance.now() > deadline ? resolve(false)
                        : setTimeout(settled, 100);
                    settled();
                };
                requestAnimationFrame(step);
            })
            """);

        switched.ShouldBeTrue("the tap 120ms after the drag must select the topic, not be swallowed");
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

        await CreateTopicAsync(page, "Drift tap target topic message");
        await CreateTopicAsync(page, "Drift tap decoy topic message");

        await TapHearthHandleAsync(page);
        await TapHearthHandleAsync(page);
        await Assertions.Expect(page.Locator(".hearth-search-input")).ToBeVisibleAsync();

        var switched = await page.EvaluateAsync<bool>(
            """
            () => new Promise(resolve => {
                const row = [...document.querySelectorAll('.topic-item')]
                    .find(r => r.textContent.includes('Drift tap target'));
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
            """);

        switched.ShouldBeTrue("a 12px-drift tap must still select the topic, not become a sheet gesture");
    }

    // The drawer's whole purpose: the conversation under the finger is the one that opens — and
    // that has to hold during the 280ms the sheet spends settling into a detent, which is exactly
    // when a thumb arrives after flicking it open. While the transform moves, the press and the
    // release land on different rows, so the browser resolves the click to their common ancestor
    // (.hearth-rows) and the row's own handler never runs: the tap is silently spent, and only a
    // second one, after the sheet has stopped, selects anything.
    //
    // Asserting against what elementFromPoint saw at touchstart rather than a rect measured
    // beforehand — a measurement taken before the tap is already stale by the time the finger
    // lands, which would test the harness's timing rather than the app's behaviour.
    [SkippableFact]
    public async Task MobileViewport_ATapWhileTheSheetIsStillSettling_OpensTheRowUnderTheFinger()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync(isMobile: true);
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        await CreateTopicAsync(page, "Settle tap alpha topic message");
        await CreateTopicAsync(page, "Settle tap bravo topic message");
        await CreateTopicAsync(page, "Settle tap delta topic message");

        // Stretch the settle so the finger provably arrives while the sheet is still travelling;
        // at its real 280ms the tap races the harness's own round trips and proves nothing.
        await page.AddStyleTagAsync(new PageAddStyleTagOptions
        {
            Content = ".hearth { transition-duration: 3s !important; }"
        });

        // Peek → Half → Full. The second tap starts the travel the finger will arrive during.
        await TapHearthHandleAsync(page);
        await TapHearthHandleAsync(page);

        await page.EvaluateAsync(
            """
            () => {
                window.__under = null;
                window.__moving = null;
                document.addEventListener('touchstart', e => {
                    const t = e.touches[0];
                    const hit = document.elementFromPoint(t.clientX, t.clientY);
                    const row = hit ? hit.closest('.topic-item') : null;
                    window.__under = row ? row.querySelector('.topic-name').textContent : null;
                    window.__moving = getComputedStyle(document.querySelector('.hearth')).transform;
                }, { capture: true, once: true });
            }
            """);

        await page.WaitForTimeoutAsync(400);
        await page.Touchscreen.TapAsync(195, 650);

        // Give the click, and the render it causes, room to land before reading the outcome.
        await page.WaitForTimeoutAsync(1_500);

        var outcome = await page.EvaluateAsync<string>(
            """
            () => {
                const selected = document.querySelector('.topic-item.selected .topic-name');
                return (window.__under ?? '<no row under the finger>')
                    + ' => ' + (selected ? selected.textContent : '<nothing selected>')
                    + ' [sheet at touchstart: ' + window.__moving + ']';
            }
            """);

        var under = outcome.Split(" => ")[0];
        var trailer = outcome[(outcome.IndexOf(" [sheet at touchstart:", StringComparison.Ordinal))..];
        under.ShouldStartWith("Settle tap");
        outcome.ShouldBe($"{under} => {under}{trailer}");
    }

    private static async Task CreateTopicAsync(IPage page, string message)
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