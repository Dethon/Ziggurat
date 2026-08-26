using Domain.Contracts;
using Infrastructure.HtmlProcessing;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// The one browser-dependent step in the catalogue: what an image actually rendered at. Extraction
// reads plain attributes and is unit-tested without a browser, so this is the layer that has to
// prove the attributes are there and say what the reader would have seen.
//
// NavigateAsync only accepts http/https, so a neutral anchor page is the canvas the fixture markup
// is injected onto -- the same shape BrowserJQueryWidgetCompatTests uses.
[Collection(PlaywrightCollections.SharedBrowser)]
public class PageImageMeasurementTests(PlaywrightWebBrowserFixture fixture)
{
    [Trait("Category", "External")]
    [SkippableFact]
    public async Task AnImageSizedOnlyByStylesheet_IsFilteredOnWhatItRendered()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        // Neither image carries a markup dimension. The stylesheet is the only thing that says how
        // big they are, which is exactly the question attribute-reading cannot answer.
        var measured = await MeasureAsync(
            """
            <style>#big { width: 400px; height: 300px; } #small { width: 20px; height: 20px; }</style>
            <img id="big" src="/big.png" alt="A wide chart">
            <img id="small" src="/small.png" alt="A bullet">
            """,
            "big", "small");

        measured["big"].Survived.ShouldBeTrue();
        measured["big"].Width.ShouldBe(400);
        measured["big"].Height.ShouldBe(300);
        measured["small"].Survived.ShouldBeFalse();
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task AnImageWhoseMarkupDisagreesWithItsRendering_IsFilteredOnTheRendering()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        // Both markup claims are false in opposite directions. A filter reading width/height would
        // get both of these backwards.
        var measured = await MeasureAsync(
            """
            <style>#liar { width: 8px !important; height: 8px !important; }</style>
            <img id="liar" src="/liar.png" width="800" height="600" alt="Claims to be large">
            <img id="honest" src="/honest.png" width="10" height="10"
                 style="width:300px;height:300px" alt="Claims to be tiny">
            """,
            "liar", "honest");

        measured["liar"].Survived.ShouldBeFalse();
        measured["honest"].Survived.ShouldBeTrue();
        measured["honest"].Width.ShouldBe(300);
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task ASurvivingImage_KeepsItsAddressAndAFilteredOneLosesIt()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var measured = await MeasureAsync(
            """
            <img id="shot" src="/shot.png" style="width:420px;height:240px" alt="A screenshot">
            <img id="pixel" src="/track.gif" style="width:1px;height:1px" alt="Tracker">
            """,
            "shot", "pixel");

        // A survivor keeps src so the fetch can resolve its ref against it; everything else is
        // stripped exactly as before.
        measured["shot"].HasSrc.ShouldBeTrue();
        measured["pixel"].HasSrc.ShouldBeFalse();
    }

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task MeasuringManyImages_CostsOneEvaluationRatherThanOnePerImage()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var markup = string.Join("\n", Enumerable.Range(1, 60).Select(i =>
            $"""<img id="i{i}" src="/p{i}.png" style="width:200px;height:200px" alt="Picture {i}">"""));

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

    [Trait("Category", "External")]
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

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task ALabelThePageCarries_ComesBackWithTheFetchedPicture()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        // The fetch runs the same label ladder the entry did, so the words the model chose the
        // picture by are the words a later note names it with. Where the ladder runs out entirely
        // the note falls back to the ref, which is why every rung has to be present on both sides.
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
            // A data URI carries no filename, so give the element one the ladder can reach: the
            // rung under test is the one below enclosing-link text.
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
        // the tool breaking. Same ladder, same cut: one pair of eyes, one spelling.
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

    [Trait("Category", "External")]
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

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task ARefFromASessionThatIsGone_SaysTheSessionIsMissing()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var fetched = await fixture.Browser.FetchImagesAsync(
            new ImageFetchRequest($"never-opened-{Guid.NewGuid():N}", ["i-1"]));

        fetched.SessionMissing.ShouldBeTrue();
        fetched.Images.ShouldBeEmpty();
    }

    [Trait("Category", "External")]
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

    // The images point at paths that answer 404 on the anchor origin, which is deliberate: an
    // image is laid out at its styled size whether or not its bytes ever arrive, and the filter
    // must read the box rather than the payload.
    private async Task PrepareAsync(string sessionId, string markup)
    {
        var nav = await fixture.Browser.NavigateAsync(new BrowseRequest(sessionId, "https://example.com"));
        nav.Status.ShouldBe(BrowseStatus.Success);

        await fixture.Browser.EvaluateOnSessionAsync(
            sessionId, $"() => {{ document.body.innerHTML = {System.Text.Json.JsonSerializer.Serialize(markup)}; }}");
    }

    private const string OnePixelPngBase64 =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private readonly record struct Measured(bool Survived, int Width, int Height, bool HasSrc);
}