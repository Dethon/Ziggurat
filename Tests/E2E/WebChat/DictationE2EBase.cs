using System.Text.Json;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// A real dictation, end to end: Chromium's fake microphone, the real encoder, the real ticket, the
// real upload through Caddy to the channel server, and a whisper the fixture answers for. The
// gestures go over CDP touch (Input.dispatchTouchEvent) rather than synthetic PointerEvents —
// untrusted events call the handlers but never enter the input pipeline, which is how four hearth
// fixes once went green while the phone kept failing.
//
// Opening a page costs about a second and a half of the suite before a single gesture is made, so a
// case here is one page taken through everything that page can be asked about at once, rather than
// one assertion per page.
//
// The cases are split across two classes because a collection is what xUnit serializes and this
// was the longest chain in the run at forty-six seconds. What used to stop them running at once was
// the stub, not the browser: one shared transcript meant one collection, since every case sets the
// words it expects back. The recording now goes up named for the space it was spoken in and the
// stub answers per space, so a collection can dictate beside another without crossing answers.
public abstract class DictationE2EBase
{
    protected DictationE2EBase(WebChatE2EFixture fixture) => Fixture = fixture;

    protected WebChatE2EFixture Fixture { get; }

    // Comfortably past the 400 ms mis-tap floor and nowhere near the two-minute cap.
    protected const int HoldMs = 900;

    // Long enough that the recording holds the 8192 samples the spectrum below is measured over.
    protected const int SpectrumHoldMs = 1_400;

    // Holds the button until the recorder itself holds `samples` (at the 16 kHz it encodes), at least
    // `atLeastMs`: the strip shows once the microphone opened, but under a loaded machine the audio
    // starts flowing later, and a hold timed from the strip let go with 0.2 s recorded.
    protected static async Task HoldUntilRecordedAsync(IPage page, int samples, int atLeastMs)
    {
        var held = Task.Delay(atLeastMs);
        await WithTraceAsync(page, () => page.WaitForFunctionAsync(
            "n => (window.dictation && window.dictation._run ? window.dictation._run.samples : 0) >= n",
            samples, new PageWaitForFunctionOptions { Timeout = 30_000, PollingInterval = 50 }));
        await held;
    }

    // A wait on the recorder that gives up says what the recorder did: under a full suite every
    // dictation case of one run failed at once (pass 20, ten of nineteen, the silence case hearing
    // "no sound at all") while beside a second suite they passed, and the page's own trace is the only
    // record of where the microphone got to.
    protected static async Task WithTraceAsync(IPage page, Func<Task> wait)
    {
        try
        {
            await wait();
        }
        catch (Exception ex) when (ex is TimeoutException or PlaywrightException)
        {
            var trace = await page.EvaluateAsync<string>(
                "() => JSON.stringify({ trace: window.dictation ? window.dictation._trace : null, " +
                "samples: window.dictation && window.dictation._run ? window.dictation._run.samples : null, " +
                "ctx: window.dictation && window.dictation._run && window.dictation._run.ctx ? window.dictation._run.ctx.state : null })");
            throw new TimeoutException($"{ex.Message}\nDictation trace: {trace}", ex);
        }
    }

