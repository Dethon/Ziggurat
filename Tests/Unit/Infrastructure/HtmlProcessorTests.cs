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
}