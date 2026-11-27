using System.Text.RegularExpressions;
using Domain.Contracts;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// web_screenshot against a real page: a viewport-sized JPEG of the current tab, a screen asked for
// by number scrolled to, and the refs a form flow is holding left standing.
[Collection(PlaywrightCollections.IsolatedSessionsD)]
public partial class PageScreenshotTests(IsolatedSessionBrowserFixture fixture)
{
    // Three and a bit screens at the fixture's 1920x1080 viewport.
    private const string TallPage = """<div style="height:3500px;background:linear-gradient(#c33,#33c)">top</div>""";

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task AScreenshot_IsTheViewportAsAJpeg_AndCountsThePagesScreens()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        await WithPageAsync(TallPage, async sessionId =>
        {
            var shot = await fixture.Browser.ScreenshotAsync(new ScreenshotRequest(sessionId));

            shot.Status.ShouldBe(ScreenshotStatus.Success, shot.ErrorMessage);
            shot.MediaType.ShouldBe("image/jpeg");
            JpegSize(shot.Bytes!).ShouldBe((1920, 1080));
            shot.Url.ShouldBe(HermeticPage.AnchorUrl);
            shot.Screen.ShouldBe(1);
            shot.Screens.ShouldBe(4);
        });
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task AScreenAskedFor_IsScrolledTo_AndOnePastTheEndShowsTheLast()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        await WithPageAsync(TallPage, async sessionId =>
        {
            var second = await fixture.Browser.ScreenshotAsync(new ScreenshotRequest(sessionId, Screen: 2));

            second.Screen.ShouldBe(2);
            (await fixture.Browser.EvaluateOnSessionAsync<int>(sessionId, "() => Math.round(window.scrollY)"))
                .ShouldBe(1080);

            var pastTheEnd = await fixture.Browser.ScreenshotAsync(new ScreenshotRequest(sessionId, Screen: 9));

            pastTheEnd.Status.ShouldBe(ScreenshotStatus.Success, pastTheEnd.ErrorMessage);
            pastTheEnd.Screen.ShouldBe(pastTheEnd.Screens);

            // Unasked, it shows the page where it stands, which is where the last call left it.
            var standing = await fixture.Browser.ScreenshotAsync(new ScreenshotRequest(sessionId));
            standing.Screen.ShouldBe(pastTheEnd.Screens);
        });
    }

    // A screenshot taken in the middle of a form must not cost the form its refs.
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task AScreenshot_LeavesTheRefsItWasTakenBetweenStanding()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        await WithPageAsync("""<button onclick="this.textContent='Pressed'">Press me</button>""", async sessionId =>
        {
            var snapshot = await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId));
            var buttonRef = RefPattern().Match(snapshot.Snapshot!).Value;

            await fixture.Browser.ScreenshotAsync(new ScreenshotRequest(sessionId));
            var acted = await fixture.Browser.ActionAsync(new WebActionRequest(sessionId, buttonRef));

            acted.Status.ShouldBe(WebActionStatus.Success, acted.ErrorMessage);
        });
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task ASessionWithNoPage_IsNotFound()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var shot = await fixture.Browser.ScreenshotAsync(new ScreenshotRequest($"test-{Guid.NewGuid():N}"));

        shot.Status.ShouldBe(ScreenshotStatus.SessionNotFound);
    }

    private async Task WithPageAsync(string markup, Func<string, Task> test)
    {
        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            await HermeticPage.PrepareAsync(fixture.Browser, sessionId, markup);
            await test(sessionId);
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    // A baseline or progressive frame header carries the picture's size: marker, length, precision,
    // then height and width, big-endian. Walked segment by segment, because a table's bytes before
    // it can spell a frame marker too.
    private static (int Width, int Height) JpegSize(byte[] jpeg)
    {
        var at = 2;
        while (jpeg[at + 1] is not (0xC0 or 0xC2))
        {
            at += 2 + ((jpeg[at + 2] << 8) | jpeg[at + 3]);
        }

        return ((jpeg[at + 7] << 8) | jpeg[at + 8], (jpeg[at + 5] << 8) | jpeg[at + 6]);
    }

    [GeneratedRegex(@"\be-\d+\b")]
    private static partial Regex RefPattern();
}