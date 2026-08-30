using Domain.Contracts;
using Infrastructure.HtmlProcessing;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// The one browser-dependent step in the catalogue: what an image actually rendered at. Extraction
// reads plain attributes and is unit-tested without a browser, so this is the layer that has to
// prove the attributes are there and say what the reader would have seen.
//
// NavigateAsync only accepts http/https, so a route-fulfilled anchor page (HermeticPage) is the
// canvas the fixture markup is injected onto -- no third party involved anywhere in this file.
[Collection(PlaywrightCollections.IsolatedSessions)]
public class PageImageMeasurementTests(IsolatedSessionBrowserFixture fixture)
{
    [SkippableFact]
    public async Task AnImageSizedOnlyByStylesheet_IsFilteredOnWhatItRendered()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        // Neither image carries a markup dimension. The stylesheet is the only thing that says how
        // big they are, which is exactly the question attribute-reading cannot answer.
        //
        // The bytes are a data URI rather than a path nobody serves, and every case here that wants
        // an image to survive does the same. A broken image keeps its styled box only until the 404
        // lands: after that it falls back to its intrinsic size, which is zero, and the picture the
        // case sized in pixels filters out as furniture. Whether the measure pass ran before that
        // is a race, and it is the one the loaded machine loses. The dead link has its own case
        // below, where the collapse is the subject rather than the hazard.
        var measured = await MeasureAsync(
            $$"""
            <style>#big { width: 400px; height: 300px; } #small { width: 20px; height: 20px; }</style>
            <img id="big" src="data:image/png;base64,{{OnePixelPngBase64}}" alt="A wide chart">
            <img id="small" src="data:image/png;base64,{{OnePixelPngBase64}}" alt="A bullet">
            """,
            "big", "small");

