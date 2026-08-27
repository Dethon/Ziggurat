using Domain.Contracts;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// The live page the tainted-canvas fallback was built against: JPL's gallery serves every image
// from a CloudFront host that sends no ACAO header at all, so in-page reads are refused wholesale.
// The fetch re-requests the address from outside the page — CORS binds script inside it, nothing
// else — and only falls back to a screenshot when that fails too.
[Collection(PlaywrightCollections.IsolatedSessions)]
public class NoAcaoCdnFetchTests(IsolatedSessionBrowserFixture fixture)
{
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task AJpegOnANoAcaoCdn_ComesBackAsServedRatherThanAsAScreenshot()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            var nav = await fixture.Browser.NavigateAsync(new BrowseRequest(
                sessionId, "https://www.jpl.nasa.gov/images/", ScrollToLoad: true));
            Skip.If(nav.Status != BrowseStatus.Success, "JPL unreachable.");

            // A .jpg ref, because it is the proof: a screenshot leaves as PNG, so bytes arriving
            // as image/jpeg can only be the CDN's own.
            var jpegRef = await fixture.Browser.EvaluateOnSessionAsync<string>(
                sessionId,
                """
                () => [...document.querySelectorAll('img[data-img-ref]')]
                    .find(i => /\.jpe?g$/i.test((i.getAttribute('src') ?? '').split(/[?#]/)[0]))
                    ?.getAttribute('data-img-ref') ?? ''
                """);
            Skip.If(jpegRef.Length == 0, "No jpeg in the gallery today.");

            var fetched = await fixture.Browser.FetchImagesAsync(new ImageFetchRequest(sessionId, [jpegRef]));

            var image = fetched.Images.ShouldHaveSingleItem();
            image.Status.ShouldBe(ImageFetchStatus.Success);
            image.MediaType.ShouldBe("image/jpeg");
            image.Bytes!.Length.ShouldBeGreaterThan(0);
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }
}