using Domain.Contracts;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// A page's pictures usually come from another host: commons.wikimedia.org serves its images from
// upload.wikimedia.org, unsplash.com from images.unsplash.com. A credentialed fetch there is
// rejected against the wildcard Access-Control-Allow-Origin those CDNs serve, so it failed on
// exactly the images the page was displaying perfectly well; the anonymous canvas read is gated
// by the same header and passes where the CDN sends one.
//
// This shipped once as "the site refused to serve i-1", which reads as the site's doing and was
// not: the same URL in the same page loads as an <img> without complaint.
[Collection(PlaywrightCollections.IsolatedSessions)]
public class CrossOriginImageProbeTests(IsolatedSessionBrowserFixture fixture)
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

            // Attestation only: the hermetic twins own the behaviour, so a third party's bad
            // day is a skip here, never a failure of a bare run.
            Skip.If(browsed.Status != BrowseStatus.Success, "Wikimedia unreachable.");
            Skip.If(browsed.ImageCount == 0, "The picture is gone from the page.");

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