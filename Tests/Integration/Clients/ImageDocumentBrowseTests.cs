using System.Diagnostics;
using Domain.Contracts;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// A URL that answers with the picture itself: the browser renders its image-viewer document, and
// Camoufox's juggler never reports DOMContentLoaded for one — so the browse burned its whole
// 30-second wait on an event that cannot come and then answered "did not fully load" for a page
// that was nothing but the fully loaded picture. The committed response names its type; an image
// document waits for the image, not the event.
[Collection(PlaywrightCollections.IsolatedSessions)]
public class ImageDocumentBrowseTests(IsolatedSessionBrowserFixture fixture)
{
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task ADirectImageUrl_AnswersSuccessWithoutWaitingOutTheClock()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            var sw = Stopwatch.StartNew();
            var nav = await fixture.Browser.NavigateAsync(new BrowseRequest(
                sessionId,
                "https://upload.wikimedia.org/wikipedia/commons/thumb/1/15/Cat_August_2010-4.jpg/330px-Cat_August_2010-4.jpg"));
            sw.Stop();

            // Attestation only: skip -- never fail -- when the third party is having a bad day,
            // whether the site is unreachable or the picture itself is gone.
            Skip.If(nav.Status != BrowseStatus.Success, "Wikimedia unreachable.");
            Skip.If(nav.ImageCount == 0, "The picture is gone.");
            nav.ErrorMessage.ShouldBeNull();
            nav.ImageCount.ShouldBe(1);
            // Well under the 30s the dead wait used to cost; generous enough for a slow fetch.
            sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));

            // The entry's ref reaches the picture like any other page's would.
            var imageRef = System.Text.RegularExpressions.Regex.Match(nav.Content!, @"\[image (i-\d+):").Groups[1].Value;
            var fetched = await fixture.Browser.FetchImagesAsync(new ImageFetchRequest(sessionId, [imageRef]));
            fetched.Images.ShouldHaveSingleItem().Status.ShouldBe(ImageFetchStatus.Success);
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }
}