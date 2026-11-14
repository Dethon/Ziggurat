using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// The microphone's longer runs — the cap, the screen held awake, the path a dictation opens on —
// in a collection of their own: as one chain with the holds and the tap they were the run's tail.
[Collection(WebChatE2ECollections.DictationLong)]
[Trait("Category", "E2E")]
public sealed class WebChatDictationLongE2ETests(WebChatE2EFixture fixture)
    : DictationE2EBase(fixture)
{
    // Short enough that the cap case need not wait out the stack's two minutes, and long enough
    // that opening the microphone and the press that precedes it both fit inside it comfortably.
    private const int CapMs = 4_000;

    // A pocketed phone must not record indefinitely. The cap is the server's number, learned
    // through the same limits call the attachment rules arrive on — the client carries none.
    [SkippableFact]
    public async Task ADictationThatRunsPastTheCap_StopsItselfAndTranscribesWhatItHas()
    {
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");
        Fixture.TranscriptionStatus = 200;
        Fixture.Transcript = "se paró solo";

        var page = await OpenAsync();

        // The stack's cap is two minutes, which is the number every other case here needs to be
        // out of its way. The limits response lands whenever the connection produces it, so the
        // configuration is pinned first: nothing may put the stack's number back over this case's.
        await page.EvaluateAsync("() => { window.dictation.configure = () => {}; }");

        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await PressableMicAsync(page);

        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // The cap counts from the press, and the microphone takes as long as the machine takes to
        // open — five seconds under Chromium's fake device here. A cap shortened before the press
        // went off before there was anything to record, and the case reported "the microphone had
        // not finished opening" on every run. So it is shortened only now, to this many seconds
        // past wherever the clock has got to, and re-armed as the ticket would re-arm it.
        await page.EvaluateAsync(
            "() => { const d = window.dictation; const run = d._run;"
            + " d._limits = { maxMs: performance.now() - run.startedAt + " + CapMs + ", minMs: d._limits.minMs };"
            + " d._armCap(run); }");

        // Never released: the words arrive because the recording ended itself.
        await Assertions.Expect(page.Locator("textarea.chat-input"))
            .ToHaveValueAsync(
                "se paró solo",
                new LocatorAssertionsToHaveValueOptions { Timeout = CapMs + 30_000 });

        await TouchAsync(cdp, "touchEnd");
    }

    // The verdict of the route experiment, made permanent. The wedge that kept coming back was
    // per-path: the raw capture path came up born-dead until a reboot while the processed path
    // recorded fine through the same wedge — confirmed live on the phone that suffers it. So the
    // processed path is the only path: every open asks for echo cancellation, and there is no
    // probe, no mid-run swap and no remembered route left to reason about. One grant per
    // dictation, wedge-immune by default.
    [SkippableFact]
    public async Task EveryDictation_OpensOnceAndOnTheProcessedPath()
    {
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");
        Fixture.TranscriptionStatus = 200;
        Fixture.Transcript = "una sola ruta";

        var page = await OpenAsync();
        await page.EvaluateAsync(
            """
            () => {
                window.__opens = [];
                const open = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
                navigator.mediaDevices.getUserMedia = constraints => {
                    window.__opens.push(constraints);
                    return open(constraints);
                };
            }
            """);

        var cdp = await page.Context.NewCDPSessionAsync(page);
        await DictateAsync(cdp, page, HoldMs);

        // Words arriving proves the processed path carried a real recording end to end.
        await WithTraceAsync(page, () => Assertions.Expect(page.Locator("textarea.chat-input"))
            .ToHaveValueAsync("una sola ruta", new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 }));

        (await page.EvaluateAsync<int>("() => window.__opens.length")).ShouldBe(1);
        (await page.EvaluateAsync<bool>(
            "() => window.__opens[0].audio.echoCancellation === true")).ShouldBeTrue();
    }

    // A phone dims and locks a screen nothing has touched for a while, and someone holding the
    // microphone and talking into it is touching nothing: the longer the dictation, the likelier it
    // is cut off by the screen going out. On this app that is not merely dark — the lock hides the
    // page, and a hidden page throws the recording away — so a sentence spoken into a phone left to
    // itself ends as nothing at all. The screen is asked to stay awake for exactly as long as the
    // microphone is open, and the lock is let go at both ends a recording has: the release that
    // transcribes it, and the slide that throws it away. A lock still held after that is a phone
    // that never sleeps again.
    [SkippableFact]
    public async Task ADictationInFlight_HoldsTheScreenAwakeAndLetsGoWhenItEnds()
    {
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");
        Fixture.TranscriptionStatus = 200;
        Fixture.Transcript = "la pantalla sigue encendida";

        var page = await OpenAsync();

        // Every lock the page asks for, kept where the test can ask about it afterwards. Stubbed
        // rather than taken for real because a headless browser on a machine with no screen is not
        // where the platform's own answer means anything; what is being pinned is that this app asks
        // and lets go, at the right two moments.
        await page.EvaluateAsync(
            """
            () => {
                window.__locks = [];
                Object.defineProperty(navigator, 'wakeLock', {
                    configurable: true,
                    value: {
                        request: async type => {
                            const lock = {
                                type: type,
                                released: false,
                                release: async () => { lock.released = true; }
                            };
                            window.__locks.push(lock);
                            return lock;
                        }
                    }
                });
            }
            """);

        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await PressableMicAsync(page);

        // A recording that ends as words.
        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // The lock is asked for at the press and granted a turn later, so it is waited for as the
        // state it is rather than assumed to be there by the time the strip is.
        await page.WaitForFunctionAsync(
            "() => window.__locks.length > 0",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
        var held = await page.EvaluateAsync<string[]>(
            "() => window.__locks.filter(l => !l.released).map(l => l.type)");
        held.ShouldBe(["screen"]);

        await Task.Delay(HoldMs);
        await TouchAsync(cdp, "touchEnd");

        await Assertions.Expect(page.Locator("textarea.chat-input"))
            .ToHaveValueAsync(
                "la pantalla sigue encendida", new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });
        (await page.EvaluateAsync<bool>("() => window.__locks.every(l => l.released)"))
            .ShouldBeTrue("the screen was still being held awake after the dictation ended");

        // And a recording thrown away, which ends down a different path entirely.
        await page.Locator("textarea.chat-input").FillAsync("");
        var again = await PressableMicAsync(page);
        await TouchAsync(cdp, "touchStart", Point(again.X, again.Y));
        await page.WaitForFunctionAsync(
            "() => window.__locks.length > 1",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
        await TouchAsync(cdp, "touchMove", Point(again.X - 120, again.Y));
        await TouchAsync(cdp, "touchEnd");

        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]")).ToBeHiddenAsync();
        await page.WaitForFunctionAsync(
            "() => window.__locks.every(l => l.released)",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
    }
}