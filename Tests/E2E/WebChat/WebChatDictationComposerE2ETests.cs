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

    [SkippableFact]
    public async Task SlidingAwayFromTheMicrophone_ThrowsTheRecordingAwayWithNothingInTheComposer()
    {
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");
        Fixture.TranscriptionStatus = 200;
        Fixture.Transcript = "esto no debería aparecer";

        var page = await OpenAsync();
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await PressableMicAsync(page);

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
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");
        Fixture.TranscriptionStatus = 200;
        Fixture.Transcript = "un dictado enganchado";

        var page = await OpenAsync();
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = await PressableMicAsync(page);
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

        // The lift hint and the strip are the browser's own, written under the finger the moment it
        // lands, so neither says the microphone is open — the gesture can be over, and latched,
        // while getUserMedia is still pending. Stopping there ends a recording that holds nothing,
        // which comes back as "the microphone had not finished opening" and an empty composer. So
        // the wait is for the run holding audio, which is the state a stop needs.
        await page.WaitForFunctionAsync(
            "() => window.dictation._run && window.dictation._run.samples > 0",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
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
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");

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
                    ? Promise.reject(new Error('This connection is not signed in.'))
                    : window.__mint(name, ...args);
            }
            """);

        await DictateAsync(cdp, page, HoldMs);
        await Assertions.Expect(refusal)
            .ToContainTextAsync("not signed in",
                new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });

        // And the transcriber that took the recording and could not answer for it. Whisper's own
        // complaint stops at the channel server, which answers the browser a bare 502, so what is
        // asserted is the sentence the browser falls back to — named, so a refusal left over from
        // the ticket above cannot be read as this one.
        await page.EvaluateAsync("() => { window.dictation._ref.invokeMethodAsync = window.__mint; }");
        Fixture.TranscriptionStatus = 500;

        await DictateAsync(cdp, page, HoldMs);
        await Assertions.Expect(refusal)
            .ToContainTextAsync("could not turn that recording into words",
                new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Assertions.Expect(page.Locator("textarea.chat-input")).ToHaveValueAsync("");

        Fixture.TranscriptionStatus = 200;
    }

    // Which failures are allowed to turn the microphone off for the rest of the page, and which are
    // not. The control stops trying only for the two things that will still be true on the next
    // press: a permission that was refused, and a browser without the APIs to record at all.
    //
    // Everything else is the device, and the device recovers. A phone that cannot open its
    // microphone this second — the input held by a call, an audio server that has just restarted,
    // no input enumerated yet after a Bluetooth headset went away — can open it perfectly well a
    // moment later. Latching on one of those is how a working phone ends up holding a dead control
    // it can only get back by reloading, which is not a thing anyone thinks to do; and the single
    // sentence it used to answer with covered four unrelated faults, so nobody could tell which had
    // happened. The name is the whole diagnosis when the phone is the only place it reproduces.
    [SkippableFact]
    public async Task AFailedOpen_LatchesOnlyForARefusedPermissionAndNamesWhatElseWentWrong()
    {
        Skip.If(string.IsNullOrEmpty(Fixture.WebChatUrl), "WebChat stack not available");
        Fixture.TranscriptionStatus = 200;
        Fixture.Transcript = "el micrófono volvió";

        var page = await OpenAsync();
        var cdp = await page.Context.NewCDPSessionAsync(page);
        var mic = page.Locator("[data-testid=dictation-mic]");
        var refusal = page.Locator(".composer-refusal");

        // Once, and only once: the press after it meets the microphone the device really has, which
        // is the half of the rule that a latch would silently take away.
        await page.EvaluateAsync(
            """
            () => {
                const open = navigator.mediaDevices.getUserMedia.bind(navigator.mediaDevices);
                let failed = false;
                navigator.mediaDevices.getUserMedia = constraints => {
                    if (failed) return open(constraints);
                    failed = true;
                    const error = new Error('Requested device not found');
                    error.name = 'NotFoundError';
                    return Promise.reject(error);
                };
            }
            """);

        var centre = await PressableMicAsync(page);
        await TouchAsync(cdp, "touchStart", Point(centre.X, centre.Y));
        await Task.Delay(HoldMs);
        await TouchAsync(cdp, "touchEnd");

        await Assertions.Expect(refusal)
            .ToContainTextAsync("NotFoundError", new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Assertions.Expect(mic).ToBeEnabledAsync();

        // And the control really does still work, rather than merely looking as though it might.
        await DictateAsync(cdp, page, HoldMs);
        await Assertions.Expect(page.Locator("textarea.chat-input"))
            .ToHaveValueAsync("el micrófono volvió", new LocatorAssertionsToHaveValueOptions { Timeout = 30_000 });

        // The words the dictation just delivered are still in the box, and a composer with text in
        // it offers send rather than the microphone — so they go, or there is nothing to press.
        await page.Locator("textarea.chat-input").FillAsync(string.Empty);
        await Assertions.Expect(mic).ToBeVisibleAsync();

        // The other side of the rule, last because there is no way back from it: a refused
        // permission will still be refused on the next press, so the control stops asking.
        await page.EvaluateAsync(
            """
            () => {
                navigator.mediaDevices.getUserMedia = () => {
                    const error = new Error('Permission denied');
                    error.name = 'NotAllowedError';
                    return Promise.reject(error);
                };
            }
            """);

        await TouchAsync(cdp, "touchStart", Point(centre.X, centre.Y));
        await Task.Delay(HoldMs);
        await TouchAsync(cdp, "touchEnd");

        await Assertions.Expect(refusal)
            .ToContainTextAsync("permission was refused",
                new LocatorAssertionsToContainTextOptions { Timeout = 30_000 });
        await Assertions.Expect(mic).ToBeDisabledAsync();
    }

}