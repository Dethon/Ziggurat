using System.Text.Json;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// The dictation control at rest and as it opens: the meter, the strip's shape on a phone and a desktop,
// the path the microphone is opened on, and the keyboard's latch. Split from
// WebChatDictationComposerE2ETests by length, into a collection and a space of their own: the two
// halves read nothing of each other's, and as one chain they were among the run's longest.
[Collection(WebChatE2ECollections.DictationStrip)]
[Trait("Category", "E2E")]
public sealed class WebChatDictationStripE2ETests(WebChatE2EFixture fixture) : DictationE2EBase(fixture)
{
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
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");

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

    // The strip takes the textarea's place rather than sitting above it, so the composer must not
    // grow when the microphone opens — everything above it would jump at the worst moment. The
    // strip and the microphone then stand side by side, so a strip that is shorter than the button
    // reads as a control that has slipped out of the row; and latched, the two ways out — throw it
    // away, or put the words in the box — are the only ways out there are, because no keyboard is
    // behind them and letting go has already happened.
    [SkippableFact]
    public async Task OnAPhoneViewport_TheStripKeepsTheComposersShapeAndBothWaysOutStayInIt()
    {
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");
        Fixture.TranscriptionStatus = 200;
        Fixture.Transcript = "hola";

        var page = await OpenAsync(width: 390, height: 844);

        var composer = page.Locator(".input-container");
        var before = await composer.BoundingBoxAsync();

        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await PressableMicAsync(page);
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
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");
        Fixture.TranscriptionStatus = 200;
        Fixture.Transcript = "hola";

        var page = await OpenAsync(width: 1280, height: 900);

        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await PressableMicAsync(page);
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

    // The processed path, pinned as applied settings rather than as the request. Echo
    // cancellation is deliberately ON: the platform's raw capture path is the one the phone
    // wedges — born-dead grants, zeros on a healthy graph, cleared only by reboot — while the
    // voice-call path recorded through the very same wedge, confirmed live on the phone that
    // suffers it. Fidelity for the transcriber was the old theory; the wedge was the measured
    // result, and a dictation that always works wins.
    //
    // Automatic gain was learned the hard way in its own right: with it off, an Android
    // phone held in the hand and spoken to normally delivered a recording whose loudest peak was
    // under a tenth of full scale, some 20 dB below speech. It is the one part of that chain whose
    // job is the level rather than the shape of the sound, so it stays on.
    //
    // And the graph must still arrive at the context's destination. Chromium renders one that
    // reaches no output; Android does not, and every node in it — the worklet and the level meter
    // both — then goes unpulled and hears silence. Nothing below this can catch that on a desktop,
    // which is exactly why the shape of the graph is asserted rather than only its results.
    [SkippableFact]
    public async Task TheMicrophoneIsOpenedOnThePathThatSurvivesTheWedge()
    {
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");
        Fixture.TranscriptionStatus = 200;
        Fixture.Transcript = "hola";

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
        var mic = await PressableMicAsync(page);

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
        // Echo cancellation selects the platform path that survives the wedge; noise suppression
        // merely rode along on it, and live it turned out to be a gate — opening and closing on
        // the speaker mid-sentence until the transcription came back nonsense. So the path stays
        // and the gate goes.
        opened.GetProperty("echoCancellation").GetBoolean().ShouldBeTrue();
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

    // Nobody should have to hold a key down, so a keyboard press starts a latched dictation
    // outright — and Escape is how it is abandoned without reaching for the trash button. The two
    // buttons it puts on screen are drawn for the same reason the microphone is, and the one that
    // ends the dictation reads as sending the words on rather than as halting a machine.
    [SkippableFact]
    public async Task PressingEnterOnTheMicrophone_LatchesAndEscapeThrowsItAway()
    {
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");
        Fixture.TranscriptionStatus = 200;
        Fixture.Transcript = "esto se descarta";

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
}