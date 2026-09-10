using Domain.DTOs;
using Infrastructure.HtmlProcessing;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class HtmlConverterTests
{
    // A button reads as plain text in markdown, and a page whose editions are reached by
    // buttons read as a list of names — so a model composed the urls it could not see. Marked,
    // the text says what it is: something to click through a snapshot's ref, not an address.
    [Fact]
    public void Convert_AButton_IsMarkedAsOne()
    {
        var markdown = HtmlConverter.Convert(
            "<html><body><ul><li><button>La rifa de 2024</button></li></ul></body></html>",
            WebFetchOutputFormat.Markdown);

        markdown.ShouldContain("[button: La rifa de 2024]");
    }

    // The button case flattens its subtree to text, and text is not where a picture survives — the
    // same hole the anchor case was given ListImagesWithin for. An image-only button (a gallery
    // thumbnail, an icon that opens a viewer) otherwise loses its entry and its ref with it.
    [Fact]
    public void Convert_AnImageInsideAButton_IsStillListed()
    {
        var markdown = HtmlConverter.Convert(
            """<html><body><button><img src="/thumb.jpg" alt="Vista de la sala" data-img-w="300" data-img-h="300" data-img-ref="i-4"></button></body></html>""",
            WebFetchOutputFormat.Markdown);

        markdown.ShouldContain("i-4");
        markdown.ShouldContain("Vista de la sala");
    }

    // TextContent keeps the markup's own newlines and indentation, so a button written across
    // several lines marked up as a multi-line block: the marker has to read as one thing.
    [Fact]
    public void Convert_AButtonWrittenAcrossLines_IsMarkedOnOneLine()
    {
        var markdown = HtmlConverter.Convert(
            "<html><body><button>\n  <span>Icono</span>\n  Comprar ahora\n</button></body></html>",
            WebFetchOutputFormat.Markdown);

        markdown.ShouldContain("[button: Icono Comprar ahora]");
    }

    [Fact]
    public void Convert_HtmlDeclaresLegacyCharset_PreservesUnicodeAccents()
    {
        // Convert(string) backs the readability path. The input is an already-decoded Unicode
        // string; it must not be re-decoded through any <meta charset> in the markup, which would
        // double-encode accents (é -> Ã©) on pages declaring a legacy charset.
        var html = """
                   <html>
                   <head><meta charset="ISO-8859-15"></head>
                   <body><p>El miércoles en Cáceres, máxima 30°C.</p></body>
                   </html>
                   """;

        var markdown = HtmlConverter.Convert(html, WebFetchOutputFormat.Markdown);

        markdown.ShouldNotContain("Ã");
        markdown.ShouldNotContain("Â");
        markdown.ShouldContain("miércoles");
        markdown.ShouldContain("Cáceres");
        markdown.ShouldContain("30°C");
    }
}