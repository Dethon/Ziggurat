using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Domain.Contracts;
using Domain.DTOs;
using SmartReader;

namespace Infrastructure.HtmlProcessing;

public record HtmlProcessingResult(
    string? Title,
    string? Content,
    int ContentLength,
    bool Truncated,
    WebPageMetadata? Metadata,
    bool IsPartial,
    string? ErrorMessage)
{
    // How many images the returned body lists, and how many the model would only reach by paging
    // forward. An image it cannot see is one it cannot know to ask for.
    public int ImageCount { get; init; }

    public int ImagesBeyondWindow { get; init; }
}

public static partial class HtmlProcessor
{
    public static async Task<HtmlProcessingResult> ProcessAsync(BrowseRequest request, string html,
        CancellationToken ct)
    {
        // Parse the string directly. The html came from page.ContentAsync() and is already a
        // correctly-decoded Unicode string; round-tripping it through a byte stream (req.Content)
        // makes AngleSharp re-decode it per the document's <meta charset>, double-encoding accents
        // on pages that declare a legacy charset (e.g. aemet.es / ISO-8859-15).
        var document = new HtmlParser().ParseDocument(html);

        if (!string.IsNullOrEmpty(request.Selector))
        {
            return ProcessWithSelector(request, document);
        }

        if (request.UseReadability)
        {
            return await ProcessWithReadabilityAsync(request, html, document, ct);
        }

        return ProcessFullBody(request, document);
    }

    private static HtmlProcessingResult ProcessWithSelector(BrowseRequest request, IDocument document)
    {
        var elements = document.QuerySelectorAll(request.Selector!);
        if (elements.Length == 0)
        {
            return CreatePartialResult(document.Title, ExtractMetadata(document),
                $"CSS selector '{request.Selector}' did not match any elements");
        }

        var contentParts = elements.Select(e => HtmlConverter.Convert(e, WebFetchOutputFormat.Markdown)).ToList();
        var content = string.Join("\n\n---\n\n", contentParts);

        return CreateSuccessResult(request.MaxLength, request.Offset, document.Title, content,
            ExtractMetadata(document));
    }

    private static HtmlProcessingResult ProcessFullBody(BrowseRequest request, IDocument document)
    {
        var content = HtmlConverter.Convert(document.Body ?? document.DocumentElement, WebFetchOutputFormat.Markdown);

        return CreateSuccessResult(request.MaxLength, request.Offset, document.Title, content,
            ExtractMetadata(document));
    }

    private static async Task<HtmlProcessingResult> ProcessWithReadabilityAsync(
        BrowseRequest request, string html, IDocument document, CancellationToken ct)
    {
        var article = await new Reader(request.Url, html).GetArticleAsync(ct);

        if (string.IsNullOrEmpty(article.Content))
        {
            return ProcessFullBody(request, document);
        }

        // Readability can also fail by succeeding: on a page whose real content sits in custom
        // elements it scores as non-content, it settles on whatever plain prose is left -- a
        // consent notice, a subscription pitch -- and returns that as the article. Compare what it
        // kept against what the document actually holds, and prefer the body when the two disagree
        // by an order of magnitude.
        if (ReadabilityExtractionLooksTruncated(
                (article.TextContent ?? string.Empty).Length,
                (document.Body?.TextContent ?? string.Empty).Trim().Length))
        {
            return ProcessFullBody(request, document);
        }

        var metadata = UpdateMetadataFromArticle(ExtractMetadata(document), article);
        var articleContent = HtmlConverter.Convert(article.Content, WebFetchOutputFormat.Markdown);

        return CreateSuccessResult(request.MaxLength, request.Offset, article.Title, articleContent, metadata);
    }

    // All three bounds are needed to tell a failed extraction from a legitimately thin one.
    // Measured on real captures: reddit yields 625 chars from a 133627-char body (0.47%) and must
    // fall back, while an arstechnica index yields 977 from 59950 (1.63%) and must not. The
    // absolute floor separates those two; the ratio keeps a large-but-minority extraction (bbc at
    // 7.69%) safe; the body floor stops a small page, where a short article is the whole story,
    // from ever being second-guessed.
    private const int MinTrustedExtractionChars = 800;
    private const double MinTrustedExtractionRatio = 0.01;
    private const int MinBodyCharsToDoubt = 10000;