    protected async Task<IPage> OpenAsync(int? width = null, int? height = null)
    {
        var page = await Fixture.CreateSignedInPageAsync(Fixture.NextUserIndex(), hasTouch: true);
        if (width is not null && height is not null)
        {
            await page.SetViewportSizeAsync(width.Value, height.Value);
        }
        await WebChatE2ETests.GotoWebChatAsync(page, Fixture.WebChatUrl);
        await WebChatE2ETests.SelectAgentAsync(page);

        // SLOW_MIC_MS=2500 makes every case here open the microphone as slowly as a loaded machine
        // does, which is the one condition this suite goes flaky under: a case that waits out a
        // span instead of the microphone stops the recording before it holds any audio, and the
        // dictation ends as "the microphone had not finished opening" — reported as whatever the
        // case was really asserting. Two cases were failing that way and neither was reproducible
        // on a quiet machine.
        //
        // Above about three and a half seconds the app itself stops working rather than the tests:
        // an AudioContext built that long after the press is outside the gesture's activation
        // window, so `ctx.resume()` never resolves and no recording is made at all (dictation.js).
        // Cases that patch the open themselves add to this, so their effective wait is the sum.
        if (Environment.GetEnvironmentVariable("SLOW_MIC_MS") is { Length: > 0 } slowOpenMs)
        {
            await page.EvaluateAsync($$"""
                () => {
                    const open = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
                    navigator.mediaDevices.getUserMedia = constraints =>
                        new Promise(resolve => setTimeout(() => resolve(open(constraints)), {{slowOpenMs}}));
                }
                """);
        }

        // With nothing typed the right-hand control is the microphone; that is the premise of
        // every case here, so it is waited for rather than assumed.
        await Assertions.Expect(page.Locator("[data-testid=dictation-mic]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        // Visible is not yet listening. The browser gets the microphone element from .NET's first
        // render, one interop call after the button is on screen, and a press that lands in between
        // reaches no handler at all — the case simply does nothing and fails on whatever it asserted
        // next. Registration sets _mic, so that is the thing to wait for.
        await page.WaitForFunctionAsync(
            "() => window.dictation && window.dictation._mic",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        return page;
    }

    // One held dictation, start to release, for cases that care only about how it ended. Held until
    // the recorder holds that long of audio: timed from the strip, a loaded machine let go before
    // the sound arrived and the case read an empty composer as whatever it was asserting.
    protected static async Task DictateAsync(ICDPSession cdp, IPage page, int holdMs)
    {
        var mic = await PressableMicAsync(page);
        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await WithTraceAsync(page, () => Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 }));
        await HoldUntilRecordedAsync(page, holdMs * FakeMicrophoneAudio.TranscriptionRate / 1_000, holdMs);
        await TouchAsync(cdp, "touchEnd");
    }

    // The centre of the microphone, once it is a thing a press can reach.
    //
    // Chromium dispatches no pointer event at all to a disabled button, and the microphone is
    // disabled whenever the composer is — which a SignalR reconnect makes it, and a loaded machine
    // reconnects more. A press landing in that window is swallowed whole: no run starts, no strip
    // appears, and the case spends its timeout waiting for a recording nobody was asked for. The
    // registered element is checked against the one on screen for the same reason, since a press is
    // only a press if it reaches the listener.
    protected static async Task<(double X, double Y)> PressableMicAsync(IPage page)
    {
        await page.WaitForFunctionAsync(
            """
            () => {
                const mic = document.querySelector('[data-testid=dictation-mic]');
                const d = window.dictation;
                return !!(mic && !mic.disabled && d && d._mic === mic && !d._unavailable);
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });

        return await CentreOfAsync(page, "[data-testid=dictation-mic]");
    }

    protected static async Task<(double X, double Y)> CentreOfAsync(IPage page, string selector)
    {
        var box = await page.Locator(selector).BoundingBoxAsync();
        box.ShouldNotBeNull($"{selector} is not on screen");
        return (box.X + box.Width / 2, box.Y + box.Height / 2);
    }

    protected static Dictionary<string, object> Point(double x, double y) =>
        new() { ["x"] = x, ["y"] = y, ["id"] = 1 };

    // touchEnd carries an EMPTY touchPoints array; touchStart/touchMove must carry at least one.
    // CDP rejects the call otherwise.
    protected static Task TouchAsync(ICDPSession cdp, string type, params Dictionary<string, object>[] points) =>
        cdp.SendAsync("Input.dispatchTouchEvent", new Dictionary<string, object>
        {
            ["type"] = type,
            ["touchPoints"] = points
        });
}