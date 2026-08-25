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
                        <img src="/photo.jpg" alt="A harbour at dusk" data-img-w="640" data-img-h="480">
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
                        <img src="/one.jpg" alt="First" data-img-w="300" data-img-h="300">
                        <img src="/two.jpg" alt="Second" data-img-w="300" data-img-h="300">
                        """);

        var content = await ContentOf(html);

        content.ShouldContain("[image i-1: First]");
        content.ShouldContain("[image i-2: Second]");
        content.ShouldNotContain("e-1");
    }

    [Theory]
    // The ladder, one rung per row: each strips the rung above it.
    [InlineData(
        """<img src="/p.jpg" alt="Alt text" title="Title text" data-img-w="300" data-img-h="300">""",
        "Alt text")]
    [InlineData(
        """<figure><img src="/p.jpg" title="Title text" data-img-w="300" data-img-h="300"><figcaption>Caption text</figcaption></figure>""",
        "Caption text")]
    [InlineData(
        """<img src="/p.jpg" title="Title text" data-img-w="300" data-img-h="300">""",
        "Title text")]
    [InlineData(
        """<a href="/go"><img src="/p.jpg" data-img-w="300" data-img-h="300">Link text</a>""",
        "Link text")]
    [InlineData(
        """<img src="/gallery/sunset-over-the-bay.jpg" data-img-w="300" data-img-h="300">""",
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
        var html = Page("""<img src="data:image/png;base64,iVBORw0KGgo=" data-img-w="640" data-img-h="480">""");

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
                         <img src="/real.jpg" alt="Real" data-img-w="800" data-img-h="600">
                         """);

        var content = await ContentOf(html);

        content.ShouldNotContain("Spacer");
        // The survivor takes the first ref: a filtered image consumes no number, so nothing the
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
        var html = Page("""<img src="https://cdn.example.com/very/long/path/photo.jpg?token=abc" alt="Photo" data-img-w="800" data-img-h="600">""");

        var content = await ContentOf(html);

        content.ShouldContain("[image i-1: Photo]");
        content.ShouldNotContain("cdn.example.com");
    }

    [Fact]
    public async Task TheNumberOfListedImages_IsReported()
    {
        var html = Page("""
                        <img src="/one.jpg" alt="One" data-img-w="300" data-img-h="300">
                        <img src="/tiny.gif" alt="Tiny" data-img-w="10" data-img-h="10">
                        <img src="/two.jpg" alt="Two" data-img-w="300" data-img-h="300">
                        """);

        var result = await HtmlProcessor.ProcessAsync(Request(), html, CancellationToken.None);

        result.ImageCount.ShouldBe(2);
    }

    [Fact]
    public async Task ALabelCarryingBracketsOrNewlines_DoesNotBreakTheEntry()
    {
        var html = Page("""
                        <img src="/p.jpg" alt="A [bracketed]
                        caption" data-img-w="300" data-img-h="300">
                        """);

        var content = await ContentOf(html);

        content.ShouldContain("[image i-1: A (bracketed) caption]");
    }

    [Fact]
    public async Task AVeryLongLabel_IsShortened()
    {
        // The always-on cost is paid by every browse, fetch or not.
        var html = Page($"""<img src="/p.jpg" alt="{new string('x', 400)}" data-img-w="300" data-img-h="300">""");

        var content = await ContentOf(html);

        var entry = content[content.IndexOf("[image i-1", StringComparison.Ordinal)..];
        entry[..entry.IndexOf(']')].Length.ShouldBeLessThan(140);
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