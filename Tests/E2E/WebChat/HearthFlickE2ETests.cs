using System.Text.Json;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;
using Xunit.Abstractions;

namespace Tests.E2E.WebChat;

// Pins "one tap selects a conversation" against the gesture that broke it: a flick.
//
// The discriminator the reporter confirmed on the device: the drawer only eats a tap when the
// sheet is *flicked* open to Full in one fast swipe. Opening to Half, or sliding slowly to Full,
// works. So the two tests here differ in exactly one variable — the velocity of the drag that
// opens the sheet — and end in the same place (Full detent), with the same tap on the same row.
//
// Playwright has no touch-drag API, so both gestures are dispatched over CDP
// (Input.dispatchTouchEvent): real, trusted touch input through Chromium's own gesture
// recogniser. Every previous "gesture" test in this suite built its drag from
// `new PointerEvent(...)` inside the page — untrusted events that call the app's handlers but
// never enter the input pipeline, so they can neither produce a fling nor a compatibility click.
// That is why four fixes went green while the phone kept failing.
[Collection(WebChatE2ECollections.Flick)]
[Trait("Category", "E2E")]
public sealed class HearthFlickE2ETests(WebChatE2EFixture fixture, ITestOutputHelper output)
{
    private const double FlickThreshold = -0.6;     // app.js _settle: `if (h._vy < -FLICK) detent = 'Full'`

    [SkippableFact]
    public async Task MobileViewport_FlickingTheSheetOpenThenTappingARow_SelectsThatRow()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var run = await RunGestureThenTapAsync(fast: true);

        // The gesture must genuinely be a flick by the app's own measure, or this test has
        // measured nothing at all.
        run.Vy.ShouldBeLessThan(FlickThreshold, run.Report);
        run.DetentAfterGesture.Contains("detent-full").ShouldBeTrue(run.Report);

