using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// The taps that arrive while the sheet is still moving. Each one drives a gesture and then waits
// for the drawer to settle under it, which is what makes them the long half of the suite.
[Collection(WebChatE2ECollections.HearthTap)]
[Trait("Category", "E2E")]
public sealed class HearthTapE2ETests(WebChatE2EFixture fixture) : HearthE2EBase
{
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

        // The travel above is stretched to three seconds, so what this has to wait for is not a
        // duration but a position: the finger must come down on a row, with the sheet still moving
        // under it. A fixed wait assumed the travel had already begun when it was called, and on a
        // loaded machine the second handle tap rendered late — 400ms in, the sheet was still short
        // of the tap point and the finger landed on nothing, failing with no row under it. Waiting
        // for the row to arrive at the point leaves seconds of travel still to run, so the tap is
        // no less mid-settle than it was, and the harness's own timing is out of the assertion.
        //
        // The row has to be *comfortably* over the point rather than merely touching it. Waiting for
        // first contact returned the instant a row's leading edge crossed y, and the sheet then kept
        // moving during the round trip that follows — on a loaded machine that was enough for the
        // row to slide back off before the finger arrived, and the tap again landed on nothing. A
        // margin means the wait ends with the row spanning the point, so the same drift leaves it
        // still covered; the sheet is no less in motion for it.
        await page.WaitForFunctionAsync(
            """
            () => {
                const hit = document.elementFromPoint(195, 650);
                const row = hit ? hit.closest('.topic-item') : null;
                if (!row) return false;
                const box = row.getBoundingClientRect();
                return 650 - box.top >= 20 && box.bottom - 650 >= 20;
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 10_000, PollingInterval = 16 });
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
}