using Domain.Contracts;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// A page's pictures usually come from another host: commons.wikimedia.org serves its images from
// upload.wikimedia.org, unsplash.com from images.unsplash.com. A scripted fetch there is subject
// to CORS, and image CDNs send no Access-Control-Allow-Origin -- they serve <img> tags, not XHR --
// so a fetch fails on exactly the images the page is displaying perfectly well.
//
// This shipped once as "the site refused to serve i-1", which reads as the site's doing and was
// not: the same URL in the same page loads as an <img> without complaint.
[Collection(PlaywrightCollections.SharedBrowser)]
public class CrossOriginImageProbeTests(PlaywrightWebBrowserFixture fixture)
{
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task AnImageServedFromAnotherHost_StillReachesTheModel()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            // The exact page and picture that came back refused on ai370. The window is widened
            // because this page's picture sits past the default one -- a separate matter from the
            // fetch, and the envelope reports it as imagesBeyondWindow.
            var browsed = await fixture.Browser.NavigateAsync(new BrowseRequest(
                sessionId, "https://commons.wikimedia.org/wiki/File:Cat_in_Efremov,_Russia1.jpg",
                MaxLength: 100000));

            browsed.Status.ShouldBe(BrowseStatus.Success);
            browsed.ImageCount.ShouldBeGreaterThan(0);

            var fetched = await fixture.Browser.FetchImagesAsync(
                new ImageFetchRequest(sessionId, ["i-1"]));

            var image = fetched.Images.ShouldHaveSingleItem();
            image.Status.ShouldBe(ImageFetchStatus.Success);
            image.MediaType.ShouldNotBeNullOrEmpty();
            // A real picture, not an error page or a placeholder pixel.
            image.Bytes.ShouldNotBeNull().Length.ShouldBeGreaterThan(5000);
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }
}