        measured["big"].Survived.ShouldBeTrue();
        measured["big"].Width.ShouldBe(400);
        measured["big"].Height.ShouldBe(300);
        measured["small"].Survived.ShouldBeFalse();
    }

    [SkippableFact]
    public async Task AnImageWhoseMarkupDisagreesWithItsRendering_IsFilteredOnTheRendering()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        // Both markup claims are false in opposite directions. A filter reading width/height would
        // get both of these backwards.
        var measured = await MeasureAsync(
            $$"""
            <style>#liar { width: 8px !important; height: 8px !important; }</style>
            <img id="liar" src="data:image/png;base64,{{OnePixelPngBase64}}" width="800" height="600"
                 alt="Claims to be large">
            <img id="honest" src="data:image/png;base64,{{OnePixelPngBase64}}" width="10" height="10"
                 style="width:300px;height:300px" alt="Claims to be tiny">
            """,
            "liar", "honest");

        measured["liar"].Survived.ShouldBeFalse();
        measured["honest"].Survived.ShouldBeTrue();
        measured["honest"].Width.ShouldBe(300);
    }

    [SkippableFact]
    public async Task ASurvivingImage_KeepsItsAddressAndAFilteredOneLosesIt()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var measured = await MeasureAsync(
            $$"""
            <img id="shot" src="data:image/png;base64,{{OnePixelPngBase64}}"
                 style="width:420px;height:240px" alt="A screenshot">
            <img id="pixel" src="data:image/png;base64,{{OnePixelPngBase64}}"
                 style="width:1px;height:1px" alt="Tracker">
            """,
            "shot", "pixel");

        // A survivor keeps src so the fetch can resolve its ref against it; everything else is
        // stripped exactly as before.
        measured["shot"].HasSrc.ShouldBeTrue();
        measured["pixel"].HasSrc.ShouldBeFalse();
    }

    [SkippableFact]
    public async Task MeasuringManyImages_CostsOneEvaluationRatherThanOnePerImage()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        // Bytes that really arrive, so the sixty boxes stay the size the style gives them. A src
        // nobody serves measures at 200px right after injection and collapses to nothing once the
        // 404 lands, because a broken image falls back to its intrinsic size — zero — and the
        // inline width does not hold the box open. Whether the measure pass beat that collapse was
        // a race the loaded machine lost: sixty stamped became fifty-eight, reported as the
        // one-pass claim failing. What this case is about is the cost of the pass, not what a dead
        // link renders as, which AnImageWhoseBytesNeverArrived_ReportsADeadLinkNotARefusal owns.
        var markup = string.Join("\n", Enumerable.Range(1, 60).Select(i =>
            $"""<img id="i{i}" src="data:image/png;base64,{OnePixelPngBase64}" style="width:200px;height:200px" alt="Picture {i}">"""));

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            await PrepareAsync(sessionId, markup);

            var before = DateTimeOffset.UtcNow;
            await fixture.Browser.AnnotateImagesOnSessionAsync(sessionId);
            var elapsed = DateTimeOffset.UtcNow - before;

            var stamped = await fixture.Browser.EvaluateOnSessionAsync<int>(
                sessionId, $"() => document.querySelectorAll('[{PageImageEntry.WidthAttribute}]').length");

            stamped.ShouldBe(60);
            // One pass over the page, not sixty round trips: a per-image crossing would put this
            // in the seconds even on a local container.
            elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task AnImageOnTheLivePage_IsFetchedByItsRefThroughThatSession()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            // A data URI, so the bytes are real image bytes and the assertion is about the fetch
            // path rather than about what some remote host happens to serve today. The path under
            // test is the same either way: the request goes out of the page, so it carries that
            // page's cookies and fingerprint.
            await PrepareAsync(
                sessionId,
                $"""
                 <img id="live" src="data:image/png;base64,{OnePixelPngBase64}"
                      style="width:300px;height:300px" alt="A logo">
                 """);
            await fixture.Browser.AnnotateImagesOnSessionAsync(sessionId);

            var fetched = await fixture.Browser.FetchImagesAsync(new ImageFetchRequest(sessionId, ["i-1"]));

            var image = fetched.Images.ShouldHaveSingleItem();
            image.Status.ShouldBe(ImageFetchStatus.Success);
            image.Bytes.ShouldNotBeNull().Length.ShouldBeGreaterThan(0);
            image.MediaType.ShouldNotBeNullOrEmpty();
            image.Label.ShouldBe("A logo");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task ALabelThePageCarries_ComesBackWithTheFetchedPicture()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        // The fetch reads the page's facts and the one ladder names the picture, so the words
        // the model chose it by are the words a later note names it with -- by construction now,
        // not by a mirrored script.
        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            // Bytes that really arrive, so the assertion is about the label the fetch carries back
            // rather than about a failure path.
            await PrepareAsync(
                sessionId,
                $"""
                 <img id="named" src="data:image/png;base64,{OnePixelPngBase64}"
                      style="width:300px;height:300px">
                 """);
            await fixture.Browser.AnnotateImagesOnSessionAsync(sessionId);
            // A data URI carries no filename, so give the element a title the ladder can reach --
            // proving the fetch-side facts builder feeds the rungs below the description.
            await fixture.Browser.EvaluateOnSessionAsync(
                sessionId,
                """
                () => document.getElementById('named').setAttribute('title', 'harbour-at-dusk.png')
                """);

            var fetched = await fixture.Browser.FetchImagesAsync(new ImageFetchRequest(sessionId, ["i-1"]));

            var image = fetched.Images.ShouldHaveSingleItem();
            image.Status.ShouldBe(ImageFetchStatus.Success);
            image.Label.ShouldBe("harbour-at-dusk.png");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task ALongLabel_IsCutTheWayTheEntryCutIt()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        // The entry ends an over-cap label with an ellipsis so the cut announces itself; the
        // fetch used to slice the same length bare, and a live test read the mid-word stop as
        // the tool breaking. One ladder, one cut -- this pins the integration end of it.
        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            var longAlt = new string('x', 700);
            await PrepareAsync(
                sessionId,
                $"""
                 <img src="data:image/png;base64,{OnePixelPngBase64}" alt="{longAlt}"
                      style="width:300px;height:300px">
                 """);
            await fixture.Browser.AnnotateImagesOnSessionAsync(sessionId);

            var fetched = await fixture.Browser.FetchImagesAsync(new ImageFetchRequest(sessionId, ["i-1"]));

            var image = fetched.Images.ShouldHaveSingleItem();
            image.Status.ShouldBe(ImageFetchStatus.Success);
            image.Label.ShouldBe(new string('x', 500) + "…");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task ACrossOriginImageTheCdnShares_ComesBackAsServedRatherThanReEncoded()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        // The common CDN case, live on Wikimedia: the host sends ACAO, so an anonymous fetch
        // keeps the bytes exactly as served. The canvas used to answer first and re-encoded
        // every cross-origin jpeg as a fatter PNG — the moon left at 1.3MB.
        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            await PrepareAsync(
                sessionId,
                """
                <img id="shared" src="https://acao-cdn.test/pic.jpg" alt="A full moon"
                     style="width:300px;height:300px">
                """);

            await fixture.Browser.RouteOnSessionAsync(sessionId, "https://acao-cdn.test/pic.jpg", route =>
                route.FulfillAsync(new()
                {
                    ContentType = "image/jpeg",
                    Headers = new Dictionary<string, string> { ["Access-Control-Allow-Origin"] = "*" },
                    BodyBytes = Convert.FromBase64String(OnePixelJpegBase64)
                }));
            await fixture.Browser.EvaluateOnSessionAsync(
                sessionId,
                """
                async () => {
                    const img = document.getElementById('shared');
                    img.src = img.src; // re-request now that the route answers
                    await new Promise(r => { img.onload = r; img.onerror = r; });
                }
                """);
            await fixture.Browser.AnnotateImagesOnSessionAsync(sessionId);

            var fetched = await fixture.Browser.FetchImagesAsync(new ImageFetchRequest(sessionId, ["i-1"]));

            var image = fetched.Images.ShouldHaveSingleItem();
            image.Status.ShouldBe(ImageFetchStatus.Success);
            image.MediaType.ShouldBe("image/jpeg");
            image.Bytes.ShouldBe(Convert.FromBase64String(OnePixelJpegBase64));
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task AnImageCorsWontRelease_IsReadOffTheScreenInstead()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        // JPL's gallery, live: a CDN that sends no ACAO header at all fails the anonymous probe,
        // the rendered element taints the canvas, and a picture the page plainly displays came
        // back as the site refusing. The pixels are already painted — an element screenshot reads
        // them without CORS having a say.
        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            await PrepareAsync(
                sessionId,
                """
                <img id="guarded" src="https://no-acao-cdn.test/pic.png" alt="A rover on Mars"
                     style="width:300px;height:300px">
                """);

            // Fulfilled from the test with no Access-Control-Allow-Origin: the element loads
            // (a plain <img> needs no CORS) but every script-side read of its pixels is refused.
            await fixture.Browser.RouteOnSessionAsync(sessionId, "https://no-acao-cdn.test/pic.png", route =>
                route.FulfillAsync(new()
                {
                    ContentType = "image/png",
                    BodyBytes = Convert.FromBase64String(OnePixelPngBase64)
                }));
            await fixture.Browser.EvaluateOnSessionAsync(
                sessionId,
                """
                async () => {
                    const img = document.getElementById('guarded');
                    img.src = img.src; // re-request now that the route answers
                    await new Promise(r => { img.onload = r; img.onerror = r; });
                }
                """);
            await fixture.Browser.AnnotateImagesOnSessionAsync(sessionId);

            var fetched = await fixture.Browser.FetchImagesAsync(new ImageFetchRequest(sessionId, ["i-1"]));

            var image = fetched.Images.ShouldHaveSingleItem();
            image.Status.ShouldBe(ImageFetchStatus.Success);
            image.MediaType.ShouldBe("image/png");
            image.Bytes!.Length.ShouldBeGreaterThan(0);
            image.Label.ShouldBe("A rover on Mars");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task AnImageWhoseBytesNeverArrived_ReportsADeadLinkNotARefusal()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        // Styled to survive the size filter, pointing at an address that answers 404: the page
        // lays the box out, the bytes never arrive. Smithsonian's rotted 2012 blog images are the
        // live case — the old "site refused, trying again may work" answer sent the model round
        // two pointless retries.
        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            await PrepareAsync(
                sessionId,
                """<img id="gone" src="/no-such-picture.jpg" style="width:300px;height:300px" alt="A rotted link">""");
            await fixture.Browser.AnnotateImagesOnSessionAsync(sessionId);

            var fetched = await fixture.Browser.FetchImagesAsync(new ImageFetchRequest(sessionId, ["i-1"]));

            fetched.Images.ShouldHaveSingleItem().Status.ShouldBe(ImageFetchStatus.NeverLoaded);
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task ARefFromASessionThatIsGone_SaysTheSessionIsMissing()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var fetched = await fixture.Browser.FetchImagesAsync(
            new ImageFetchRequest($"never-opened-{Guid.NewGuid():N}", ["i-1"]));

        fetched.SessionMissing.ShouldBeTrue();
        fetched.Images.ShouldBeEmpty();
    }

    [SkippableFact]
    public async Task ARefNamingNothingOnThePage_IsRefusedWithoutBeingConfusedForADeadSession()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            await PrepareAsync(sessionId, """<p>no pictures here</p>""");
            await fixture.Browser.AnnotateImagesOnSessionAsync(sessionId);

            var fetched = await fixture.Browser.FetchImagesAsync(new ImageFetchRequest(sessionId, ["i-7"]));

            fetched.SessionMissing.ShouldBeFalse();
            fetched.Images.ShouldHaveSingleItem().Status.ShouldBe(ImageFetchStatus.NotAnImageRef);
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    private async Task<Dictionary<string, Measured>> MeasureAsync(string markup, params string[] ids)
    {
        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            await PrepareAsync(sessionId, markup);
            await fixture.Browser.AnnotateImagesOnSessionAsync(sessionId);

            var results = new Dictionary<string, Measured>();
            foreach (var id in ids)
            {
                var raw = await fixture.Browser.EvaluateOnSessionAsync<string>(
                    sessionId,
                    $$"""
                      () => {
                          const img = document.getElementById('{{id}}');
                          return [
                              img.getAttribute('{{PageImageEntry.WidthAttribute}}') ?? '',
                              img.getAttribute('{{PageImageEntry.HeightAttribute}}') ?? '',
                              img.hasAttribute('src') ? '1' : '0'
                          ].join('|');
                      }
                      """);

                var parts = raw.Split('|');
                results[id] = new Measured(
                    Survived: parts[0].Length > 0,
                    Width: int.TryParse(parts[0], out var w) ? w : 0,
                    Height: int.TryParse(parts[1], out var h) ? h : 0,
                    HasSrc: parts[2] == "1");
            }

            return results;
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    // Some images point at addresses nothing answers, which is deliberate: an image is laid out
    // at its styled size whether or not its bytes ever arrive, and the filter must read the box
    // rather than the payload.

    private Task PrepareAsync(string sessionId, string markup) =>
        HermeticPage.PrepareAsync(fixture.Browser, sessionId, markup);

    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private const string OnePixelJpegBase64 =
        "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD3+iiigD//2Q==";

    private readonly record struct Measured(bool Survived, int Width, int Height, bool HasSrc);
}