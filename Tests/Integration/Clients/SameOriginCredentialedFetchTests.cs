using Domain.Contracts;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// The same-origin half of the credentials rule, proven hermetically: the wire fetch carries the
// page's own cookies, so a picture the site serves only to its logged-in pages still leaves
// exactly as served. The route answers the bytes only to a request presenting the cookie -- a
// regression to an anonymous same-origin fetch gets a 403, falls to the canvas, and leaves as a
// re-encoded PNG this test refuses.
[Collection(PlaywrightCollections.IsolatedSessions)]
public class SameOriginCredentialedFetchTests(IsolatedSessionBrowserFixture fixture)
{
    [SkippableFact]
    public async Task ASameOriginImageBehindACookie_LeavesAsServedThroughTheCredentialedFetch()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            await HermeticPage.PrepareAsync(fixture.Browser, sessionId, "<p>anchor</p>");

            await fixture.Browser.EvaluateOnSessionAsync(
                sessionId, "() => { document.cookie = 'auth=yes'; }");

            var imageUrl = $"{HermeticPage.AnchorUrl}gallery/pic.jpg";
            var jpeg = Convert.FromBase64String(OnePixelJpegBase64);
            await fixture.Browser.RouteOnContextAsync(imageUrl, route =>
            {
                var cookie = route.Request.Headers.GetValueOrDefault("cookie") ?? "";
                return cookie.Contains("auth=yes", StringComparison.Ordinal)
                    ? route.FulfillAsync(new() { ContentType = "image/jpeg", BodyBytes = jpeg })
                    : route.FulfillAsync(new() { Status = 403 });
            });

            // Injected after the route stands, and awaited to onload, so the measurement below
            // reads a box the bytes are actually holding open.
            await fixture.Browser.EvaluateOnSessionAsync<int>(
                sessionId,
                """
                async () => {
                    document.body.innerHTML =
                        '<img id="mine" src="/gallery/pic.jpg" style="width:300px;height:300px"'
                        + ' alt="Our own picture">';
                    const img = document.getElementById('mine');
                    await new Promise(r => {
                        if (img.complete) return r();
                        img.onload = r;
                        img.onerror = r;
                    });
                    return document.body.offsetHeight;
                }
                """);
            await fixture.Browser.AnnotateImagesOnSessionAsync(sessionId);

            var fetched = await fixture.Browser.FetchImagesAsync(new ImageFetchRequest(sessionId, ["i-1"]));

            var image = fetched.Images.ShouldHaveSingleItem();
            image.Status.ShouldBe(ImageFetchStatus.Success);
            // As served, not a canvas re-encode: only the credentialed wire fetch can answer jpeg.
            image.MediaType.ShouldBe("image/jpeg");
            image.Bytes.ShouldBe(jpeg);
            image.Label.ShouldBe("Our own picture");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    private const string OnePixelJpegBase64 =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD3+iiigD//2Q==";
}