    internal static bool ReadabilityExtractionLooksTruncated(int extractedLength, int bodyLength)
    {
        return extractedLength < MinTrustedExtractionChars
               && bodyLength >= MinBodyCharsToDoubt
               && (double)extractedLength / bodyLength < MinTrustedExtractionRatio;
    }

    private static WebPageMetadata ExtractMetadata(IDocument document)
    {
        string? description = null;
        string? author = null;
        DateOnly? datePublished = null;
        string? siteName = null;

        var metaTags = document.QuerySelectorAll("meta");
        foreach (var meta in metaTags)
        {
            var name = meta.GetAttribute("name")?.ToLowerInvariant();
            var property = meta.GetAttribute("property")?.ToLowerInvariant();
            var content = meta.GetAttribute("content");

            if (string.IsNullOrEmpty(content))
            {
                continue;
            }

            if (name == "description" || property == "og:description")
            {
                description ??= content;
            }
            else if (name == "author")
            {
                author ??= content;
            }
            else if (property == "og:site_name")
            {
                siteName ??= content;
            }
            else if (property == "article:published_time" || name == "date")
            {
                if (DateTime.TryParse(content, out var date))
                {
                    datePublished ??= DateOnly.FromDateTime(date);
                }
            }
        }

        return new WebPageMetadata(description, author, datePublished, siteName);
    }

    private static WebPageMetadata UpdateMetadataFromArticle(WebPageMetadata metadata, Article article)
    {
        return metadata with
        {
            DatePublished = article.PublicationDate.HasValue
                ? DateOnly.FromDateTime(article.PublicationDate.Value)
                : metadata.DatePublished,
            Author = !string.IsNullOrEmpty(article.Author) ? article.Author : metadata.Author,
            SiteName = !string.IsNullOrEmpty(article.SiteName) ? article.SiteName : metadata.SiteName
        };
    }

    private static HtmlProcessingResult CreateSuccessResult(
        int maxLength, int offset, string? title, string content, WebPageMetadata metadata)
    {
        var totalLength = content.Length;

        if (offset > 0 && offset < content.Length)
        {
            content = content[offset..];
        }
        else if (offset >= content.Length)
        {
            content = "";
        }

        // Counted from the offset on, before truncation slices the tail: "beyond the window"
        // promises that paging forward reaches them, so entries already paged past must not be in
        // the count -- they would send the model forward chasing pictures that are behind it.
        var totalImages = PageImageEntry.Count(content);

        var truncated = content.Length > maxLength;
        if (truncated)
        {
            content = HtmlConverter.Truncate(content, maxLength);
        }

        // Strip control characters that cause LLM API 422 errors (keep \t, \n, \r)
        content = ControlCharsRegex().Replace(content, "");

        var hasMore = offset + content.Length < totalLength;

        // Counted off the markdown rather than tracked through the walk: the entry shape is the
        // one both this and truncation agree on, so counting what actually reached the model
        // cannot disagree with what it can read.
        var listed = PageImageEntry.Count(content);

        return new HtmlProcessingResult(
            Title: title,
            Content: content,
            ContentLength: totalLength,
            Truncated: truncated || hasMore,
            Metadata: metadata,
            IsPartial: false,
            ErrorMessage: null
        )
        {
            ImageCount = listed,
            ImagesBeyondWindow = Math.Max(0, totalImages - listed)
        };
    }

    private static HtmlProcessingResult CreatePartialResult(
        string? title, WebPageMetadata metadata, string errorMessage)
    {
        return new HtmlProcessingResult(
            Title: title,
            Content: errorMessage,
            ContentLength: 0,
            Truncated: false,
            Metadata: metadata,
            IsPartial: true,
            ErrorMessage: errorMessage
        );
    }

    // Matches control chars U+0000-U+001F except \t (0x09), \n (0x0A), \r (0x0D)
    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F]")]
    private static partial Regex ControlCharsRegex();
}