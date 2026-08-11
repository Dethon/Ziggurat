using System.Text.Json;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// What the composer does with a dictation once the microphone is working: latching, discarding,
// the two refusals, and the shape the strip keeps beside the controls it replaces.
[Collection(WebChatE2ECollections.DictationComposer)]
[Trait("Category", "E2E")]
public sealed class WebChatDictationComposerE2ETests(WebChatE2EFixture fixture) : DictationE2EBase(fixture)
{
    // A phone is not a desktop here, in two directions at once.
    //
    // Asking for echo cancellation opens the microphone on the platform's voice-call path, whose
    // noise suppression is tuned for a human listening down a phone line rather than for a
    // transcriber. Nothing in a dictation is ever played, so nothing should ask for either.
    //
    // Automatic gain is the exception, and it was learned the hard way: with it off, an Android
    // phone held in the hand and spoken to normally delivered a recording whose loudest peak was
    // under a tenth of full scale, some 20 dB below speech. It is the one part of that chain whose
    // job is the level rather than the shape of the sound, so it stays on.
    //
    // And the graph must still arrive at the context's destination. Chromium renders one that
    // reaches no output; Android does not, and every node in it — the worklet and the level meter
    // both — then goes unpulled and hears silence. Nothing below this can catch that on a desktop,
    // which is exactly why the shape of the graph is asserted rather than only its results.
    [SkippableFact]
    public async Task TheMicrophoneIsOpenedForARecorderRatherThanForAPhoneCall()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        fixture.TranscriptionStatus = 200;
        fixture.Transcript = "hola";

        var page = await OpenAsync();
        // There is no way to ask a node what it is connected to, so the connections are noted as
        // they are made.
        await page.EvaluateAsync(
            """
            () => {
                window.__reachedTheOutput = false;
                const connect = AudioNode.prototype.connect;
                AudioNode.prototype.connect = function (target, ...rest) {
                    window.__reachedTheOutput =
                        window.__reachedTheOutput || target instanceof AudioDestinationNode;
                    return connect.call(this, target, ...rest);
                };
            }
            """);

        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await CentreOfAsync(page, "[data-testid=dictation-mic]");

        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // The level meter hangs off the microphone now rather than sitting in the recording path,
        // so that what is drawn cannot change what is encoded. It still has to be hearing
        // something: a meter stuck at zero is the one thing that would tell someone their input
        // device is misrouted, and it would be saying it about a microphone that is working.
        await page.WaitForFunctionAsync(
            """
            () => {
                const strip = document.querySelector('.dictation-strip');
                return strip
                    && parseFloat(strip.style.getPropertyValue('--dictation-level') || '0') > 0.01;
            }
            """,
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        var opened = JsonDocument.Parse(await page.EvaluateAsync<string>(
            """
            () => {
                const run = window.dictation._run;
                const settings = run.stream.getAudioTracks()[0].getSettings();
                const probe = new AudioContext();
                const deviceRate = probe.sampleRate;
                probe.close();
                return JSON.stringify({
                    echoCancellation: settings.echoCancellation,
                    noiseSuppression: settings.noiseSuppression,
                    autoGainControl: settings.autoGainControl,
                    reachedTheOutput: window.__reachedTheOutput,
                    contextRate: run.ctx.sampleRate,
                    deviceRate: deviceRate
                });
            }
            """)).RootElement;

        await TouchAsync(cdp, "touchEnd");

        // Reported rather than merely absent: a browser that does not say cannot be taken to agree.
        opened.GetProperty("echoCancellation").GetBoolean().ShouldBeFalse();
        opened.GetProperty("noiseSuppression").GetBoolean().ShouldBeFalse();
        opened.GetProperty("autoGainControl").GetBoolean().ShouldBeTrue();

        // A graph that ends nowhere is a graph an Android device never runs.
        opened.GetProperty("reachedTheOutput").GetBoolean().ShouldBeTrue();

        // The graph runs at whatever rate the device runs at. Asking a phone for a 16 kHz graph
        // puts a resampler we cannot see in the capture path; the one that produces the 16 kHz the
        // transcriber needs is ours, downstream, and the same on every device.
        opened.GetProperty("contextRate").GetDouble()
            .ShouldBe(opened.GetProperty("deviceRate").GetDouble());
    }

