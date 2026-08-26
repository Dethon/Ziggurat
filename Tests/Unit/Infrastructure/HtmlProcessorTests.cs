using Domain.Contracts;
using Infrastructure.HtmlProcessing;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class HtmlProcessorTests
{
    [Fact]
    public async Task ProcessAsync_WithValidHtml_ReturnsContent()
    {
        // Arrange
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test Page</title></head>
                   <body>
                       <article>
                           <h1>Hello World</h1>
                           <p>This is test content.</p>
                       </article>
                   </body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test");

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.IsPartial.ShouldBeFalse();
        result.Title.ShouldBe("Test Page");
        result.Content.ShouldNotBeNullOrEmpty();
        result.Content.ShouldContain("Hello World");
    }

    [Fact]
    public async Task ProcessAsync_HtmlDeclaresLegacyCharset_PreservesUnicodeAccents()
    {
        // Playwright's page.ContentAsync() returns a correctly-decoded Unicode string even when the
        // site declares and serves a legacy charset (aemet.es serves ISO-8859-15). The serialized
        // HTML still carries the <meta charset> tag. If the markdown extractor re-decodes that
        // already-decoded string through the meta charset, every accented char double-encodes
        // (é -> Ã©, ° -> Â°). The accents must survive verbatim.
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head>
                       <meta http-equiv="Content-Type" content="text/html; charset=ISO-8859-15" />
                       <title>Predicción 7 días</title>
                   </head>
                   <body>
                       <p>El miércoles habrá lluvia en Cáceres. Temperatura máxima 30°C.</p>
                   </body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "https://www.aemet.es/");

        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        result.Content.ShouldNotBeNull();
        result.Content.ShouldNotContain("Ã");
        result.Content.ShouldNotContain("Â");
        result.Content.ShouldContain("miércoles");
        result.Content.ShouldContain("Cáceres");
        result.Content.ShouldContain("30°C");
        result.Title.ShouldBe("Predicción 7 días");
    }

    [Fact]
    public async Task ProcessAsync_WithNonMatchingSelector_ReturnsPartialStatus()
    {
        // Arrange
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test Page</title></head>
                   <body><p>Content</p></body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test", Selector: ".nonexistent");

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.IsPartial.ShouldBeTrue();
        result.ErrorMessage!.ShouldContain("nonexistent");
    }

    [Fact]
    public async Task ProcessAsync_ExtractsMetadata()
    {
        // Arrange
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head>
                       <title>Test Page</title>
                       <meta name="description" content="Page description">
                       <meta name="author" content="John Doe">
                       <meta property="og:site_name" content="Example Site">
                       <meta property="article:published_time" content="2024-01-15T10:00:00Z">
                   </head>
                   <body><p>Content</p></body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test");

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.Metadata.ShouldNotBeNull();
        result.Metadata.Description.ShouldBe("Page description");
        result.Metadata.Author.ShouldBe("John Doe");
        result.Metadata.SiteName.ShouldBe("Example Site");
        result.Metadata.DatePublished.ShouldBe(new DateOnly(2024, 1, 15));
    }

    [Fact]
    public async Task ProcessAsync_WithMarkdownFormat_ConvertsToMarkdown()
    {
        // Arrange
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test</title></head>
                   <body>
                       <h1>Title</h1>
                       <p>Text with <strong>bold</strong> and <em>italic</em>.</p>
                       <a href="https://example.com">Link</a>
                   </body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test");

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.Content!.ShouldContain("# Title");
        result.Content!.ShouldContain("**bold**");
        result.Content!.ShouldContain("*italic*");
        result.Content!.ShouldContain("[Link](https://example.com)");
    }

    [Fact]
    public async Task ProcessAsync_TruncatesLongContent()
    {
        // Arrange
        var longContent = string.Join("\n",
            Enumerable.Range(1, 1000).Select(i => $"<p>Paragraph {i} with some content.</p>"));
        var html = $"""
                    <!DOCTYPE html>
                    <html>
                    <head><title>Test</title></head>
                    <body>{longContent}</body>
                    </html>
                    """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test", MaxLength: 500);

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.Truncated.ShouldBeTrue();
        result.Content!.Length.ShouldBeLessThanOrEqualTo(520);
        result.ContentLength.ShouldBeGreaterThan(500); // ContentLength is total length (for pagination)
        result.Content!.ShouldContain("[Content truncated...]");
    }

    [Fact]
    public async Task ProcessAsync_WithClassSelector_ReturnsAllMatches()
    {
        // Arrange
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test</title></head>
                   <body>
                       <div class="product">
                           <p>Product 1</p>
                       </div>
                       <div class="product">
                           <p>Product 2</p>
                       </div>
                       <div class="product">
                           <p>Product 3</p>
                       </div>
                   </body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test", Selector: ".product");

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.IsPartial.ShouldBeFalse();
        result.Content.ShouldNotBeNull();
        result.Content.ShouldContain("Product 1");
        result.Content.ShouldContain("Product 2");
        result.Content.ShouldContain("Product 3");
        result.Content.ShouldContain("---"); // Separator between matches
    }

    [Fact]
    public async Task ProcessAsync_WithAnImageSelector_ListsTheEntriesInsteadOfNothing()
    {
        // A live test scoped a browse to "img" hoping for the page's image catalogue and got
        // an empty body: the converter renders an image where it meets one as a child, but a
        // selector hands it the <img> itself as the root, and a root with no children wrote
        // nothing at all.
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test</title></head>
                   <body>
                       <img src="a.jpg" alt="A tall ship at anchor"
                            data-img-w="300" data-img-h="200" data-img-ref="i-7">
                       <img src="b.jpg" alt="The same ship under sail"
                            data-img-w="300" data-img-h="200" data-img-ref="i-8">
                   </body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test", Selector: "img");

        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        result.IsPartial.ShouldBeFalse();
        result.Content.ShouldNotBeNull();
        result.Content.ShouldContain("[image i-7: A tall ship at anchor]");
        result.Content.ShouldContain("[image i-8: The same ship under sail]");
        result.ImageCount.ShouldBe(2);
    }

    [Fact]
    public async Task ProcessAsync_WithClassSelector_LinksInContentAreRendered()
    {
        // Arrange
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test</title></head>
                   <body>
                       <div class="card">
                           <a href="https://example.com/1">Link 1</a>
                       </div>
                       <div class="card">
                           <a href="https://example.com/2">Link 2</a>
                       </div>
                   </body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test", Selector: ".card");

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.Content.ShouldNotBeNull();
        result.Content.ShouldContain("https://example.com/1");
        result.Content.ShouldContain("https://example.com/2");
    }

    [Fact]
    public async Task ProcessAsync_WithOffset_ReturnsContentFromOffset()
    {
        // Arrange
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test</title></head>
                   <body>
                       <p>First paragraph with some content.</p>
                       <p>Second paragraph with more content.</p>
                       <p>Third paragraph with final content.</p>
                   </body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test", Offset: 0, MaxLength: 50);

        // Act - First chunk
        var result1 = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Act - Second chunk starting from offset
        var request2 = request with { Offset = 50 };
        var result2 = await HtmlProcessor.ProcessAsync(request2, html, CancellationToken.None);

        // Assert
        result1.Truncated.ShouldBeTrue();
        result2.Content.ShouldNotBeNull();
        result1.Content.ShouldNotBe(result2.Content);
        result1.ContentLength.ShouldBe(result2.ContentLength);
    }

    [Fact]
    public async Task ProcessAsync_NextOffset_ContinuesExactlyWhereTheCutLanded()
    {
        // The truncated body carries a "[Content truncated...]" suffix and the cut backs up to a
        // newline or past a partial image entry, so the body's length is not the consumed length.
        // NextOffset must name the source position the cut actually landed on: paging with it
        // reassembles the page with nothing skipped and nothing beyond whitespace repeated.
        var longContent = string.Join("\n",
            Enumerable.Range(1, 200).Select(i => $"<p>Paragraph {i} with some content.</p>"));
        var html = $"<html><body>{longContent}</body></html>";
        var request = new BrowseRequest(SessionId: "t", Url: "http://example.com/", MaxLength: 500);

        var whole = await HtmlProcessor.ProcessAsync(
            request with { MaxLength = 100000 }, html, CancellationToken.None);
        var window1 = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);
        var window2 = await HtmlProcessor.ProcessAsync(
            request with { Offset = window1.NextOffset!.Value, MaxLength = 100000 },
            html, CancellationToken.None);

        window1.Truncated.ShouldBeTrue();
        var body1 = window1.Content!.Replace("\n\n[Content truncated...]", "");
        (body1 + window2.Content).ShouldBe(whole.Content);
    }

    [Fact]
    public async Task ProcessAsync_AnEntryLongerThanTheWindow_GoesOutWholeAndPagingAdvances()
    {
        // The cut backs up past a partial entry; when the window starts exactly at an entry whose
        // text exceeds the window, backing up consumes nothing and nextOffset stood still — the
        // model paged offset 20 → 20 → 20 forever while imagesBeyondWindow kept promising the
        // entry ahead. The cap is a safeguard, not an editor: the entry goes out whole instead.
        var longAlt = string.Join(" ", Enumerable.Repeat("word", 90));
        var html = $"""
            <html><body>
            <p>Some opening text.</p>
            <img src="/a.jpg" alt="{longAlt}" data-img-w="300" data-img-h="300" data-img-ref="i-1">
            <p>Text after the picture.</p>
            </body></html>
            """;
        var request = new BrowseRequest(SessionId: "t", Url: "http://example.com/", MaxLength: 300);

        var offset = 0;
        var listed = 0;
        for (var hop = 0; hop < 20; hop++)
        {
            var window = await HtmlProcessor.ProcessAsync(
                request with { Offset = offset }, html, CancellationToken.None);
            listed += window.ImageCount;
            if (window.NextOffset is not { } next)
            {
                break;
            }

            next.ShouldBeGreaterThan(offset);
            offset = next;
        }

        // Paging reached the entry: it was listed in exactly one window, whole.
        listed.ShouldBe(1);
    }

    [Fact]
    public async Task ProcessAsync_APageThatFitsWhole_HasNoNextOffset()
    {
        var html = "<html><body><p>All of it.</p></body></html>";
        var request = new BrowseRequest(SessionId: "t", Url: "http://example.com/");

        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        result.Truncated.ShouldBeFalse();
        result.NextOffset.ShouldBeNull();
    }

    [Fact]
    public async Task ProcessAsync_WithOffsetBeyondContent_ReturnsEmptyContent()
    {
        // Arrange
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test</title></head>
                   <body><p>Short content</p></body>
                   </html>
                   """;
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test", Offset: 100000);

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.Content.ShouldBe("");
    }

    [Fact]
    public async Task ProcessAsync_WithMultiClassSelector_ReturnsMatchedElements()
    {
        // Arrange
        var html = """
                   <!DOCTYPE html>
                   <html>
                   <head><title>Test</title></head>
                   <body>
                       <ul>
                           <li class="item active"><strong>Item 1</strong> active</li>
                           <li class="item">Item 2 not active</li>
                           <li class="item active special"><em>Item 3</em> active special</li>
                           <li class="item active">Item 4 active</li>
                       </ul>
                   </body>
                   </html>
                   """;
        var request = new BrowseRequest(
            SessionId: "test",
            Url: "http://example.com/test",
            Selector: "li.item.active");

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.IsPartial.ShouldBeFalse();
        result.Content.ShouldNotBeNull();
        result.Content.ShouldContain("Item 1");
        result.Content.ShouldContain("Item 3");
        result.Content.ShouldNotContain("Item 2 not active"); // Missing 'active' class
        result.Content.ShouldContain("Item 4 active");
    }

    [Fact]
    public async Task ProcessAsync_WithControlCharacters_StripsInvalidChars()
    {
        // Arrange - Content with control characters that break LLM APIs (422)
        // ReSharper disable VariableLengthStringHexEscapeSequence
        var html = "<!DOCTYPE html><html><head><title>Test</title></head><body>" +
                   "<p>Normal text</p>" +
                   "<p>Has\x00null\x01and\x02control\x1Fchars</p>" +
                   "<p>Tabs\tand\nnewlines are fine</p>" +
                   "</body></html>";
        var request = new BrowseRequest(SessionId: "test", Url: "http://example.com/test");

        // Act
        var result = await HtmlProcessor.ProcessAsync(request, html, CancellationToken.None);

        // Assert
        result.Content.ShouldNotBeNull();
        result.Content.ShouldContain("Normal text");
        var invalidChars = result.Content!.Where(c => c < ' ' && c != '\t' && c != '\n' && c != '\r').ToList();
        invalidChars.ShouldBeEmpty($"Content contains invalid control characters: {string.Join(", ", invalidChars.Select(c => $"U+{(int)c:X4}"))}");
    }

    // Reddit's comments live in <shreddit-comment> custom elements, which readability scores as
    // non-content, so on a real 1.1MB thread it picked the cookie notice -- the densest block of
    // ordinary prose on the page -- as the article and returned 625 characters of consent text with
    // no comments. The pre-existing fallback only fires when readability returns nothing at all,
    // and this returns something, confidently. Four browses in production came back this way and
    // the agent reported it could not see the comments because of a consent wall, when they had
    // been in the DOM the whole time.
    //
    // The bounds come from measuring real captures rather than taste. Falling back needs all three,
    // because no single one separates the cases:
    //   reddit  /anime   625 chars of 133627 (0.47%)  <- must fall back
    //   reddit  /nvidia  657 chars of 117547 (0.56%)  <- must fall back
    //   arstechnica      977 chars of  59950 (1.63%)  <- thin but correct, must be kept
    //   bbc news        8116 chars of 105496 (7.69%)  <- must be kept
    [Theory]
    // The two production failures.
    [InlineData(625, 133627, true)]
    [InlineData(657, 117547, true)]
    // Thin-but-correct extractions that must survive.
    [InlineData(977, 59950, false)]
    [InlineData(8116, 105496, false)]
    [InlineData(3367, 14860, false)]
    [InlineData(28434, 56833, false)]
    // A small page is not evidence of a bad extraction: with little text anywhere, a short
    // article is simply the whole story.
    [InlineData(200, 900, false)]
    [InlineData(0, 0, false)]
    public void ReadabilityExtractionLooksTruncated_SeparatesFailedExtractionsFromThinOnes(
        int extractedLength, int bodyLength, bool expected)
    {
        HtmlProcessor.ReadabilityExtractionLooksTruncated(extractedLength, bodyLength)
            .ShouldBe(expected);
    }
}