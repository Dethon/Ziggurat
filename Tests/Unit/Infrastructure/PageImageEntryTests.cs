using Domain.Contracts;
using Infrastructure.HtmlProcessing;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// The catalogue rules, asserted on the markdown a page produces. Every image here carries the
// dimensions the browser would have stamped on it (data-img-w/data-img-h), because that is what
// production hands the extractor -- markup width/height is what the filter must not read.
public class PageImageEntryTests
{
    [Fact]
    public async Task AnImageThatSurvivesFiltering_IsListedWhereItSitsInTheDocument()
    {
        var html = Page("""
                        <h1>First section</h1>
                        <p>Before the picture.</p>
                        <img src="/photo.jpg" alt="A harbour at dusk" data-img-w="640" data-img-h="480" data-img-ref="i-1">
                        <p>After the picture.</p>
                        """);

        var content = await ContentOf(html);

        var entry = content.IndexOf("[image i-1", StringComparison.Ordinal);
        entry.ShouldBeGreaterThan(content.IndexOf("Before the picture", StringComparison.Ordinal));
        entry.ShouldBeLessThan(content.IndexOf("After the picture", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImageRefs_AreNumberedInDocumentOrderAndSpelledApartFromElementRefs()
    {
        var html = Page("""
                        <img src="/one.jpg" alt="First" data-img-w="300" data-img-h="300" data-img-ref="i-1">
                        <img src="/two.jpg" alt="Second" data-img-w="300" data-img-h="300" data-img-ref="i-2">
                        """);

        var content = await ContentOf(html);

        content.ShouldContain("[image i-1: First]");
        content.ShouldContain("[image i-2: Second]");
        content.ShouldNotContain("e-1");
    }

    [Theory]
    // The ladder, one rung per row: each strips the rung above it.
    [InlineData(
        """<img src="/p.jpg" alt="Alt text" title="Title text" data-img-w="300" data-img-h="300" data-img-ref="i-1">""",
        "Alt text")]
    [InlineData(
        """<figure><img src="/p.jpg" title="Title text" data-img-w="300" data-img-h="300" data-img-ref="i-1"><figcaption>Caption text</figcaption></figure>""",
        "Caption text")]
    [InlineData(
        """<img src="/p.jpg" title="Title text" data-img-w="300" data-img-h="300" data-img-ref="i-1">""",
        "Title text")]
    [InlineData(
        """<a href="/go"><img src="/p.jpg" data-img-w="300" data-img-h="300" data-img-ref="i-1">Link text</a>""",
        "Link text")]
    [InlineData(
        """<img src="/gallery/sunset-over-the-bay.jpg" data-img-w="300" data-img-h="300" data-img-ref="i-1">""",
        "sunset-over-the-bay.jpg")]
    public async Task ALabel_FallsBackDownTheLadderUntilSomethingSpeaks(string markup, string expected)
    {
        var content = await ContentOf(Page(markup));

        content.ShouldContain($": {expected}]");
    }

    [Fact]
    public async Task AnImageWithNothingToSay_IsListedWithItsRenderedDimensions()
    {
        // A data URI has no filename either, so the ladder runs out and size is all that is left.
        // Dimensions alone still separate a photograph from a logo.
        var html = Page("""<img src="data:image/png;base64,iVBORw0KGgo=" data-img-w="640" data-img-h="480" data-img-ref="i-1">""");

        var content = await ContentOf(html);

        content.ShouldContain("[image i-1: 640x480]");
    }

    [Theory]
    [InlineData(99, 400)]
    [InlineData(400, 99)]
    [InlineData(1, 1)]
    public async Task AnImageUnderTheSizeFloor_GetsNoEntryAndNoRef(int width, int height)
    {
        var html = Page($"""
                         <img src="/spacer.gif" alt="Spacer" data-img-w="{width}" data-img-h="{height}">
                         <img src="/real.jpg" alt="Real" data-img-w="800" data-img-h="600" data-img-ref="i-1">
                         """);

        var content = await ContentOf(html);

        content.ShouldNotContain("Spacer");
        // The page stamps survivors only, so the survivor carries the first ref and nothing the
        // model can read hints at a handle it cannot use.
        content.ShouldContain("[image i-1: Real]");
    }

    [Fact]
    public async Task AnImageWithNoStampedDimensions_IsFilteredOut()
    {
        // Nothing measured it, so nothing vouches for it being content. Markup attributes are not
        // consulted precisely because they lie.
        var html = Page("""<img src="/p.jpg" alt="Unmeasured" width="800" height="600">""");

        var content = await ContentOf(html);

        content.ShouldNotContain("Unmeasured");
        content.ShouldNotContain("[image i-");
    }

    [Fact]
    public async Task AnImageAddress_NeverReachesTheModel()
    {
        // The ref is the handle; the URL costs context and buys nothing the ref does not.
        var html = Page("""<img src="https://cdn.example.com/very/long/path/photo.jpg?token=abc" alt="Photo" data-img-w="800" data-img-h="600" data-img-ref="i-1">""");

        var content = await ContentOf(html);

        content.ShouldContain("[image i-1: Photo]");
        content.ShouldNotContain("cdn.example.com");
    }

    [Fact]
    public async Task TheNumberOfListedImages_IsReported()
    {
        var html = Page("""
                        <img src="/one.jpg" alt="One" data-img-w="300" data-img-h="300" data-img-ref="i-1">
                        <img src="/tiny.gif" alt="Tiny" data-img-w="10" data-img-h="10">
                        <img src="/two.jpg" alt="Two" data-img-w="300" data-img-h="300" data-img-ref="i-2">
                        """);

        var result = await HtmlProcessor.ProcessAsync(Request(), html, CancellationToken.None);

        result.ImageCount.ShouldBe(2);
    }

    [Fact]
    public async Task ALabelCarryingBracketsOrNewlines_DoesNotBreakTheEntry()
    {
        var html = Page("""
                        <img src="/p.jpg" alt="A [bracketed]
                        caption" data-img-w="300" data-img-h="300" data-img-ref="i-1">
                        """);

        var content = await ContentOf(html);

        content.ShouldContain("[image i-1: A (bracketed) caption]");
    }

    [Fact]
    public async Task AThoroughLabel_ArrivesWhole()
    {
        // The cap is a safeguard against a pathological attribute, not an editor: a carefully
        // written alt -- APOD's run a few hundred characters -- is exactly what the model picks
        // a picture by, and cutting it threw that work away on every browse.
        var html = Page($"""<img src="/p.jpg" alt="{new string('x', 400)}" data-img-w="300" data-img-h="300" data-img-ref="i-1">""");

        var content = await ContentOf(html);

        content.ShouldContain($"[image i-1: {new string('x', 400)}]");
    }

    [Fact]
    public async Task APathologicalLabel_IsStillShortened()
    {
        var html = Page($"""<img src="/p.jpg" alt="{new string('x', 700)}" data-img-w="300" data-img-h="300" data-img-ref="i-1">""");

        var content = await ContentOf(html);

        content.ShouldContain($"[image i-1: {new string('x', 500)}…]");
    }

    [Fact]
    public async Task TheFilenameRung_SpeaksThroughTheConverterToo()
    {
        // ImageLabelTests pins the rung on the ladder itself; this pins the extraction-side facts
        // builder feeding it -- a src the parser mangled would show up as a picture listed by
        // dimensions instead of by name.
        var content = await ContentOf(Page(
            """<img src="/gallery/harbour-at-dusk.png" data-img-w="300" data-img-h="300" data-img-ref="i-1">"""));

        content.ShouldContain("[image i-1: harbour-at-dusk.png]");
    }

    [Fact]
    public async Task AnImageInsideALinkThatWrapsOtherContent_IsStillListed()
    {
        // The shape a real page uses: the anchor contains the picture and nothing else worth
        // rendering as link text. commons.wikimedia.org's file pages are built exactly this way.
        var content = await ContentOf(Page(
            """<a href="/file.jpg"><img src="/photo.jpg" alt="A cat" data-img-w="539" data-img-h="600" data-img-ref="i-1"></a>"""));

        content.ShouldContain("[image i-1: A cat]");
    }

    [Fact]
    public async Task AnImageThePageNeverStamped_GetsNoEntryAndNoRef()
    {
        // The stamped ref is the only source of an entry's ref: the fetch resolves refs against
        // the live DOM, so a number the converter invented would be a handle the model acts on
        // and is refused by. An unstamped picture is treated like a non-survivor.
        var html = Page("""<img src="/p.jpg" alt="Unstamped" data-img-w="640" data-img-h="480">""");

        var content = await ContentOf(html);

        content.ShouldNotContain("[image");
        content.ShouldNotContain("Unstamped");
    }

    [Fact]
    public async Task AnUnstampedImageInsideATable_GetsNoEntryEither()
    {
        var html = Page("""
                        <table><tr><td>
                            <img src="/p.jpg" alt="Unstamped" data-img-w="640" data-img-h="480">
                        </td></tr></table>
                        """);

        var content = await ContentOf(html);

        content.ShouldNotContain("[image");
    }

    [Fact]
    public async Task AnUnstampedImageInsideALink_GetsNoEntryEither()
    {
        var html = Page("""
                        <a href="/go"><img src="/p.jpg" alt="Unstamped" data-img-w="640" data-img-h="480"></a>
                        """);

        var content = await ContentOf(html);

        content.ShouldNotContain("[image");
    }

    private static async Task<string> ContentOf(string html) =>
        (await HtmlProcessor.ProcessAsync(Request(), html, CancellationToken.None)).Content!;

    private static BrowseRequest Request() =>
        new(SessionId: "test", Url: "http://example.com/test", MaxLength: 100000);

    private static string Page(string body) =>
        $"""
         <!DOCTYPE html>
         <html>
         <head><title>Test</title></head>
         <body>{body}</body>
         </html>
         """;
}