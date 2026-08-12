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

    // Opening the microphone takes as long as the device takes, and a finger that has already
    // decided does not wait for it. The latch is made against the recording that exists from the
    // moment of the press, so what the browser reports when the graph finally comes up has to be
    // where the gesture got to by then — not where it was when the finger landed. Reported as the
    // press, it arrived behind the latch and undid it: the strip stayed, neither way out appeared,
    // and the pointer that could have ended the recording had already been let go.
    [SkippableFact]
    public async Task LatchingWhileTheMicrophoneIsStillOpening_LeavesTheDictationLatched()
    {
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");
        Fixture.TranscriptionStatus = 200;
        Fixture.Transcript = "enganchado antes de tiempo";

        var page = await OpenAsync();
        // A phone's microphone does not open in a frame. This is the same wait, made long enough
        // for a gesture to be completed inside it rather than left to the machine's mood — and
        // short enough that the whole case still finishes well inside the recording cap.
        await page.EvaluateAsync(
            """
            () => {
                const open = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
                navigator.mediaDevices.getUserMedia = constraints =>
                    new Promise(resolve => setTimeout(() => resolve(open(constraints)), 900));
            }
            """);

        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await PressableMicAsync(page);

        // No waiting for the strip: the whole point is that the gesture finishes first.
        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        foreach (var step in Enumerable.Range(1, 5))
        {
            await TouchAsync(cdp, "touchMove", Point(mic.X, mic.Y - step * 16));
            await Task.Delay(16);
        }
        await TouchAsync(cdp, "touchEnd");

        var stop = page.Locator("[data-testid=dictation-stop]");
        await Assertions.Expect(stop)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // Past the moment the microphone finishes opening, which is when the press's own account of
        // itself used to arrive and take the latch back.
        await Task.Delay(1_400);
        await Assertions.Expect(stop)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 1_000 });
        await Assertions.Expect(page.Locator("[data-testid=dictation-trash]")).ToBeVisibleAsync();

        // And it is a working dictation, not merely a strip with the right buttons on it.
        await stop.ClickAsync();
        await Assertions.Expect(page.Locator("textarea.chat-input"))
            .ToHaveValueAsync(
                "enganchado antes de tiempo", new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });
    }

    // The other half of a microphone that opens slowly. A finger can also come back UP inside that
    // wait — a deliberate hold, past the mis-tap floor, released before the graph ever existed. The
    // recording that ends is empty, and sending it asks whisper to account for audio that was never
    // captured: the person is told their words could not be made out, about words nothing ever
    // heard. Nothing goes up, and what is said names the microphone.
    [SkippableFact]
    public async Task ReleasingBeforeTheMicrophoneOpens_SaysSoRatherThanSendingAnEmptyRecording()
    {
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");
        Fixture.TranscriptionStatus = 200;
        // Distinctive: if this reaches the composer, an empty recording was uploaded and answered.
        Fixture.Transcript = "esto probaría que se subió algo";

        var page = await OpenAsync();
        // Longer than the hold below, so the release lands with certainty inside the wait rather
        // than at the mercy of how quickly the machine opens a fake device.
        await page.EvaluateAsync(
            """
            () => {
                const open = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
                navigator.mediaDevices.getUserMedia = constraints =>
                    new Promise(resolve => setTimeout(() => resolve(open(constraints)), 2500));
            }
            """);

        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await PressableMicAsync(page);

        // Comfortably past the 400 ms mis-tap floor: this is a hold, not a tap, so the answer must
        // not be the hint that tells someone to hold it.
        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Task.Delay(HoldMs);
        await TouchAsync(cdp, "touchEnd");

        var refusal = page.Locator(".composer-refusal");
        await Assertions.Expect(refusal)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        (await refusal.InnerTextAsync()).ShouldContain("microphone");

        // Long past the moment the microphone finishes opening, so a late arrival cannot rescue it.
        await Task.Delay(2_500);
        await Assertions.Expect(page.Locator("textarea.chat-input")).ToHaveValueAsync("");
        await Assertions.Expect(page.Locator(".composer-hint")).ToBeHiddenAsync();
    }

    // The microphone can be granted and the graph still fail to come up behind it — the worklet is
    // fetched over the network, and a phone loses one whenever it feels like it. Everything the open
    // got as far as acquiring is live at that moment: a real capture the person can see in the
    // status bar, and a context holding an output stream of its own because the chain ends at the
    // destination. The failure path is the only thing left holding either, and it drops the run on
    // the floor: nothing else ever closes them, and the next press acquires another pair.
    //
    // Whether that is what wedges a phone is not settled — but a recording that failed must not
    // leave the microphone open behind it either way, and this is the one leak we can see from here.
    [SkippableFact]
    public async Task AGraphThatFailsToComeUp_ClosesTheMicrophoneItHadAlreadyOpened()
    {
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");

        var page = await OpenAsync();

        // Every microphone the page is granted, and every context it builds, kept where the test can
        // ask about them afterwards — the run object that held them is unreachable by then.
        await page.EvaluateAsync(
            """
            () => {
                window.__streams = [];
                window.__contexts = [];
                const open = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
                navigator.mediaDevices.getUserMedia = async constraints => {
                    const stream = await open(constraints);
                    window.__streams.push(stream);
                    return stream;
                };
                const Ctx = window.AudioContext;
                window.AudioContext = function (...args) {
                    const ctx = new Ctx(...args);
                    window.__contexts.push(ctx);
                    return ctx;
                };
                // The worklet, and only the worklet: the microphone is granted exactly as it would
                // be on the phone, and the graph falls over one step later.
                AudioWorklet.prototype.addModule = () => Promise.reject(new Error('no worklet today'));
            }
            """);

        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await PressableMicAsync(page);
        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Task.Delay(HoldMs);
        await TouchAsync(cdp, "touchEnd");

        // The refusal is what says the failure path has run to the end; asserting the microphone
        // before it would pass on a graph that simply had not finished failing yet.
        await Assertions.Expect(page.Locator(".composer-refusal"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        var opened = await page.EvaluateAsync<string[]>(
            "() => window.__streams.flatMap(s => s.getTracks().map(t => t.readyState))");
        opened.ShouldNotBeEmpty("the microphone was never granted, so the leak is not what was tested");
        opened.ShouldAllBe(state => state == "ended");

        var contexts = await page.EvaluateAsync<string[]>("() => window.__contexts.map(c => c.state)");
        contexts.ShouldNotBeEmpty("no context was built, so the leak is not what was tested");
        contexts.ShouldAllBe(state => state == "closed");
    }
}