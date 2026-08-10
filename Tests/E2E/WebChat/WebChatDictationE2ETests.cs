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