    // What the page is before anything is recorded: the meter's curve, and the drawn control that
    // stands where Send does. Neither needs a microphone, so both are asked of the same page.
    //
    // The meter exists to tell a microphone that is hearing something from one that is not, and a
    // phone held in the hand delivers speech around a hundredth of full scale. On a linear needle
    // that is zero to the eye, so the two cases it exists to separate looked the same — which is
    // how a real recording was read off a phone as nothing being captured at all.
    //
    // And a drawn microphone rather than an emoji: the platform's font decides what an emoji looks
    // like, and on some of them it is neither the same shape nor the same colour as the icons
    // beside it. A microphone is also a symmetrical object, and an eye reads a lopsided one as a
    // mistake before it reads it as a microphone — the bounding box cannot see this, because the
    // flared side stays inside the arc below it, so the outline itself is sampled and folded about
    // the middle.
    [SkippableFact]
    public async Task AtRest_TheMeterReadsAQuietMicrophoneAsQuietAndTheControlIsADrawnMicrophone()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await OpenAsync();

        var needle = await page.EvaluateAsync<double[]>(
            """
            () => [0, 0.001, 0.005, 0.03, 0.2, 1].map(rms => window.dictation._meter(rms))
            """);

        // Silence is the only reading that is nothing at all.
        needle[0].ShouldBe(0);
        // -60 dBFS is the floor: below a whisper in a quiet room.
        needle[1].ShouldBe(0, tolerance: 0.01);
        // -46 dBFS, which is what the phone actually returned and was read as a dead microphone.
        needle[2].ShouldBeGreaterThan(0.15);
        // -30 dBFS, quiet speech, must be unmistakably alive.
        needle[3].ShouldBeGreaterThan(0.4);
        needle[5].ShouldBe(1);
        // And it only ever rises.
        needle.Zip(needle.Skip(1)).ShouldAllBe(pair => pair.Second >= pair.First);

        var mic = page.Locator("[data-testid=dictation-mic]");
        await Assertions.Expect(mic.Locator("svg")).ToBeVisibleAsync();
        (await mic.InnerTextAsync()).ShouldNotContain("🎤");

        var strayed = await page.EvaluateAsync<double>(
            """
            () => {
                const path = document.querySelector('[data-testid=dictation-mic] path');
                const length = path.getTotalLength();
                const samples = Array.from({ length: 400 }, (_, i) => path.getPointAtLength(i * length / 400));
                // The viewBox is 0 0 24 24, so the axis of a centred drawing is x = 12.
                const mirrored = samples.map(p => ({ x: 24 - p.x, y: p.y }));
                const distance = point => Math.min(...samples.map(
                    s => Math.hypot(s.x - point.x, s.y - point.y)));
                return Math.max(...mirrored.map(distance));
            }
            """);

