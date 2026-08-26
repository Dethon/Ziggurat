using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Domain.Tools.Web;

namespace Infrastructure.HtmlProcessing;

// What a browsed page says about a picture, in the place the picture sits. One shape, known here
// and nowhere else: the converter writes it, truncation refuses to split it, and view_image reads
// the ref back off it.
public static partial class PageImageEntry
{
    private const string Open = "[image ";
    private const char Close = ']';

    // A hundred pixels on either side. Below it an image is a spacer, an icon, a tracking pixel or
    // a bullet, and a catalogue where nine entries in ten are 1x1 is a catalogue nobody reads.
    public const int MinRenderedSide = 100;

    // The dimensions the page measured, stamped before extraction. Markup width/height is not
    // consulted: it lies as often as it is absent.
    public const string WidthAttribute = "data-img-w";
    public const string HeightAttribute = "data-img-h";

    // The ref the page stamped while it measured. Read rather than re-derived, so the handle the
    // model sees is the one the live DOM answers to -- a counter here could drift from the page's
    // the moment either side's traversal differed.
    public const string RefAttribute = "data-img-ref";

    // An entry is read as a menu item, not as prose. Past this the label stops helping the model
    // choose and only costs the browse -- every browse, whether or not it ever fetches.
    private const int MaxLabelLength = 120;

    public static string Write(string imageRef, string label) => $"{Open}{imageRef}: {label}{Close}";

    public static string RefFor(int number) => ImageRef.For(number);

    // Null when the image is page furniture rather than content.
    public static string? LabelFor(IHtmlImageElement img)
    {
        if (!Survives(img))
        {
            return null;
        }

        var label = FirstSpoken(img) ?? Dimensions(img);
        return Sanitize(label);
    }

    // The ref the page assigned, where it assigned one. Falls back to the extractor's own count so
    // hand-written HTML -- every unit test, and any path that never met a browser -- still lists
    // its images rather than going silent.
    public static string RefOn(IElement img, int fallbackNumber) =>
        img.GetAttribute(RefAttribute) is { Length: > 0 } stamped ? stamped : RefFor(fallbackNumber);

    // Where a body cut short must back up to, so it never ends mid-entry. -1 when the tail carries
    // no partial entry at all.
    //
    // Matched against the entry's own opening shape rather than the bare "[image " literal: prose
    // that happens to contain those words is page text and must not be trimmed for it.
    public static int PartialEntryStart(string text)
    {
        var open = PartialOpenRegex().Match(text);
        return open.Success && text.IndexOf(Close, open.Index) < 0 ? open.Index : DanglingOpenStart(text);
    }

    // "[image i-" and whatever digits follow, before the colon that would let the opening regex
    // see it. A tail cut there is worse than a half-written label: "[image i-12" out of
    // "[image i-123: ..." reads as a plausible ref that names a different picture.
    private const string OpenWithRef = $"{Open}{ImageRef.Prefix}";

    private static int DanglingOpenStart(string text)
    {
        var last = text.LastIndexOf('[');
        if (last < 0 || text.IndexOf(Close, last) >= 0)
        {
            return -1;
        }

        var tail = text[last..];
        var isPartialOpen = tail.Length <= OpenWithRef.Length
            ? OpenWithRef.StartsWith(tail, StringComparison.Ordinal)
            : tail.StartsWith(OpenWithRef, StringComparison.Ordinal)
              && tail[OpenWithRef.Length..].All(char.IsAsciiDigit);
        return isPartialOpen ? last : -1;
    }

    public static int Count(string text) => EntryRegex().Count(text);

    private static bool Survives(IHtmlImageElement img) =>
        Side(img, WidthAttribute) >= MinRenderedSide && Side(img, HeightAttribute) >= MinRenderedSide;

    private static int Side(IElement img, string attribute) =>
        int.TryParse(img.GetAttribute(attribute), out var side) ? side : 0;

    private static string? FirstSpoken(IHtmlImageElement img) =>
        NonEmpty(img.GetAttribute("alt"))
        ?? NonEmpty(img.Closest("figure")?.QuerySelector("figcaption")?.TextContent)
        ?? NonEmpty(img.GetAttribute("title"))
        ?? NonEmpty(img.Closest("a")?.TextContent)
        ?? FileName(img);

    private static string Dimensions(IHtmlImageElement img) =>
        $"{Side(img, WidthAttribute)}x{Side(img, HeightAttribute)}";

    // A name only where the address actually carries one. A data URI has no filename, and the
    // tail of its payload read as one is worse than no label at all.
    private static string? FileName(IElement img)
    {
        var src = img.GetAttribute("src");
        if (string.IsNullOrWhiteSpace(src) || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = src.Split('?', '#')[0];
        var name = NonEmpty(path[(path.LastIndexOf('/') + 1)..]);
        return name is not null && FileNameRegex().IsMatch(name) ? name : null;
    }

    private static string? NonEmpty(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    // A label is somebody else's text sitting inside a delimited entry, so it may not carry the
    // delimiters: a bracket or a newline in an alt attribute would otherwise end the entry early
    // and hand the model a ref it cannot use.
    private static string Sanitize(string label)
    {
        var flat = WhitespaceRegex().Replace(label, " ").Replace('[', '(').Replace(']', ')').Trim();
        return flat.Length <= MaxLabelLength ? flat : flat[..MaxLabelLength].TrimEnd() + "…";
    }

    [GeneratedRegex(@"\[image i-\d+: [^\]]*\]")]
    private static partial Regex EntryRegex();

    // The last entry opening in the text, whether or not it ever closes.
    [GeneratedRegex(@"\[image i-\d+:", RegexOptions.RightToLeft)]
    private static partial Regex PartialOpenRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^[^\s/]+\.[A-Za-z0-9]{1,5}$")]
    private static partial Regex FileNameRegex();
}