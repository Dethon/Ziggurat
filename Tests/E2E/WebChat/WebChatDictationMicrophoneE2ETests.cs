using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// The four ways the microphone itself can go wrong: a finger that latches or lets go before the
// graph is up, a graph that never comes up at all, and a microphone that opens but only ever hands
// back silence.
//
// Split out of WebChatDictationE2ETests rather than living beside it, because a collection is what
// xUnit serialises and that class was the run's critical path: ten cases end to end, forty seconds,
// finishing after everything else in the suite had gone quiet. These four are the slow half — each
// one waits out a microphone that is deliberately not ready — and on a slice of their own they run
// beside the six that remain instead of behind them. The fixture gives every collection its own
// space, user block and whisper transcript, so the two dictate at once without crossing answers.
[Collection(WebChatE2ECollections.DictationMicrophone)]
[Trait("Category", "E2E")]
public sealed class WebChatDictationMicrophoneE2ETests(WebChatE2EFixture fixture)
    : DictationE2EBase(fixture)
{
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
        // itself used to arrive and take the latch back. Waited for as the state it is — the run is
        // holding audio, so the open, the graph and the worklet are all behind it — rather than as a
        // span. A span had to cover the patched open plus everything the graph does after it, and on
        // a loaded machine it did not: the stop landed while getUserMedia was still pending, the
        // recording held nothing, and the dictation ended as "the microphone had not finished
        // opening" with an empty composer to show for it.
        await page.WaitForFunctionAsync(
            "() => window.dictation._run && window.dictation._run.samples > 0",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
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

    // What a wedged phone actually does, per two traces taken in the state: grants a live-looking
    // track, renders the whole run, and delivers digital zeros — no mute, no ended, no state
    // change, nothing above -78 dBFS in seconds of held microphone. Uploading that asks whisper to
    // account for silence and comes back blaming the transcription, so the person retries into the
    // same wall. A recording the app itself measured as dead is refused on the spot, with words
    // that name the phone's audio being stuck rather than the words nobody said.
    [SkippableFact]
    public async Task AMicrophoneThatGivesOnlySilence_IsRefusedAsStuckRatherThanUploaded()
    {
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");
        Fixture.TranscriptionStatus = 200;
        // Distinctive: if this reaches the composer, the silence was uploaded and answered.
        Fixture.Transcript = "esto probaría que se subió el silencio";

        var page = await OpenAsync();
        // The wedge, reproduced: a real audio track that carries only zeros — a destination node
        // nothing feeds, on a context resumed inside the gesture so it genuinely renders.
        await page.EvaluateAsync(
            """
            () => {
                navigator.mediaDevices.getUserMedia = async () => {
                    const ctx = new AudioContext();
                    await ctx.resume();
                    return ctx.createMediaStreamDestination().stream;
                };
            }
            """);

        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await PressableMicAsync(page);

        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Task.Delay(HoldMs);
        await TouchAsync(cdp, "touchEnd");

        var refusal = page.Locator(".composer-refusal");
        await Assertions.Expect(refusal)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        (await refusal.InnerTextAsync()).ShouldContain("silence");

        // Long enough for an upload that should not exist to have been answered.
        await Task.Delay(1_500);
        await Assertions.Expect(page.Locator("textarea.chat-input")).ToHaveValueAsync("");

        var trace = await page.EvaluateAsync<string>("() => window.dictation.diagnostics()");
        trace.ShouldContain("all zeros");
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
        // Past the mis-tap floor, and past the grant: the premise is a microphone that was handed
        // over and a graph that fell over behind it, so a release that beats getUserMedia is the
        // other case entirely — the press is answered by "the microphone had not finished opening"
        // and this case never gets near the leak it is here for.
        await Task.Delay(HoldMs);
        await page.WaitForFunctionAsync(
            "() => window.__streams.length > 0",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
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