        // A sample every ~0.15 units of outline, so anything above a fifth of a unit is the drawing
        // being asymmetrical rather than the sampling being coarse.
        strayed.ShouldBeLessThan(0.2);
    }

    // Nobody should have to hold a key down, so a keyboard press starts a latched dictation
    // outright — and Escape is how it is abandoned without reaching for the trash button. The two
    // buttons it puts on screen are drawn for the same reason the microphone is, and the one that
    // ends the dictation reads as sending the words on rather than as halting a machine.
    [SkippableFact]
    public async Task PressingEnterOnTheMicrophone_LatchesAndEscapeThrowsItAway()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        fixture.TranscriptionStatus = 200;
        fixture.Transcript = "esto se descarta";

        var page = await OpenAsync();
        var mic = page.Locator("[data-testid=dictation-mic]");
        await mic.FocusAsync();
        await mic.PressAsync("Enter");

        // Latched from the start: the stop button is there without anything having been released.
        var trash = page.Locator("[data-testid=dictation-trash]");
        var stop = page.Locator("[data-testid=dictation-stop]");
        await Assertions.Expect(stop)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        await Assertions.Expect(trash.Locator("svg")).ToBeVisibleAsync();
        await Assertions.Expect(stop.Locator("svg")).ToBeVisibleAsync();
        (await trash.InnerTextAsync()).ShouldNotContain("🗑");
        (await stop.InnerTextAsync()).ShouldNotContain("■");

        await page.Keyboard.PressAsync("Escape");

        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 15_000 });
        await Task.Delay(1_500);
        await Assertions.Expect(page.Locator("textarea.chat-input")).ToHaveValueAsync("");
    }

    [SkippableFact]
    public async Task SlidingAwayFromTheMicrophone_ThrowsTheRecordingAwayWithNothingInTheComposer()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        fixture.TranscriptionStatus = 200;
        fixture.Transcript = "esto no debería aparecer";

        var page = await OpenAsync();
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await CentreOfAsync(page, "[data-testid=dictation-mic]");

        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Task.Delay(HoldMs);

        // Past the 96 px discard threshold, toward the textarea.
        foreach (var step in Enumerable.Range(1, 8))
        {
            await TouchAsync(cdp, "touchMove", Point(mic.X - step * 20, mic.Y));
            await Task.Delay(16);
        }
        await TouchAsync(cdp, "touchEnd");

        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 15_000 });
        // Nothing arrives late either: a discarded recording makes no request at all, and one that
        // did would be answered by a stub on the same machine well inside this.
        await Task.Delay(1_000);
        await Assertions.Expect(page.Locator("textarea.chat-input")).ToHaveValueAsync("");
    }

    // Sliding up to latch is the one gesture nothing on screen announces, so the way up has to be
    // visible under the finger that could make it — and gone the moment it has been made. Letting
    // go then does not end the dictation: the stop button does.
    [SkippableFact]
    public async Task SlidingUpToLatchAndThenPressingStop_PutsTheWordsInTheComposer()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        fixture.TranscriptionStatus = 200;
        fixture.Transcript = "un dictado enganchado";

        var page = await OpenAsync();
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await CentreOfAsync(page, "[data-testid=dictation-mic]");
        var lift = page.Locator("[data-testid=dictation-lift]");

        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Assertions.Expect(lift)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // It sits above the button it belongs to, which is the whole of what it says.
        var hint = await lift.BoundingBoxAsync();
        var button = await page.Locator("[data-testid=dictation-mic]").BoundingBoxAsync();
        hint.ShouldNotBeNull();
        button.ShouldNotBeNull();
        (hint.Y + hint.Height).ShouldBeLessThanOrEqualTo(button.Y + 1);

        // Past the 56 px latch threshold, upward.
        foreach (var step in Enumerable.Range(1, 5))
        {
            await TouchAsync(cdp, "touchMove", Point(mic.X, mic.Y - step * 16));
            await Task.Delay(16);
        }
        await TouchAsync(cdp, "touchEnd");

        var stop = page.Locator("[data-testid=dictation-stop]");
        await Assertions.Expect(stop)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Assertions.Expect(lift).ToBeHiddenAsync();

        await Task.Delay(HoldMs);
        await stop.ClickAsync();

        await Assertions.Expect(page.Locator("textarea.chat-input"))
            .ToHaveValueAsync("un dictado enganchado", new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });
        // The stop button finishes the dictation; it never sends.
        await Assertions.Expect(page.Locator(".chat-message.user")).ToHaveCountAsync(0);
    }

    // The two ways a dictation that recorded fine can still end in words nobody gets, one page
    // apiece being a page too many. A server that answers with a refusal has plainly been reached,
    // so flattening the two into one sentence sends whoever is holding the phone looking at the
    // network for a fault that is not there — the refusal's own words are the only thing that tells
    // them where to look.
    [SkippableFact]
    public async Task WhenADictationCannotBeCompleted_TheComposerSaysWhyRatherThanNothingHappening()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await OpenAsync();
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var refusal = page.Locator(".composer-refusal");

        // The ticket the browser could not mint, in the words the server refused it with.
        await page.EvaluateAsync(
            """
            () => {
                const ref = window.dictation._ref;
                window.__mint = ref.invokeMethodAsync.bind(ref);
                ref.invokeMethodAsync = (name, ...args) => name === 'MintTicketAsync'
                    ? Promise.reject(new Error('User not registered. Call RegisterUser first.'))
                    : window.__mint(name, ...args);
            }
            """);

        await DictateAsync(cdp, page, HoldMs);
        await Assertions.Expect(refusal)
            .ToContainTextAsync("User not registered",
                new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });

        // And the transcriber that took the recording and could not answer for it. Whisper's own
        // complaint stops at the channel server, which answers the browser a bare 502, so what is
        // asserted is the sentence the browser falls back to — named, so a refusal left over from
        // the ticket above cannot be read as this one.
        await page.EvaluateAsync("() => { window.dictation._ref.invokeMethodAsync = window.__mint; }");
        fixture.TranscriptionStatus = 500;

        await DictateAsync(cdp, page, HoldMs);
        await Assertions.Expect(refusal)
            .ToContainTextAsync("could not turn that recording into words",
                new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Assertions.Expect(page.Locator("textarea.chat-input")).ToHaveValueAsync("");

        fixture.TranscriptionStatus = 200;
    }

    // The strip takes the textarea's place rather than sitting above it, so the composer must not
    // grow when the microphone opens — everything above it would jump at the worst moment. The
    // strip and the microphone then stand side by side, so a strip that is shorter than the button
    // reads as a control that has slipped out of the row; and latched, the two ways out — throw it
    // away, or put the words in the box — are the only ways out there are, because no keyboard is
    // behind them and letting go has already happened.
    [SkippableFact]
    public async Task OnAPhoneViewport_TheStripKeepsTheComposersShapeAndBothWaysOutStayInIt()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        fixture.TranscriptionStatus = 200;
        fixture.Transcript = "hola";

        var page = await OpenAsync(width: 390, height: 844);

        var composer = page.Locator(".input-container");
        var before = await composer.BoundingBoxAsync();

        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await CentreOfAsync(page, "[data-testid=dictation-mic]");
        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        var during = await composer.BoundingBoxAsync();
        // The clock has to be readable while it is on screen, so it is asserted visible rather
        // than merely present.
        await Assertions.Expect(page.Locator("[data-testid=dictation-timer]")).ToBeVisibleAsync();

        var strip = await page.Locator("[data-testid=dictation-strip]").BoundingBoxAsync();
        var button = await page.Locator("[data-testid=dictation-mic]").BoundingBoxAsync();

        // Past the 56 px latch threshold, upward, so the strip is asked the same question with the
        // two buttons in it that it was asked with the level meter alone.
        foreach (var step in Enumerable.Range(1, 5))
        {
            await TouchAsync(cdp, "touchMove", Point(mic.X, mic.Y - step * 16));
            await Task.Delay(16);
        }
        await TouchAsync(cdp, "touchEnd");

        // Visible rather than merely present: a control pushed out of a strip that clips its
        // overflow is a control that is not there.
        await Assertions.Expect(page.Locator("[data-testid=dictation-stop]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Assertions.Expect(page.Locator("[data-testid=dictation-trash]")).ToBeVisibleAsync();

        var latchedStrip = await page.Locator("[data-testid=dictation-strip]").BoundingBoxAsync();
        var stop = await page.Locator("[data-testid=dictation-stop]").BoundingBoxAsync();

        await page.Locator("[data-testid=dictation-trash]").ClickAsync();

        before.ShouldNotBeNull();
        during.ShouldNotBeNull();
        during.Height.ShouldBe(before.Height, tolerance: 1);

        strip.ShouldNotBeNull();
        button.ShouldNotBeNull();
        strip.Height.ShouldBe(button.Height, tolerance: 1);

        latchedStrip.ShouldNotBeNull();
        stop.ShouldNotBeNull();
        (stop.X + stop.Width).ShouldBeLessThanOrEqualTo(latchedStrip.X + latchedStrip.Width + 1);
    }

    // The same row on a desktop, where the composer is wide and the strip has room to be any height
    // it likes.
    [SkippableFact]
    public async Task OnADesktopViewport_TheStripStandsAsTallAsTheMicrophoneBesideIt()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        fixture.TranscriptionStatus = 200;
        fixture.Transcript = "hola";

        var page = await OpenAsync(width: 1280, height: 900);

        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await CentreOfAsync(page, "[data-testid=dictation-mic]");
        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        var strip = await page.Locator("[data-testid=dictation-strip]").BoundingBoxAsync();
        var button = await page.Locator("[data-testid=dictation-mic]").BoundingBoxAsync();

        await TouchAsync(cdp, "touchEnd");

        strip.ShouldNotBeNull();
        button.ShouldNotBeNull();
        strip.Height.ShouldBe(button.Height, tolerance: 1);
    }
}