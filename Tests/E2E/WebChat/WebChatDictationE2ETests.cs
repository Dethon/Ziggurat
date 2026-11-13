using System.Text.Json;
using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;
using Tests.Integration.Fixtures;

namespace Tests.E2E.WebChat;

// The microphone itself: holding it, tapping it, and what a held dictation traces. The cap, the
// screen held awake and the path it opens on are WebChatDictationLongE2ETests'.
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
        // Past the opening transient, the measured window, and the closing one.
        await HoldUntilRecordedAsync(page,
            FakeMicrophoneAudio.TranscriptionRate / 5 + 8192 + FakeMicrophoneAudio.TranscriptionRate / 5, SpectrumHoldMs);
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

        IPage? tapped = null;

        // The press has to outlast the run starting — _onUp returns without deciding anything when
        // there is no run yet — and end well inside the 400 ms mis-tap floor, which dictation.js
        // measures on the page's own performance.now(). Waiting for the run makes the press as short
        // as this gesture can be and still be one, but the release is still a round trip, and on a
        // machine running a second suite a stalled one carried the press past the floor, where the
        // app is right to treat the gesture as the hold it had become. So the tap is the best of a
        // few: a page that never shows the hint on any of them has a tap that does not work.
        var aTapLanded = await LatencyBudget.HoldsOnceAsync(async () =>
        {
            var page = await OpenAsync();
            var cdp = await page.Context.NewCDPSessionAsync(page);
            var mic = await PressableMicAsync(page);

            await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
            await page.WaitForFunctionAsync(
                "() => window.dictation && window.dictation._run",
                null,
                new PageWaitForFunctionOptions { Timeout = 15_000 });
            await TouchAsync(cdp, "touchEnd");

            // Either the tap's hint or the hold's ending — a transcript, or a refusal when the hold
            // was too short to have heard anything: whichever the page decided.
            await page.WaitForFunctionAsync(
                "() => document.querySelector('.composer-hint') !== null" +
                " || document.querySelector('.composer-refusal') !== null" +
                " || document.querySelector('textarea.chat-input')?.value !== ''",
                null,
                new PageWaitForFunctionOptions { Timeout = 15_000 });
            if (!await page.Locator(".composer-hint").IsVisibleAsync())
            {
                return false;
            }

            tapped = page;
            return true;
        });

        aTapLanded.ShouldBeTrue("no tap showed the hold hint; every press was read as a hold");
        var page = tapped!;
        await Task.Delay(1_500);
        await Assertions.Expect(page.Locator("textarea.chat-input")).ToHaveValueAsync("");
        await Assertions.Expect(page.Locator(".composer-refusal")).ToBeHiddenAsync();
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

}