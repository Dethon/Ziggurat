using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace WebChat.Client.Helpers;

// A message as the chat page draws it: Markdown, never markup. Whoever wrote a message — another member
// of a shared topic, or a model that read a page or a household server's answer — is not trusted on this
// origin, which the admin API shares, so raw HTML is shown as the text it is and a link only leads to a
// web page, a mail address or a path of the deployment's own: a script in either acted as the reader.
public static class ChatMarkdown
{
    private static readonly MarkdownPipeline _pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    private static readonly string[] _schemes = ["http", "https", "mailto", "tel"];

    public static string ToHtml(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return "";
        }

        var document = Markdown.Parse(content, _pipeline);
        foreach (var link in document.Descendants<LinkInline>().Where(l => !IsSafe(l.Url)).ToList())
        {
            // Its words stay, as text: a link that leads nowhere would still be a click that navigates.
            foreach (var child in link.ToList())
            {
                child.Remove();
                link.InsertBefore(child);
            }

            link.Remove();
        }

        foreach (var link in document.Descendants<AutolinkInline>().Where(l => !IsSafe(l.Url)).ToList())
        {
            link.ReplaceBy(new LiteralInline(link.Url));
        }

        return document.ToHtml(_pipeline);
    }

    // A destination with no scheme is a path or a fragment of this site; anything else names its scheme
    // before the first ':' that comes before any '/', '?' or '#'.
    private static bool IsSafe(string? url)
    {
        var trimmed = (url ?? "").Trim();
        var colon = trimmed.IndexOf(':');
        var end = trimmed.IndexOfAny(['/', '?', '#']);
        return colon < 0 || (end >= 0 && end < colon)
            || _schemes.Contains(trimmed[..colon], StringComparer.OrdinalIgnoreCase);
    }
}