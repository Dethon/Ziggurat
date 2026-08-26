using System.Text.Json;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// The microphone itself: holding it, the cap that stops it, and the two ways a finger can get
// ahead of a graph that has not opened yet.
[Collection(WebChatE2ECollections.Dictation)]
[Trait("Category", "E2E")]
public sealed class WebChatDictationE2ETests(WebChatE2EFixture fixture)
    : DictationE2EBase(fixture)
{
    // The whole of a good dictation: a held microphone, a finger that drifts a little while it is
    // held, words in the composer, and a recording whose bytes are what whisper can actually read.
    [SkippableFact]
    public async Task HoldingTheMicrophoneAndLettingGo_PutsTheWordsInTheComposerToSend()
    {
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");
        Fixture.TranscriptionStatus = 200;
        Fixture.Transcript = "hola desde el micrófono";

        var page = await OpenAsync();
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await PressableMicAsync(page);

        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // A finger that drifts a little is still a finger holding the button down: only distance
        // past a threshold means anything, and this is well inside every one of them.
        foreach (var step in Enumerable.Range(1, 4))
        {
            await TouchAsync(cdp, "touchMove", Point(mic.X - step * 5, mic.Y - step * 3));
            await Task.Delay(60);
        }
        await Task.Delay(SpectrumHoldMs);
        await TouchAsync(cdp, "touchEnd");

        // The words land in the composer, not in a message: the person is always the one who
        // presses send.
        var composer = page.Locator("textarea.chat-input");
        await Assertions.Expect(composer)
            .ToHaveValueAsync("hola desde el micrófono", new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });
        await Assertions.Expect(page.Locator(".chat-message.user")).ToHaveCountAsync(0);

        // One control in that spot, always the one the person is about to use: with something to
        // send, the microphone is off screen rather than standing beside Send.
        await Assertions.Expect(page.Locator("[data-testid=composer-send]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Assertions.Expect(page.Locator("[data-testid=dictation-mic]")).ToBeHiddenAsync();

        await composer.PressAsync("Enter");
        await Assertions.Expect(page.Locator(".chat-message.user").First)
            .ToContainTextAsync("hola desde el micrófono", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });

        // What whisper is actually fed. MediaRecorder's Opus is what a browser reaches for by
        // default and what lemonade answers 400 to, so the format is the feature, not a detail.
        var wav = Fixture.LastAudio.ShouldNotBeNull();
        System.Text.Encoding.ASCII.GetString(wav[..4]).ShouldBe("RIFF");
        System.Text.Encoding.ASCII.GetString(wav[8..12]).ShouldBe("WAVE");
        BitConverter.ToInt16(wav, 22).ShouldBe((short)1);      // mono
        BitConverter.ToInt32(wav, 24).ShouldBe(16_000);
        BitConverter.ToInt16(wav, 34).ShouldBe((short)16);     // s16le
        // A recording of nothing has a header and no samples, which is what a graph that never
        // pulled the worklet produces.
        BitConverter.ToInt32(wav, 40).ShouldBeGreaterThan(0);

        // Dropping to 16 kHz is the step that can quietly ruin a recording: everything above 8 kHz
        // in the captured signal folds back down on top of the speech unless it is filtered away
        // first. The fake microphone plays a 1 kHz tone that must survive and a 12 kHz tone that
        // must not reappear at 4 kHz.
        var samples = FakeMicrophoneAudio.Samples(wav);
        // Past the microphone opening and short of it closing, so neither end's transient is
        // measured as content.
        var window = samples.Skip(FakeMicrophoneAudio.TranscriptionRate / 5).Take(8192).ToArray();
        window.Length.ShouldBe(8192, "the recording is too short to measure");

        var speech = FakeMicrophoneAudio.MagnitudeAt(
            window, FakeMicrophoneAudio.SpeechToneHz, FakeMicrophoneAudio.TranscriptionRate);
        var alias = FakeMicrophoneAudio.MagnitudeAt(
            window, FakeMicrophoneAudio.AliasHz, FakeMicrophoneAudio.TranscriptionRate);

        // Neither silence nor a wall of clipping: a recording that is either is one no transcriber
        // can do anything with, however clean its spectrum.
        var rms = Math.Sqrt(window.Sum(s => s * s) / window.Length);
        rms.ShouldBeGreaterThan(0.02);
        rms.ShouldBeLessThan(0.71);

        speech.ShouldBeGreaterThan(0.01, "the 1 kHz tone did not survive the recording");
        var decibels = 20 * Math.Log10(alias / speech);
        decibels.ShouldBeLessThan(-20, $"12 kHz folded back to 4 kHz at {decibels:F1} dB");
    }

    // A mis-tap must cost nothing: no recording, no request, and a short hint saying what to do
    // instead — not a refusal, because nothing went wrong.
    [SkippableFact]
    public async Task TappingTheMicrophone_RecordsNothingAndSaysToHoldIt()
    {
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");
        Fixture.TranscriptionStatus = 200;
        Fixture.Transcript = "no debería existir";

        var page = await OpenAsync();
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await PressableMicAsync(page);

        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));

        // The press has to outlast the run starting — _onUp returns without deciding anything when
        // there is no run yet — and end well inside the 400 ms mis-tap floor, which dictation.js
        // measures on the page's own performance.now(). Eighty milliseconds of sleep guaranteed
        // neither: it was longer than starting takes, and a stalled round trip carried it past the
        // floor, where the app is right to treat the gesture as the hold it had become. Waiting for
        // the run makes the press as short as this gesture can be and still be one.
        await page.WaitForFunctionAsync(
            "() => window.dictation && window.dictation._run",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });
        await TouchAsync(cdp, "touchEnd");

        await Assertions.Expect(page.Locator(".composer-hint"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Task.Delay(1_500);
        await Assertions.Expect(page.Locator("textarea.chat-input")).ToHaveValueAsync("");
        await Assertions.Expect(page.Locator(".composer-refusal")).ToBeHiddenAsync();
    }

    // A pocketed phone must not record indefinitely. The cap is the server's number, learned
    // through the same limits call the attachment rules arrive on — the client carries none.
    [SkippableFact]
    public async Task ADictationThatRunsPastTheCap_StopsItselfAndTranscribesWhatItHas()
    {
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");
        Fixture.TranscriptionStatus = 200;
        Fixture.Transcript = "se paró solo";

        var page = await OpenAsync();
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await PressableMicAsync(page);

        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // Never released: the words arrive because the recording ended itself.
        await Assertions.Expect(page.Locator("textarea.chat-input"))
            .ToHaveValueAsync(
                "se paró solo",
                new LocatorAssertionsToHaveValueOptions
                {
                    Timeout = (float)Fixture.RecordingCap.TotalMilliseconds + 30_000
                });

        await TouchAsync(cdp, "touchEnd");
    }

    // The trace is the only witness on a phone, and this is what it must be able to say for the
    // failure that is actually reported from there: a capture that starts, goes quiet mid-run, and
    // leaves nothing. Three lines carry that diagnosis. The script line says which build spoke, so
    // "still broken" after a deploy is distinguishable from "the fix never arrived". The track lines
    // timestamp the platform taking the microphone away mid-run, which is the one event a phone
    // fires and a desktop never does. And the recorded line carries the peak and when sound was
    // last heard, because a count of samples cannot tell zeros from speech — a silent capture and a
    // working one both fill batches at the same rate.
    [SkippableFact]
    public async Task AHeldDictation_TracesTheScriptTheTracksEventsAndWhatWasHeard()
    {
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");
        Fixture.TranscriptionStatus = 200;
        Fixture.Transcript = "quedó registrado";

        var page = await OpenAsync();

        // The stamp is a round trip of its own, made once at registration; registered alone does
        // not mean it has landed yet.
        await page.WaitForFunctionAsync(
            "() => window.dictation.diagnostics().includes('script:')",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await PressableMicAsync(page);

        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // What Android does mid-run when the platform takes the capture away, played onto the live
        // track. A dispatched event does not flip track.muted, but the listener it reaches is the
        // one the real event reaches, and the listener is what is under test.
        await page.EvaluateAsync(
            """
            () => {
                const track = window.dictation._run.stream.getAudioTracks()[0];
                track.dispatchEvent(new Event('mute'));
                track.dispatchEvent(new Event('unmute'));
            }
            """);

        await Task.Delay(HoldMs);
        await TouchAsync(cdp, "touchEnd");

        await Assertions.Expect(page.Locator("textarea.chat-input"))
            .ToHaveValueAsync("quedó registrado", new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });

        // The context closes after the upload, and its own statechange handler is what writes the
        // line — the same handler that names a phone suspending the graph mid-run.
        await page.WaitForFunctionAsync(
            "() => window.dictation.diagnostics().includes('context: closed')",
            null,
            new PageWaitForFunctionOptions { Timeout = 15_000 });

        var trace = await page.EvaluateAsync<string>("() => window.dictation.diagnostics()");
        trace.ShouldContain("track: mute");
        trace.ShouldContain("track: unmute");
        trace.ShouldMatch(@"peak -?\d+ dB");
        trace.ShouldMatch(@"last sound at \d+ms");
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
        await Assertions.Expect(page.Locator("textarea.chat-input"))
            .ToHaveValueAsync("una sola ruta", new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });

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