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