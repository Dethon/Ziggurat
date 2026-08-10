using Microsoft.Playwright;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.E2E.WebChat;

// A real dictation, end to end: Chromium's fake microphone, the real encoder, the real ticket, the
// real upload through Caddy to the channel server, and a whisper the fixture answers for. The
// gestures go over CDP touch (Input.dispatchTouchEvent) rather than synthetic PointerEvents —
// untrusted events call the handlers but never enter the input pipeline, which is how four hearth
// fixes once went green while the phone kept failing.
[Collection("WebChatE2E")]
[Trait("Category", "E2E")]
public sealed class WebChatDictationE2ETests(WebChatE2EFixture fixture)
{
    // Comfortably past the 400 ms mis-tap floor and nowhere near the two-minute cap.
    private const int HoldMs = 900;

    [SkippableFact]
    public async Task HoldingTheMicrophoneAndLettingGo_PutsTheWordsInTheComposerToSend()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        fixture.TranscriptionStatus = 200;
        fixture.Transcript = "hola desde el micrófono";

        var page = await OpenAsync();
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await CentreOfAsync(page, "[data-testid=dictation-mic]");

        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Task.Delay(HoldMs);
        await TouchAsync(cdp, "touchEnd");

        // The words land in the composer, not in a message: the person is always the one who
        // presses send.
        var composer = page.Locator("textarea.chat-input");
        await Assertions.Expect(composer)
            .ToHaveValueAsync("hola desde el micrófono", new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });
        await Assertions.Expect(page.Locator(".chat-message.user")).ToHaveCountAsync(0);

        await composer.PressAsync("Enter");
        await Assertions.Expect(page.Locator(".chat-message.user").First)
            .ToContainTextAsync("hola desde el micrófono", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });

        // What whisper is actually fed. MediaRecorder's Opus is what a browser reaches for by
        // default and what lemonade answers 400 to, so the format is the feature, not a detail.
        var wav = fixture.LastAudio.ShouldNotBeNull();
        System.Text.Encoding.ASCII.GetString(wav[..4]).ShouldBe("RIFF");
        System.Text.Encoding.ASCII.GetString(wav[8..12]).ShouldBe("WAVE");
        BitConverter.ToInt16(wav, 22).ShouldBe((short)1);      // mono
        BitConverter.ToInt32(wav, 24).ShouldBe(16_000);
        BitConverter.ToInt16(wav, 34).ShouldBe((short)16);     // s16le
        // A recording of nothing has a header and no samples, which is what a graph that never
        // pulled the worklet produces.
        BitConverter.ToInt32(wav, 40).ShouldBeGreaterThan(0);
    }

    // Nobody should have to hold a key down, so a keyboard press starts a latched dictation
    // outright — and Escape is how it is abandoned without reaching for the trash button.
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
        await Assertions.Expect(page.Locator("[data-testid=dictation-stop]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        await page.Keyboard.PressAsync("Escape");

        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeHiddenAsync(new LocatorAssertionsToBeHiddenOptions { Timeout = 15_000 });
        await Task.Delay(1_500);
        await Assertions.Expect(page.Locator("textarea.chat-input")).ToHaveValueAsync("");
    }

    // A mis-tap must cost nothing: no recording, no request, and a short hint saying what to do
    // instead — not a refusal, because nothing went wrong.
    [SkippableFact]
    public async Task TappingTheMicrophone_RecordsNothingAndSaysToHoldIt()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        fixture.TranscriptionStatus = 200;
        fixture.Transcript = "no debería existir";

        var page = await OpenAsync();
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await CentreOfAsync(page, "[data-testid=dictation-mic]");

        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Task.Delay(80);
        await TouchAsync(cdp, "touchEnd");

        await Assertions.Expect(page.Locator(".composer-hint"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Task.Delay(1_500);
        await Assertions.Expect(page.Locator("textarea.chat-input")).ToHaveValueAsync("");
        await Assertions.Expect(page.Locator(".composer-refusal")).ToBeHiddenAsync();
    }

    // A finger that drifts a little is still a finger holding the button down: only distance past
    // a threshold means anything, and this is well inside every one of them.
    [SkippableFact]
    public async Task APressThatDrifts_IsStillAHoldAndStillProducesWords()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        fixture.TranscriptionStatus = 200;
        fixture.Transcript = "un dedo que se mueve un poco";

        var page = await OpenAsync();
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await CentreOfAsync(page, "[data-testid=dictation-mic]");

        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        foreach (var step in Enumerable.Range(1, 4))
        {
            await TouchAsync(cdp, "touchMove", Point(mic.X - step * 5, mic.Y - step * 3));
            await Task.Delay(60);
        }
        await Task.Delay(HoldMs);
        await TouchAsync(cdp, "touchEnd");

        await Assertions.Expect(page.Locator("textarea.chat-input"))
            .ToHaveValueAsync(
                "un dedo que se mueve un poco", new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });
    }

    // A pocketed phone must not record indefinitely. The cap is the server's number, learned
    // through the same limits call the attachment rules arrive on — the client carries none.
    [SkippableFact]
    public async Task ADictationThatRunsPastTheCap_StopsItselfAndTranscribesWhatItHas()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        fixture.TranscriptionStatus = 200;
        fixture.Transcript = "se paró solo";

        var page = await OpenAsync();
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await CentreOfAsync(page, "[data-testid=dictation-mic]");

        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // Never released: the words arrive because the recording ended itself.
        await Assertions.Expect(page.Locator("textarea.chat-input"))
            .ToHaveValueAsync(
                "se paró solo",
                new LocatorAssertionsToHaveValueOptions
                {
                    Timeout = (float)fixture.RecordingCap.TotalMilliseconds + 30_000
                });

        await TouchAsync(cdp, "touchEnd");
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
        // Nothing arrives late either: a discarded recording makes no request at all.
        await Task.Delay(2_000);
        await Assertions.Expect(page.Locator("textarea.chat-input")).ToHaveValueAsync("");
    }

    [SkippableFact]
    public async Task SlidingUpToLatchAndThenPressingStop_PutsTheWordsInTheComposer()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        fixture.TranscriptionStatus = 200;
        fixture.Transcript = "un dictado enganchado";

        var page = await OpenAsync();
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await CentreOfAsync(page, "[data-testid=dictation-mic]");

        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // Past the 56 px latch threshold, upward.
        foreach (var step in Enumerable.Range(1, 5))
        {
            await TouchAsync(cdp, "touchMove", Point(mic.X, mic.Y - step * 16));
            await Task.Delay(16);
        }
        await TouchAsync(cdp, "touchEnd");

        // Letting go does not end a latched dictation: the stop button does.
        var stop = page.Locator("[data-testid=dictation-stop]");
        await Assertions.Expect(stop)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Task.Delay(HoldMs);
        await stop.ClickAsync();

        await Assertions.Expect(page.Locator("textarea.chat-input"))
            .ToHaveValueAsync("un dictado enganchado", new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });
        // The stop button finishes the dictation; it never sends.
        await Assertions.Expect(page.Locator(".chat-message.user")).ToHaveCountAsync(0);
    }

    [SkippableFact]
    public async Task WhenTheTranscriberFails_TheComposerSaysSoRatherThanNothingHappening()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        fixture.TranscriptionStatus = 500;

        var page = await OpenAsync();
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await CentreOfAsync(page, "[data-testid=dictation-mic]");

        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Task.Delay(HoldMs);
        await TouchAsync(cdp, "touchEnd");

        await Assertions.Expect(page.Locator(".composer-refusal"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        await Assertions.Expect(page.Locator("textarea.chat-input")).ToHaveValueAsync("");

        fixture.TranscriptionStatus = 200;
    }

    // The strip takes the textarea's place rather than sitting above it, so the composer must not
    // grow when the microphone opens — everything above it would jump at the worst moment.
    [SkippableFact]
    public async Task OnAPhoneViewport_TheRecordingStripLeavesTheComposersHeightAlone()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        fixture.TranscriptionStatus = 200;
        fixture.Transcript = "hola";

        var page = await fixture.CreatePageAsync(hasTouch: true);
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());
        await Assertions.Expect(page.Locator("[data-testid=dictation-mic]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

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

        await TouchAsync(cdp, "touchEnd");

        before.ShouldNotBeNull();
        during.ShouldNotBeNull();
        during.Height.ShouldBe(before.Height, tolerance: 1);
    }

    // The strip and the microphone stand side by side while recording, so a strip that is shorter
    // than the button reads as a control that has slipped out of the row.
    [SkippableTheory]
    [InlineData(390, 844)]
    [InlineData(1280, 900)]
    public async Task WhileRecording_TheStripStandsAsTallAsTheMicrophoneBesideIt(int width, int height)
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        fixture.TranscriptionStatus = 200;
        fixture.Transcript = "hola";

        var page = await fixture.CreatePageAsync(hasTouch: true);
        await page.SetViewportSizeAsync(width, height);
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());
        await Assertions.Expect(page.Locator("[data-testid=dictation-mic]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

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

    // Sliding up to latch is the one gesture nothing on screen announces, so it has to be visible
    // under the finger that could make it — and gone the moment it has been made.
    [SkippableFact]
    public async Task HoldingTheMicrophone_ShowsTheWayUpToLatchUntilItIsLatched()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        fixture.TranscriptionStatus = 200;
        fixture.Transcript = "hola";

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

        foreach (var step in Enumerable.Range(1, 5))
        {
            await TouchAsync(cdp, "touchMove", Point(mic.X, mic.Y - step * 16));
            await Task.Delay(16);
        }
        await TouchAsync(cdp, "touchEnd");

        await Assertions.Expect(page.Locator("[data-testid=dictation-stop]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Assertions.Expect(lift).ToBeHiddenAsync();

        await page.Locator("[data-testid=dictation-trash]").ClickAsync();
    }

    // A server that answers with a refusal has plainly been reached. Flattening the two into one
    // sentence sends whoever is holding the phone looking at the network for a fault that is not
    // there — the refusal's own words are the only thing that tells them where to look.
    [SkippableFact]
    public async Task WhenTheServerRefusesTheTicket_TheComposerSaysWhatItRefusedWith()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await OpenAsync();
        await page.EvaluateAsync(
            """
            () => {
                const ref = window.dictation._ref;
                const original = ref.invokeMethodAsync.bind(ref);
                ref.invokeMethodAsync = (name, ...args) => name === 'MintTicketAsync'
                    ? Promise.reject(new Error('User not registered. Call RegisterUser first.'))
                    : original(name, ...args);
            }
            """);

        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await CentreOfAsync(page, "[data-testid=dictation-mic]");
        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Task.Delay(HoldMs);
        await TouchAsync(cdp, "touchEnd");

        await Assertions.Expect(page.Locator(".composer-refusal"))
            .ToContainTextAsync("User not registered",
                new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
    }

    // One control in that spot, always the one the person is about to use: with something to send,
    // the microphone is off screen rather than standing beside Send.
    [SkippableFact]
    public async Task WithSomethingToSend_TheMicrophoneIsNotOnScreen()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await OpenAsync();
        await page.Locator("textarea.chat-input").FillAsync("unas palabras escritas");

        await Assertions.Expect(page.Locator("button.btn-primary", new PageLocatorOptions { HasText = "Send" }))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Assertions.Expect(page.Locator("[data-testid=dictation-mic]")).ToBeHiddenAsync();
    }

    // Latched on a phone, the two ways out — throw it away, or put the words in the box — are the
    // only ways out there are: no keyboard is behind them and letting go has already happened.
    [SkippableFact]
    public async Task LatchedOnAPhone_TheStripStillOffersBothWaysOut()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        fixture.TranscriptionStatus = 200;
        fixture.Transcript = "hola";

        var page = await fixture.CreatePageAsync(hasTouch: true);
        await page.SetViewportSizeAsync(390, 844);
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());
        await Assertions.Expect(page.Locator("[data-testid=dictation-mic]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });

        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await CentreOfAsync(page, "[data-testid=dictation-mic]");
        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

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

        var strip = await page.Locator("[data-testid=dictation-strip]").BoundingBoxAsync();
        var stop = await page.Locator("[data-testid=dictation-stop]").BoundingBoxAsync();
        strip.ShouldNotBeNull();
        stop.ShouldNotBeNull();
        (stop.X + stop.Width).ShouldBeLessThanOrEqualTo(strip.X + strip.Width + 1);

        await page.Locator("[data-testid=dictation-trash]").ClickAsync();
    }

    // The two buttons in the strip are drawn for the same reason the microphone is, and the one
    // that ends the dictation reads as sending the words on rather than as halting a machine.
    [SkippableFact]
    public async Task TheLatchedControls_AreDrawnIconsRatherThanEmoji()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");
        fixture.TranscriptionStatus = 200;
        fixture.Transcript = "hola";

        var page = await OpenAsync();
        await page.Locator("[data-testid=dictation-mic]").PressAsync("Enter");

        var trash = page.Locator("[data-testid=dictation-trash]");
        var stop = page.Locator("[data-testid=dictation-stop]");
        await Assertions.Expect(trash)
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        await Assertions.Expect(trash.Locator("svg")).ToBeVisibleAsync();
        await Assertions.Expect(stop.Locator("svg")).ToBeVisibleAsync();
        (await trash.InnerTextAsync()).ShouldNotContain("🗑");
        (await stop.InnerTextAsync()).ShouldNotContain("■");

        await trash.ClickAsync();
    }

    // A drawn microphone rather than an emoji: the platform's font decides what an emoji looks
    // like, and on some of them it is neither the same shape nor the same colour as the icons
    // beside it.
    [SkippableFact]
    public async Task TheMicrophoneControl_IsADrawnIconRatherThanAnEmoji()
    {
        Skip.If(string.IsNullOrEmpty(fixture.WebChatUrl), "WebChat stack not available");

        var page = await OpenAsync();
        var mic = page.Locator("[data-testid=dictation-mic]");

        await Assertions.Expect(mic.Locator("svg")).ToBeVisibleAsync();
        (await mic.InnerTextAsync()).ShouldNotContain("🎤");
    }

    private async Task<IPage> OpenAsync()
    {
        var page = await fixture.CreatePageAsync(hasTouch: true);
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        // With nothing typed the right-hand control is the microphone; that is the premise of
        // every case here, so it is waited for rather than assumed.
        await Assertions.Expect(page.Locator("[data-testid=dictation-mic]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        return page;
    }

    private static async Task<(double X, double Y)> CentreOfAsync(IPage page, string selector)
    {
        var box = await page.Locator(selector).BoundingBoxAsync();
        box.ShouldNotBeNull($"{selector} is not on screen");
        return (box.X + box.Width / 2, box.Y + box.Height / 2);
    }

    private static Dictionary<string, object> Point(double x, double y) =>
        new() { ["x"] = x, ["y"] = y, ["id"] = 1 };

    // touchEnd carries an EMPTY touchPoints array; touchStart/touchMove must carry at least one.
    // CDP rejects the call otherwise.
    private static Task TouchAsync(ICDPSession cdp, string type, params Dictionary<string, object>[] points) =>
        cdp.SendAsync("Input.dispatchTouchEvent", new Dictionary<string, object>
        {
            ["type"] = type,
            ["touchPoints"] = points
        });
}