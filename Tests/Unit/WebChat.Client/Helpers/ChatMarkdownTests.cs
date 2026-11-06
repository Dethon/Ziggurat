using Shouldly;
using WebChat.Client.Helpers;

namespace Tests.Unit.WebChat.Client.Helpers;

// What a message renders as on the chat page: Markdown, never markup. A message is whoever wrote it —
// another member of a shared topic, or a model that read a page or a household server's answer — and
// the page is same-origin with the admin API, so a script in it acts as the reader (pass 18: Óscar's
// `<img onerror>` in a shared topic ran in Nuria's page, an owner's).
public class ChatMarkdownTests
{
    [Theory]
    [InlineData("<img src=\"x\" onerror=\"alert(1)\">")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("hola <svg onload=alert(1)></svg> adiós")]
    [InlineData("<a href=\"https://example.test\" onclick=\"alert(1)\">x</a>")]
    [InlineData("<iframe src=\"/accounts/admin/household/members\"></iframe>")]
    public void RawHtml_IsShownAsText_NeverAsMarkup(string content)
    {
        var html = ChatMarkdown.ToHtml(content);

        html.ShouldNotContain("<img");
        html.ShouldNotContain("<script");
        html.ShouldNotContain("<svg");
        html.ShouldNotContain("<iframe");
        html.ShouldNotContain("onclick=\"");
        html.ShouldContain("&lt;");
    }

    [Theory]
    [InlineData("[pulsa](javascript:alert(1))")]
    [InlineData("[pulsa](JaVaScRiPt:alert(1))")]
    [InlineData("[pulsa](java&#115;cript:alert(1))")]
    [InlineData("[pulsa](vbscript:msgbox(1))")]
    [InlineData("[pulsa](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)")]
    [InlineData("<javascript:alert(1)>")]
    [InlineData("[pulsa][r]\n\n[r]: javascript:alert(1)")]
    [InlineData("![foto](javascript:alert(1))")]
    public void ALinkToAScript_LeadsNowhere(string content)
    {
        var html = ChatMarkdown.ToHtml(content);

        html.ShouldNotContain("<a ");
        html.ShouldNotContain("<img");
        html.ShouldNotContain("href=", Case.Insensitive);
        html.ShouldNotContain("src=", Case.Insensitive);
    }

    [Fact]
    public void ALinkToAScript_KeepsItsWordsAsText()
    {
        ChatMarkdown.ToHtml("mira [esto **aquí**](javascript:alert(1)) ya").ShouldBe("<p>mira esto <strong>aquí</strong> ya</p>\n");
    }

    [Theory]
    [InlineData("[web](https://example.test/a?b=1)", "href=\"https://example.test/a?b=1\"")]
    [InlineData("[correo](mailto:ana@example.test)", "href=\"mailto:ana@example.test\"")]
    [InlineData("https://example.test/bare", "href=\"https://example.test/bare\"")]
    [InlineData("[aquí](/browse/abc/handoff/t)", "href=\"/browse/abc/handoff/t\"")]
    [InlineData("**negrita** y `<b>código</b>`", "<strong>negrita</strong>")]
    [InlineData("| a | b |\n|---|---|\n| 1 | 2 |", "<table>")]
    public void Markdown_StillRenders(string content, string expected)
    {
        ChatMarkdown.ToHtml(content).ShouldContain(expected);
    }

    [Fact]
    public void HtmlInACodeSpan_IsShownAsCode()
    {
        ChatMarkdown.ToHtml("`<b>código</b>`").ShouldContain("<code>&lt;b&gt;código&lt;/b&gt;</code>");
    }

    [Fact]
    public void NoContent_IsNothing()
    {
        ChatMarkdown.ToHtml(null).ShouldBe("");
        ChatMarkdown.ToHtml("").ShouldBe("");
    }
}