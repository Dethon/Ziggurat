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
public abstract class DictationE2EBase(WebChatE2EFixture fixture)
{
    // Comfortably past the 400 ms mis-tap floor and nowhere near the two-minute cap.
    protected const int HoldMs = 900;

    // Long enough that the recording holds the 8192 samples the spectrum below is measured over.
    protected const int SpectrumHoldMs = 1_400;

    protected async Task<IPage> OpenAsync(int? width = null, int? height = null)
    {
        var page = await fixture.CreatePageAsync(hasTouch: true);
        if (width is not null && height is not null)
        {
            await page.SetViewportSizeAsync(width.Value, height.Value);
        }
        await WebChatE2ETests.GotoWebChatAsync(page, fixture.WebChatUrl);
        await WebChatE2ETests.SelectUserAndAgentAsync(page, fixture.NextUserIndex());

        // With nothing typed the right-hand control is the microphone; that is the premise of
        // every case here, so it is waited for rather than assumed.
        await Assertions.Expect(page.Locator("[data-testid=dictation-mic]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 30_000 });
        return page;
    }

    // One held dictation, start to release, for cases that care only about how it ended.
    protected static async Task DictateAsync(ICDPSession cdp, IPage page, int holdMs)
    {
        var mic = await CentreOfAsync(page, "[data-testid=dictation-mic]");
        await TouchAsync(cdp, "touchStart", Point(mic.X, mic.Y));
        await Assertions.Expect(page.Locator("[data-testid=dictation-strip]"))
            .ToBeVisibleAsync(new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });
        await Task.Delay(holdMs);
        await TouchAsync(cdp, "touchEnd");
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