using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using Domain.DTOs;

namespace Infrastructure.HtmlProcessing;

public static partial class HtmlConverter
{
    public static string Convert(IElement? element, WebFetchOutputFormat format)
    {
        if (element == null)
        {
            return string.Empty;
        }

        return format switch
        {
            WebFetchOutputFormat.Html => element.InnerHtml,
            _ => ConvertToMarkdown(element)
        };
    }

    public static string Convert(string html, WebFetchOutputFormat format)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        if (format == WebFetchOutputFormat.Html)
        {
            return html;
        }

        // Parse the string directly rather than round-tripping through a byte stream, which would
        // make AngleSharp re-decode already-decoded text per any <meta charset> and mangle accents.
        var document = new HtmlParser().ParseDocument(html);

        var body = document.Body ?? document.DocumentElement;

        return ConvertToMarkdown(body);
    }

    public static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        // Ensure we have room for the truncation suffix
        var targetLength = Math.Max(0, maxLength - 20);
        if (targetLength == 0)
        {
            return "[Content truncated...]";
        }

        var truncated = text[..Math.Min(targetLength, text.Length)];

        var lastNewline = truncated.LastIndexOf('\n');
        if (lastNewline > targetLength * 0.7)
        {
            truncated = truncated[..lastNewline];
        }

        // The same backing-up the newline rule does, for the other thing a cut must not land
        // inside. A half-written entry hands the model a ref it can read and cannot use, and it
        // has no way to tell that from a ref that simply failed -- so the entry goes whole.
        if (PageImageEntry.PartialEntryStart(truncated) is var partial and >= 0)
        {
            truncated = truncated[..partial].TrimEnd();
        }

        return truncated + "\n\n[Content truncated...]";
    }

    public static string TruncateHtml(string html, int maxLength)
    {
        if (html.Length <= maxLength)
        {
            return html;
        }

        // Ensure we have room for closing tags and truncation comment
        var targetLength = Math.Max(0, maxLength - 50);
        if (targetLength == 0)
        {
            return "<!-- Content truncated -->";
        }

        var truncated = html[..Math.Min(targetLength, html.Length)];

        var lastTagEnd = truncated.LastIndexOf('>');
        var lastTagStart = truncated.LastIndexOf('<');

        if (lastTagStart > lastTagEnd)
        {
            truncated = truncated[..lastTagStart];
        }
        else if (lastTagEnd > 0)
        {
            truncated = truncated[..(lastTagEnd + 1)];
        }

        var openTags = new Stack<string>();
        var tagPattern = TagNameRegex();

        foreach (Match match in tagPattern.Matches(truncated))
        {
            var tagName = match.Groups[2].Value.ToLowerInvariant();
            var isClosing = match.Value.StartsWith("</");
            var isSelfClosing = match.Value.EndsWith("/>") || IsSelfClosingTag(tagName);

            if (isSelfClosing)
            {
                continue;
            }

            if (isClosing)
            {
                if (openTags.Count > 0 && openTags.Peek() == tagName)
                {
                    openTags.Pop();
                }
            }
            else
            {
                openTags.Push(tagName);
            }
        }

        while (openTags.Count > 0)
        {
            truncated += $"</{openTags.Pop()}>";
        }

        return truncated + "\n<!-- Content truncated -->";
    }

    private static string ConvertToMarkdown(IElement element)
    {
        var sb = new StringBuilder();
        // Numbering runs across the whole conversion, so a ref is unique within the body the model
        // is handed. A filtered image takes no number: every ref it can read is a ref it can use.
        ConvertToMarkdownRecursive(element, sb, 0, new ImageNumberer());
        var md = sb.ToString();
        md = MultipleNewlinesRegex().Replace(md, "\n\n");
        return md.Trim();
    }

    private static void ListImagesWithin(IElement element, StringBuilder sb, ImageNumberer images)
    {
        foreach (var img in element.QuerySelectorAll("img").OfType<IHtmlImageElement>())
        {
            if (PageImageEntry.LabelFor(img) is { } label)
            {
                sb.Append(PageImageEntry.Write(PageImageEntry.RefOn(img, images.Next()), label));
            }
        }
    }

    private sealed class ImageNumberer
    {
        private int _issued;

        public int Next() => ++_issued;
    }

    private static void ConvertToMarkdownRecursive(INode node, StringBuilder sb, int listDepth,
        ImageNumberer images)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child)
            {
                case IText textNode:
                    var text = WebUtility.HtmlDecode(textNode.Data);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sb.Append(text);
                    }

                    break;
                case IElement { TagName: "SCRIPT" or "STYLE" or "NOSCRIPT" }:
                    break;
                case IHtmlAnchorElement anchor:
                    var href = anchor.GetAttribute("href");
                    var linkText = anchor.TextContent.Trim();
                    if (!string.IsNullOrEmpty(href) && !string.IsNullOrEmpty(linkText))
                    {
                        sb.Append($"[{linkText}]({href})");
                    }
                    else if (!string.IsNullOrEmpty(linkText))
                    {
                        sb.Append(linkText);
                    }

                    // A link is rendered from its text, which no image contributes to -- so a
                    // picture wrapped in one would be swallowed whole. Product photos and gallery
                    // thumbnails are linked far more often than not, which would have made the
                    // catalogue miss exactly the images worth asking for.
                    ListImagesWithin(anchor, sb, images);

                    break;
                case IHtmlImageElement img:
                    // An entry, not a markdown image: the model reaches the picture by ref through
                    // the session that listed it, and the address it would otherwise carry costs
                    // context on every browse to buy nothing.
                    if (PageImageEntry.LabelFor(img) is { } label)
                    {
                        sb.Append(PageImageEntry.Write(PageImageEntry.RefOn(img, images.Next()), label));
                    }

                    break;
                case IElement elem:
                    ConvertElementToMarkdown(elem, sb, listDepth, images);
                    break;
            }
        }
    }

    private static void ConvertElementToMarkdown(IElement elem, StringBuilder sb, int listDepth,
        ImageNumberer images)
    {
        var tag = elem.TagName.ToUpperInvariant();

        switch (tag)
        {
            case "H1":
                sb.Append("\n\n# ");
                ConvertToMarkdownRecursive(elem, sb, listDepth, images);
                sb.Append("\n\n");
                break;
            case "H2":
                sb.Append("\n\n## ");
                ConvertToMarkdownRecursive(elem, sb, listDepth, images);
                sb.Append("\n\n");
                break;
            case "H3":
                sb.Append("\n\n### ");
                ConvertToMarkdownRecursive(elem, sb, listDepth, images);
                sb.Append("\n\n");
                break;
            case "H4":
                sb.Append("\n\n#### ");
                ConvertToMarkdownRecursive(elem, sb, listDepth, images);
                sb.Append("\n\n");
                break;
            case "H5":
                sb.Append("\n\n##### ");
                ConvertToMarkdownRecursive(elem, sb, listDepth, images);
                sb.Append("\n\n");
                break;
            case "H6":
                sb.Append("\n\n###### ");
                ConvertToMarkdownRecursive(elem, sb, listDepth, images);
                sb.Append("\n\n");
                break;
            case "P":
            case "DIV":
            case "ARTICLE":
            case "SECTION":
            case "MAIN":
            case "ASIDE":
            case "HEADER":
            case "FOOTER":
            case "NAV":
                sb.Append("\n\n");
                ConvertToMarkdownRecursive(elem, sb, listDepth, images);
                sb.Append("\n\n");
                break;
            case "BR":
                sb.Append("  \n");
                break;
            case "HR":
                sb.Append("\n\n---\n\n");
                break;
            case "STRONG":
            case "B":
                sb.Append("**");
                ConvertToMarkdownRecursive(elem, sb, listDepth, images);
                sb.Append("**");
                break;
            case "EM":
            case "I":
                sb.Append('*');
                ConvertToMarkdownRecursive(elem, sb, listDepth, images);
                sb.Append('*');
                break;
            case "CODE":
                sb.Append('`');
                sb.Append(elem.TextContent);
                sb.Append('`');
                break;
            case "PRE":
                sb.Append("\n\n```\n");
                sb.Append(elem.TextContent);
                sb.Append("\n```\n\n");
                break;
            case "BLOCKQUOTE":
                sb.Append("\n\n> ");
                var quoteText = elem.TextContent.Trim().Replace("\n", "\n> ");
                sb.Append(quoteText);
                sb.Append("\n\n");
                break;
            case "UL":
            case "OL":
                sb.AppendLine();
                ConvertToMarkdownRecursive(elem, sb, listDepth + 1, images);
                sb.AppendLine();
                break;
            case "LI":
                var indent = listDepth > 0 ? new string(' ', (listDepth - 1) * 2) : "";
                var parent = elem.ParentElement;
                var bullet = parent?.TagName == "OL" ? "1." : "-";
                sb.Append($"{indent}{bullet} ");
                ConvertToMarkdownRecursive(elem, sb, listDepth > 0 ? listDepth : 1, images);
                sb.AppendLine();
                break;
            case "TABLE":
                ConvertTableToMarkdown(elem, sb);
                break;
            case "DL":
                ConvertDefinitionListToMarkdown(elem, sb);
                break;
            case "FIGURE":
                ConvertToMarkdownRecursive(elem, sb, listDepth, images);
                break;
            case "FIGCAPTION":
                sb.Append("\n*");
                ConvertToMarkdownRecursive(elem, sb, listDepth, images);
                sb.Append("*\n");
                break;
            default:
                ConvertToMarkdownRecursive(elem, sb, listDepth, images);
                break;
        }
    }

    private static void ConvertTableToMarkdown(IElement table, StringBuilder sb)
    {
        sb.Append("\n\n");

        var rows = table.QuerySelectorAll("tr");
        var isFirstRow = true;

        foreach (var row in rows)
        {
            var cells = row.QuerySelectorAll("th, td");
            if (cells.Length == 0)
            {
                continue;
            }

            sb.Append('|');
            foreach (var cell in cells)
            {
                var cellText = cell.TextContent.Trim().Replace("|", "\\|").Replace("\n", " ");
                sb.Append($" {cellText} |");
            }

            sb.AppendLine();

            if (isFirstRow)
            {
                sb.Append('|');
                foreach (var _ in cells)
                {
                    sb.Append(" --- |");
                }

                sb.AppendLine();
                isFirstRow = false;
            }
        }

        sb.AppendLine();
    }

    private static void ConvertDefinitionListToMarkdown(IElement dl, StringBuilder sb)
    {
        sb.AppendLine();

        foreach (var child in dl.Children)
        {
            switch (child.TagName)
            {
                case "DT":
                    sb.Append("**");
                    sb.Append(child.TextContent.Trim());
                    sb.AppendLine("**");
                    break;
                case "DD":
                    sb.Append(": ");
                    sb.AppendLine(child.TextContent.Trim());
                    break;
            }
        }

        sb.AppendLine();
    }

    private static bool IsSelfClosingTag(string tagName)
    {
        return tagName is "br" or "hr" or "img" or "input" or "meta" or "link" or "area" or "base" or "col" or "embed"
            or "param" or "source" or "track" or "wbr";
    }

    [GeneratedRegex(@"<(/?)(\w+)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex TagNameRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultipleNewlinesRegex();
}