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

        // Conversations outlive the run that made them and the user index restarts every run, so
        // an untagged name matches an earlier run's row as well as this one's — which makes the row
        // this test aims at and the row it then reads back two different rows with one name. The
        // tag leads because CreateTopicAsync matches on the first sixteen characters.
        var tag = Guid.NewGuid().ToString("N")[..4];
        await CreateTopicAsync(page, $"{tag} target menu tap");
        await CreateTopicAsync(page, $"{tag} decoy menu tap");

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

        var tag = Guid.NewGuid().ToString("N")[..4];
        await CreateTopicAsync(page, $"{tag} target drag tap");
        await CreateTopicAsync(page, $"{tag} decoy drag tap");

        // The row is named rather than found by a literal, so an earlier run's row with the same
        // words cannot be the one this drags and taps.
        var switched = await page.EvaluateAsync<bool>(
            """
            target => new Promise(resolve => {
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
                        .find(r => r.textContent.includes(target));
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
            """, $"{tag} target drag tap");

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

        var tag = Guid.NewGuid().ToString("N")[..4];
        await CreateTopicAsync(page, $"{tag} alpha settle tap");
        await CreateTopicAsync(page, $"{tag} bravo settle tap");
        await CreateTopicAsync(page, $"{tag} delta settle tap");

        // Rows sort by LastMessageAt and jump as replies land. The tap point below is measured in
        // one round trip and tapped in a later one, so a reorder in between would drop the finger
        // on whichever conversation slid under it.
        await WebChatE2ETests.WaitForRowsToStopMovingAsync(page);

        // Peek → Half at the sheet's own speed, and settled before anything is measured: the half
        // detent is the resting geometry the tap point is derived from.
        await TapHearthHandleAsync(page);
        await WaitForSheetToSettleAsync(page, "detent-half");

        // Stretch the Half → Full travel and make it linear. Stretching alone was not enough: the
        // real cubic-bezier(.2,.8,.2,1) is front-loaded, so even over three seconds a row crossed
        // any fixed point in about a tenth of one — shorter than the round trips that open the wait
        // below, which is how a run reached the full detent before the first poll and then spent
        // its whole timeout on a sheet that had stopped. Linear travel spends the same distance at
        // one speed, so the rows sweep the point over seconds instead of a flicker.
        //
        // Sixteen seconds rather than eight because the slack this buys is the only thing standing
        // between the wait below and the tap that follows it: the sheet keeps moving during that
        // round trip, and at eight seconds a row cleared the point in about a second and a half —
        // which a loaded machine can spend on one round trip, and a run did, landing the finger
        // between two rows. Halving the speed doubles the slack and costs only the time the rows
        // take to reach the point, which is waited for rather than slept through.
        await page.AddStyleTagAsync(new PageAddStyleTagOptions
        {
            Content = ".hearth { transition-duration: 16s !important; transition-timing-function: linear !important; }"
        });

        // Aim between the list's two resting places: below where the rows sit at Full, above where
        // they sit at Half. A stationary sheet covers this point at neither detent, so the wait can
        // only be satisfied by rows in motion — a run whose travel is already over fails on the
        // wait rather than tapping a parked row and calling it a settling one.
        var band = await page.EvaluateAsync<System.Text.Json.JsonElement>(
            """
            () => {
                const sheet = document.querySelector('.hearth');
                const t = getComputedStyle(sheet).transform;
                const travel = t === 'none' ? 0 : new DOMMatrix(t).m42;   // distance left to the full detent
                const boxes = [...document.querySelectorAll('.topic-item')].map(r => r.getBoundingClientRect());
                if (boxes.length === 0) return { rows: 0, travel, low: 0, high: 0 };
                return {
                    rows: boxes.length,
                    travel,
                    low: Math.max(...boxes.map(b => b.bottom)) - travel,  // list's bottom edge at Full
                    high: Math.min(...boxes.map(b => b.top))              // list's top edge at Half
                };
            }
            """);

        band.GetProperty("rows").GetInt32().ShouldBeGreaterThanOrEqualTo(3, $"the conversation list is missing rows: {band}");
        var low = band.GetProperty("low").GetDouble();
        var high = band.GetProperty("high").GetDouble();
        high.ShouldBeGreaterThan(
            low + 40, "the conversation list is taller than the sheet's travel, so no point is clear of it at rest");
        var aimY = (int)Math.Round((low + high) / 2);

        // Half → Full: the travel the finger arrives during.
        await TapHearthHandleAsync(page);

        await page.EvaluateAsync(
            """
            () => {
                window.__under = null;
                window.__travelLeft = null;
                document.addEventListener('touchstart', e => {
                    const t = e.touches[0];
                    const hit = document.elementFromPoint(t.clientX, t.clientY);
                    const row = hit ? hit.closest('.topic-item') : null;
                    window.__under = row ? row.querySelector('.topic-name').textContent : null;
                    const m = getComputedStyle(document.querySelector('.hearth')).transform;
                    window.__travelLeft = m === 'none' ? 0 : new DOMMatrix(m).m42;
                }, { capture: true, once: true });
            }
            """);

        // What this waits for is a position, not a duration: the finger must come down on a row,
        // with the sheet still moving under it. The row has to be comfortably over the point rather
        // than merely touching it — the sheet keeps travelling during the round trip that sends the
        // tap, and a margin means that drift leaves the row still covering the point. At the linear
        // speed above, 30px of margin is well over a second of slack on either side.
        await page.WaitForFunctionAsync(
            // The point is baked into the expression: Playwright's .NET argument serializer hands a
            // number to the predicate as an object, and elementFromPoint then rejects it as
            // non-finite.
            $$"""
            () => {
                const y = {{aimY}};
                const hit = document.elementFromPoint(195, y);
                const row = hit ? hit.closest('.topic-item') : null;
                if (!row) return false;
                const box = row.getBoundingClientRect();
                return y - box.top >= 30 && box.bottom - y >= 30;
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 25_000, PollingInterval = 16 });
        await page.Touchscreen.TapAsync(195, aimY);

        // Give the click, and the render it causes, room to land before reading the outcome.
        await page.WaitForTimeoutAsync(1_500);

        var outcome = await page.EvaluateAsync<System.Text.Json.JsonElement>(
            """
            () => {
                const selected = document.querySelector('.topic-item.selected .topic-name');
                return {
                    under: window.__under ?? '<no row under the finger>',
                    selected: selected ? selected.textContent : '<nothing selected>',
                    travelLeft: window.__travelLeft ?? -1
                };
            }
            """);

        var under = outcome.GetProperty("under").GetString();
        var selected = outcome.GetProperty("selected").GetString();
        var travelLeft = outcome.GetProperty("travelLeft").GetDouble();

        // This run's rows, not an earlier run's: the assertion below compares two names, and two
        // rows sharing one name would let it pass while the finger and the selection disagreed.
        under.ShouldStartWith(tag);
        travelLeft.ShouldBeGreaterThan(
            24, $"the sheet had all but arrived when the finger landed on \"{under}\", so this proves nothing");
        selected.ShouldBe(under, $"tapped \"{under}\" with {travelLeft:F0}px of travel left");
    }

    // The detent class arrives on a Blazor render and the transform then travels to it, so a
    // measurement taken on the class alone reads a sheet still in flight. Two identical frames
    // with the class already on is the sheet at rest.
    private static async Task WaitForSheetToSettleAsync(IPage page, string detentClass) =>
        await page.WaitForFunctionAsync(
            """
            detent => {
                const sheet = document.querySelector('.hearth');
                if (!sheet || !sheet.classList.contains(detent)) return false;
                const t = getComputedStyle(sheet).transform;
                const y = t === 'none' ? 0 : new DOMMatrix(t).m42;
                const settled = window.__lastSheetY === y;
                window.__lastSheetY = y;
                return settled;
            }
            """,
            detentClass,
            new PageWaitForFunctionOptions { Timeout = 10_000, PollingInterval = 50 });
}