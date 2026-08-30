using Domain.Contracts;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// A same-origin image is fetched with its bytes exactly as served — which handed the model
// image/svg+xml the day Wikipedia's own site logo was fetched, and the vision provider answered
// the whole request with HTTP 400. Only rasters the chat wire accepts may leave as-served;
// anything else is read off the canvas and leaves as PNG, like every cross-origin picture.
[Collection(PlaywrightCollections.IsolatedSessions)]
public class SameOriginSvgFetchTests(IsolatedSessionBrowserFixture fixture)
{
    [SkippableFact]
    public async Task ASameOriginSvg_LeavesAsPngRatherThanAFormatNoProviderTakes()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            await HermeticPage.PrepareAsync(fixture.Browser, sessionId, "<p>anchor</p>");

            // A blob URL carries the page's own origin, so the fetch takes the same-origin
            // branch — the exact path that leaked SVG bytes through. Route-fulfilled anchor,
            // blob-served bytes: this is the hermetic twin of the vector rule, no third party
            // involved.
            // Awaited to onload: a fixture image must actually load before the measurement, or
            // the stamp is a race the cold container loses.
            await fixture.Browser.EvaluateOnSessionAsync<int>(
                sessionId,
                """
                async () => {
                    const svg = '<svg xmlns="http://www.w3.org/2000/svg" width="200" height="200">'
                        + '<rect width="200" height="200" fill="tomato"/></svg>';
                    const url = URL.createObjectURL(new Blob([svg], { type: 'image/svg+xml' }));
                    document.body.innerHTML =
                        `<img id="vector" src="${url}" style="width:200px;height:200px" alt="A vector logo">`;
                    const img = document.getElementById('vector');
                    await new Promise(r => {
                        if (img.complete) return r();
                        img.onload = r;
                        img.onerror = r;
                    });
                    return document.body.offsetHeight;
                }
                """);
            await fixture.Browser.AnnotateImagesOnSessionAsync(sessionId);
            var stamped = await fixture.Browser.EvaluateOnSessionAsync<string>(
                sessionId, "() => document.getElementById('vector').getAttribute('data-img-ref') ?? ''");
            stamped.ShouldNotBeNullOrEmpty();

            var fetched = await fixture.Browser.FetchImagesAsync(new ImageFetchRequest(sessionId, [stamped]));

            var image = fetched.Images.ShouldHaveSingleItem();
            image.Status.ShouldBe(ImageFetchStatus.Success);
            image.MediaType.ShouldBe("image/png");
            image.Bytes!.Length.ShouldBeGreaterThan(0);
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }
}