        run.SelectedAfterTap.ShouldBe(run.AimedRow, run.Report);
    }

    // The control. Same start (Peek), same end (Full), same tap — only slower. The reporter says
    // this one works; if it fails too, the tests are measuring something other than the bug.
    [SkippableFact]
    public async Task MobileViewport_SlidingTheSheetOpenSlowlyThenTappingARow_SelectsThatRow()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var run = await RunGestureThenTapAsync(fast: false);

        // Deliberately NOT a flick: the position-ratio branch of _settle commits Full instead.
        run.Vy.ShouldBeGreaterThan(FlickThreshold, run.Report);
        run.DetentAfterGesture.Contains("detent-full").ShouldBeTrue(run.Report);

        run.SelectedAfterTap.ShouldBe(run.AimedRow, run.Report);
    }

    // The cure for the flick is a non-passive `touchmove` listener on the whole sheet, so the one
    // thing it could plausibly break is the conversation list's own scrolling. `_onDown` bails for
    // targets inside `.hearth-rows` without setting `_dragging`, which is the gate that keeps the
    // list native; this proves that gate end to end rather than by reading it.
    [SkippableFact]
    public async Task MobileViewport_WithTheSheetOpen_DraggingTheConversationListStillScrollsIt()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await fixture.CreatePageAsync(hasTouch: true, isMobile: true);
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);
        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        var tag = Guid.NewGuid().ToString("N")[..4];
        await CreateTopicAsync(page, $"Scroll one {tag} answer briefly");
        await CreateTopicAsync(page, $"Scroll two {tag} answer briefly");
        await CreateTopicAsync(page, $"Scroll three {tag} answer briefly");
        await WebChatE2ETests.WaitForRowsToStopMovingAsync(page);

        await EnsurePeekAsync(page);
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var handle = await CentreOfAsync(page, ".hearth-handle");
        await DragAsync(cdp, handle.X, handle.Y, handle.Y - 560, 56, 18);
        await WaitForSheetSettledAsync(page);

        // A handful of seeded rows need not overflow a full-height sheet, and a list that cannot
        // scroll would pass this test for the wrong reason. Shrink the scroller so the rows it
        // already holds overflow it.
        await page.EvaluateAsync("() => document.querySelector('.hearth-rows').style.maxHeight = '120px'");
        var overflows = await page.EvaluateAsync<bool>(
            "() => { const r = document.querySelector('.hearth-rows'); return r.scrollHeight > r.clientHeight + 8; }");
        overflows.ShouldBeTrue("the conversation list does not overflow, so this test would prove nothing");

        var rows = await CentreOfAsync(page, ".hearth-rows");
        await DragAsync(cdp, rows.X, rows.Y + 40, rows.Y - 40, 16, 12);
        await page.WaitForTimeoutAsync(500);

        var scrollTop = await page.EvaluateAsync<double>(
            "() => document.querySelector('.hearth-rows').scrollTop");
        scrollTop.ShouldBeGreaterThan(0, "the sheet's touchmove handler swallowed the list's own scrolling");
    }

    private sealed record Run(
        double Vy,
        string DetentAfterGesture,
        string AimedRow,
        string SelectedAfterTap,
        string Report);

    private async Task<Run> RunGestureThenTapAsync(bool fast)
    {
        // isMobile is not a synonym for hasTouch: it turns on Chromium's mobile emulation
        // (390x844, DSF 3, hasTouch) and with it the tap heuristics a phone is judged by.
        var page = await fixture.CreatePageAsync(hasTouch: true, isMobile: true);
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);
        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        // Topics from earlier runs survive in the stack, so unique names keep "the row I aimed at"
        // and "the row that got selected" from being two different rows with the same text.
        var tag = Guid.NewGuid().ToString("N")[..4];
        await CreateTopicAsync(page, $"Row alpha {tag} answer briefly");
        await CreateTopicAsync(page, $"Row bravo {tag} answer briefly");
        await CreateTopicAsync(page, $"Row delta {tag} answer briefly");
        var rowsAtRest = await WebChatE2ETests.WaitForRowsToStopMovingAsync(page);

        await EnsurePeekAsync(page);
        var overlayInTheWay = await page.Locator(".approval-modal-overlay").CountAsync();
        overlayInTheWay.ShouldBe(0, "an approval overlay is covering the sheet; the gesture would never reach it");

        await InstallTapLogAsync(page);

        var cdp = await page.Context.NewCDPSessionAsync(page);

        // Start on the peek bar's grab handle. _onDown (app.js:361-366) refuses to start a drag
        // from a button unless it is `.hearth-handle`, and Chromium's touch adjustment retargets
        // the middle of `.hearth-peek` onto the config trigger — so the handle is the only point
        // on the peek bar from which the sheet actually drags.
        var start = await CentreOfAsync(page, ".hearth-handle");

        // Velocity is controlled by pixels-per-step: each CDP round trip is itself ~16ms, one
        // touch frame, so a delay only ever slows the gesture down. Fast = 37px/frame ≈ -2.2 px/ms,
        // straight into _settle's velocity branch. Slow = 10px per ~34ms ≈ -0.3 px/ms, which misses
        // that branch — so it has to travel far enough (712px of peek offset down past 0.28 × 776px)
        // for the position-ratio branch to commit Full instead. Same destination, no fling.
        var travel = fast ? 300.0 : 560.0;
        var steps = fast ? 8 : 56;
        var gapMs = fast ? 0 : 18;

        var swipeStartedAt = DateTime.UtcNow;
        await DragAsync(cdp, start.X, start.Y, start.Y - travel, steps, gapMs);
        var releasedAt = DateTime.UtcNow;

        // Read the app's own velocity the instant the drag released — do not hope, assert.
        var vy = await page.EvaluateAsync<double>("() => window.hearthSheet._vy");
        var gestureLog = await DrainLogAsync(page, $"GESTURE ({(fast ? "FLICK" : "SLOW SLIDE")}, {travel}px / {steps} steps / {gapMs}ms gap)");

        var settle = await WaitForSheetSettledAsync(page);
        var detentAfterGesture = settle.SheetClass;

        var aim = await AimAtAnUnselectedRowAsync(page, tag);
        var msSinceRelease = (DateTime.UtcNow - releasedAt).TotalMilliseconds;

        var selected = "<never tapped: no reachable unselected row>";
        var tapLog = "== TAP (not attempted) ==";
        var secondTapLog = "== SECOND TAP (not needed: the first one worked) ==";
        if (aim.Name.Length > 0)
        {
            await page.Touchscreen.TapAsync(aim.X, aim.Y);
            selected = await WaitForSelectionAsync(page, aim.Name);
            tapLog = await DrainLogAsync(page, $"TAP #1 on '{aim.Name}' @{aim.X:0},{aim.Y:0} — {msSinceRelease:0}ms after release");

            // Only when the first tap did nothing: the reporter's symptom is literally "the second
            // tap works", so prove that the identical tap at the identical point now lands.
            if (selected != aim.Name)
            {
                await page.Touchscreen.TapAsync(aim.X, aim.Y);
                var secondSelected = await WaitForSelectionAsync(page, aim.Name);
                secondTapLog = await DrainLogAsync(page,
                    $"TAP #2 on '{aim.Name}' @{aim.X:0},{aim.Y:0} — identical tap, selected now '{secondSelected}'");
            }
        }

        var report = string.Join('\n',
            $"gesture      : {(fast ? "FLICK" : "SLOW SLIDE")} {travel}px over {steps} steps, {gapMs}ms gap, "
                + $"from {start.X:0},{start.Y:0}, {(releasedAt - swipeStartedAt).TotalMilliseconds:0}ms wall",
            $"app _vy      : {vy:0.000} px/ms  (flick threshold {FlickThreshold})",
            $"settle       : {settle.SheetClass} | transform {settle.Transform} | settled after {settle.ElapsedMs:0}ms"
                + $" ({(settle.Settled ? "stable" : "STILL MOVING at cap")})",
            $"aimed row    : '{aim.Name}' at {aim.X:0},{aim.Y:0} (hit-test said '{aim.Hit}')",
            $"tap fired    : {msSinceRelease:0}ms after touchEnd",
            $"selected now : '{selected}'  (wanted '{aim.Name}')",
            $"rows at rest : {rowsAtRest}",
            "",
            aim.Dump,
            "",
            gestureLog,
            "",
            tapLog,
            "",
            secondTapLog);

        output.WriteLine(report);
        return new Run(
            vy,
            detentAfterGesture,
            aim.Name.Length > 0 ? aim.Name : "<no reachable unselected row>",
            selected,
            report);
    }

    // ---- gesture synthesis -------------------------------------------------------------------

    private static Dictionary<string, object> Point(double x, double y) =>
        new() { ["x"] = x, ["y"] = y, ["id"] = 1 };

    // touchEnd/touchCancel must carry an EMPTY touchPoints array; touchStart/touchMove must carry
    // at least one. CDP rejects the call otherwise.
    private static Task TouchAsync(ICDPSession cdp, string type, params Dictionary<string, object>[] points) =>
        cdp.SendAsync("Input.dispatchTouchEvent", new Dictionary<string, object>
        {
            ["type"] = type,
            ["touchPoints"] = points
        });

    private static async Task DragAsync(ICDPSession cdp, double x, double yFrom, double yTo, int steps, int gapMs)
    {
        await TouchAsync(cdp, "touchStart", Point(x, yFrom));

        foreach (var i in Enumerable.Range(1, steps))
        {
            await TouchAsync(cdp, "touchMove", Point(x, yFrom + (yTo - yFrom) * i / steps));
            if (gapMs > 0)
            {
                await Task.Delay(gapMs);
            }
        }

        await TouchAsync(cdp, "touchEnd");
    }

    // ---- instrumentation ---------------------------------------------------------------------

    private static Task InstallTapLogAsync(IPage page) => page.EvaluateAsync(
        """
        () => {
            window.__taplog = [];
            const desc = el => {
                if (!el) return '<none>';
                const cls = el.className && el.className.baseVal !== undefined
                    ? el.className.baseVal : (el.className || '');
                const row = el.closest ? el.closest('.topic-item') : null;
                const name = row && row.querySelector('.topic-name')
                    ? ' {row:' + row.querySelector('.topic-name').textContent.trim() + '}' : '';
                return el.tagName.toLowerCase()
                    + (String(cls).trim() ? '.' + String(cls).trim().replace(/\s+/g, '.') : '')
                    + name;
            };
            const sheetState = () => {
                const h = window.hearthSheet || {};
                const el = document.querySelector('.hearth');
                const r = el && el.getBoundingClientRect();
                return '\n               sheet: _dragging=' + h._dragging
                    + ' _axisLocked=' + h._axisLocked
                    + ' _rowsMode=' + h._rowsMode
                    + ' _vy=' + (typeof h._vy === 'number' ? h._vy.toFixed(3) : h._vy)
                    + ' class="' + (el ? el.className : '<none>') + '"'
                    + ' rect=' + (r ? [r.left, r.top, r.width, r.height].map(Math.round).join(',') : '<none>')
                    + ' transform=' + (el ? getComputedStyle(el).transform : '<none>');
            };
            const types = ['touchstart', 'touchmove', 'touchend', 'touchcancel',
                           'pointerdown', 'pointermove', 'pointerup', 'pointercancel',
                           'mousedown', 'mouseup', 'click'];
            for (const t of types) {
                document.addEventListener(t, e => {
                    const pt = (e.touches && e.touches[0]) || (e.changedTouches && e.changedTouches[0]) || e;
                    const x = pt.clientX, y = pt.clientY;
                    const hit = typeof x === 'number' && typeof y === 'number'
                        ? document.elementFromPoint(x, y) : null;
                    let line = t.padEnd(12) + ' t=' + Math.round(e.timeStamp)
                        + ' @' + Math.round(x) + ',' + Math.round(y)
                        + ' target=' + desc(e.target)
                        + ' hit=' + desc(hit);
                    if (t === 'pointerdown' || t === 'click') line += sheetState();
                    window.__taplog.push(line);
                }, { capture: true, passive: true });
            }
        }
        """);

    // Drained per phase: a setup click's own `click` in the buffer would otherwise satisfy the
    // very thing this test is looking for.
    private static async Task<string> DrainLogAsync(IPage page, string label)
    {
        var body = await page.EvaluateAsync<string>(
            """
            () => {
                const sel = document.querySelector('.topic-item.selected .topic-name');
                const out = '  selected=' + (sel ? sel.textContent.trim() : '<none>')
                    + '  hearth="' + document.querySelector('.hearth').className + '"\n  '
                    + (window.__taplog.length ? window.__taplog.join('\n  ') : '<no events>');
                window.__taplog = [];
                return out;
            }
            """);
        return $"== {label} ==\n{body}";
    }

    // ---- page state --------------------------------------------------------------------------

    private static async Task EnsurePeekAsync(IPage page)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (await page.Locator(".hearth.detent-peek").CountAsync() > 0)
            {
                return;
            }

            await page.Locator(".hearth-handle").First.ClickAsync();

            // The detent lands with the sheet's own 280ms transition. Polled rather than waited
            // out, because a sheet at Full needs two of these to get back down and a flat wait
            // long enough for the worst one is paid on every attempt.
            var settleBy = DateTime.UtcNow.AddMilliseconds(600);
            while (DateTime.UtcNow < settleBy && await page.Locator(".hearth.detent-peek").CountAsync() == 0)
            {
                await page.WaitForTimeoutAsync(50);
            }
        }

        throw new InvalidOperationException("Could not return the hearth sheet to the peek detent");
    }

    private sealed record Settle(string SheetClass, string Transform, double ElapsedMs, bool Settled);

    // "Reached full and settled": the detent class landed and the composited transform stopped
    // changing. Capped, because the whole point is to tap while the fling Chromium derived from
    // the flick may still be alive — an unbounded wait would tap after it and hide the bug.
    //
    // Two equal reads are not a settle, and taking them for one is how this returns a sheet that is
    // still travelling. The sampler is 40 ms apart and the machine running the suite stalls the main
    // thread for longer than that, so a transition mid-flight reads identical twice; the aim is then
    // taken against a sheet that slides on underneath it, and the row above the aimed one is the one
    // the tap lands on. That failure is indistinguishable from the bug this file exists to catch,
    // which is the worst kind of flake to own.
    //
    // So the browser is asked rather than inferred from: a CSS transition is an animation on the
    // element for as long as it runs, however little of it a stalled sampler managed to see. The
    // repeat count stays on top of that, because a transition that has been committed but has not
    // begun is not yet an animation either.
    private static async Task<Settle> WaitForSheetSettledAsync(IPage page)
    {
        var started = DateTime.UtcNow;
        var previous = "";
        var unchanged = 0;
        while ((DateTime.UtcNow - started).TotalMilliseconds < 900)
        {
            var state = await ReadSheetAsync(page);
            unchanged = state.Still && state.Transform == previous ? unchanged + 1 : 0;
            if (state.ClassName.Contains("detent-full") && unchanged >= 2)
            {
                return new Settle(
                    state.ClassName, state.Transform, (DateTime.UtcNow - started).TotalMilliseconds, true);
            }

            previous = state.Transform;
            await page.WaitForTimeoutAsync(40);
        }

        var final = await ReadSheetAsync(page);
        return new Settle(
            final.ClassName, final.Transform, (DateTime.UtcNow - started).TotalMilliseconds, false);
    }

    private sealed record SheetRead(string ClassName, string Transform, bool Still);

    private static async Task<SheetRead> ReadSheetAsync(IPage page)
    {
        var state = await page.EvaluateAsync<string[]>(
            """
            () => {
                const el = document.querySelector('.hearth');
                // getAnimations covers a CSS transition for its whole life, including the frame
                // before it starts painting, which is exactly the window a sampler cannot see.
                return [el.className, getComputedStyle(el).transform, String(el.getAnimations().length)];
            }
            """);
        return new SheetRead(state[0], state[1], state[2] == "0");
    }

    // Returns an empty Name rather than throwing when nothing is reachable: the dump it carries is
    // the diagnostic, and a thrown exception would take the whole event log down with it.
    private static async Task<(float X, float Y, string Name, string Hit, string Dump)> AimAtAnUnselectedRowAsync(IPage page, string tag)
    {
        var aim = await page.EvaluateAsync<JsonElement>(
            """
            tag => {
                const desc = el => el
                    ? el.tagName.toLowerCase() + (el.className ? '.' + String(el.className).trim().replace(/\s+/g, '.') : '')
                    : '<null>';
                const lines = [];
                const sheet = document.querySelector('.hearth');
                const rows = document.querySelector('.hearth-rows');
                lines.push('viewport ' + innerWidth + 'x' + innerHeight
                    + ' | hearth "' + (sheet ? sheet.className : '<none>') + '"'
                    + ' rect=' + (sheet ? [sheet.getBoundingClientRect().top,
                                           sheet.getBoundingClientRect().height].map(Math.round).join(',') : '?')
                    + ' | hearth-rows rect=' + (rows ? [rows.getBoundingClientRect().top,
                                                        rows.getBoundingClientRect().height].map(Math.round).join(',') : '?')
                    + ' scrollTop=' + (rows ? rows.scrollTop : '?')
                    + ' scrollHeight=' + (rows ? rows.scrollHeight : '?'));
                let pick = null;
                for (const row of document.querySelectorAll('.topic-item')) {
                    const r = row.getBoundingClientRect();
                    const x = r.x + r.width / 2, y = r.y + r.height / 2;
                    const hit = document.elementFromPoint(x, y);
                    const reachable = !!hit && row.contains(hit);
                    const name = row.querySelector('.topic-name').textContent.trim();
                    lines.push('  row "' + name + '"'
                        + (row.classList.contains('selected') ? ' SELECTED' : '')
                        + ' rect=' + [r.x, r.y, r.width, r.height].map(Math.round).join(',')
                        + ' centre=' + Math.round(x) + ',' + Math.round(y)
                        + ' hit=' + desc(hit)
                        + (reachable ? ' REACHABLE' : ' unreachable'));
                    if (!pick && reachable && name.includes(tag) && !row.classList.contains('selected')) {
                        pick = { x, y, name, hit: desc(hit) };
                    }
                }
                if (!document.querySelectorAll('.topic-item').length) lines.push('  <no .topic-item at all>');
                return { x: pick ? pick.x : -1, y: pick ? pick.y : -1,
                         name: pick ? pick.name : '', hit: pick ? pick.hit : '<none reachable>',
                         dump: '== ROWS AT AIM TIME ==\n' + lines.join('\n') };
            }
            """, tag);
        return ((float)aim.GetProperty("x").GetDouble(),
                (float)aim.GetProperty("y").GetDouble(),
                aim.GetProperty("name").GetString() ?? "",
                aim.GetProperty("hit").GetString() ?? "",
                aim.GetProperty("dump").GetString() ?? "");
    }

    private static async Task<string> WaitForSelectionAsync(IPage page, string wanted)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var selected = "";
        while (DateTime.UtcNow < deadline)
        {
            selected = await page.EvaluateAsync<string>(
                """
                () => {
                    const n = document.querySelector('.topic-item.selected .topic-name');
                    return n ? n.textContent.trim() : '<none>';
                }
                """);
            if (selected == wanted)
            {
                return selected;
            }

            await page.WaitForTimeoutAsync(100);
        }

        return selected;
    }

    // ---- seeding -----------------------------------------------------------------------------

    private static async Task CreateTopicAsync(IPage page, string message)
    {
        var chatInput = page.Locator("textarea.chat-input");
        await WebChatE2ETests.ClickThroughApprovalsAsync(page, page.Locator(".hearth-new:visible").First);
        await Assertions.Expect(chatInput).ToBeEnabledAsync(new LocatorAssertionsToBeEnabledOptions { Timeout = 10_000 });
        await chatInput.FillAsync(message);
        await chatInput.PressAsync("Enter");
        await page.Locator(".topic-item", new PageLocatorOptions { HasText = message[..16] })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 30_000 });
    }

    private static async Task<(float X, float Y)> CentreOfAsync(IPage page, string selector)
    {
        var box = await page.Locator(selector + ":visible").First.BoundingBoxAsync()
            ?? throw new InvalidOperationException($"{selector} has no bounding box");
        return ((float)(box.X + box.Width / 2), (float)(box.Y + box.Height / 2));
    }
}