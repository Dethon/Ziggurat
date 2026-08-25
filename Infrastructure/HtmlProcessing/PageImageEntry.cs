using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace Infrastructure.HtmlProcessing;

// What a browsed page says about a picture, in the place the picture sits. One shape, known here
// and nowhere else: the converter writes it, truncation refuses to split it, and view_image reads
// the ref back off it.
public static partial class PageImageEntry
{
    public const string RefPrefix = "i-";

    private const string Open = "[image ";
    private const char Close = ']';

    // A hundred pixels on either side. Below it an image is a spacer, an icon, a tracking pixel or
    // a bullet, and a catalogue where nine entries in ten are 1x1 is a catalogue nobody reads.
    public const int MinRenderedSide = 100;

    // The dimensions the page measured, stamped before extraction. Markup width/height is not
    // consulted: it lies as often as it is absent.
    public const string WidthAttribute = "data-img-w";
    public const string HeightAttribute = "data-img-h";

    // An entry is read as a menu item, not as prose. Past this the label stops helping the model
    // choose and only costs the browse -- every browse, whether or not it ever fetches.
    private const int MaxLabelLength = 120;

    public static string Write(int number, string label) => $"{Open}{RefPrefix}{number}: {label}{Close}";

    public static string RefFor(int number) => $"{RefPrefix}{number}";

    public static bool IsImageRef(string? candidate) =>
        candidate is not null && ImageRefRegex().IsMatch(candidate);

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

    // Where a body cut short must back up to, so it never ends mid-entry. -1 when the tail carries
    // no partial entry at all.
    public static int PartialEntryStart(string text)
    {
        var open = text.LastIndexOf(Open, StringComparison.Ordinal);
        return open >= 0 && text.IndexOf(Close, open) < 0 ? open : -1;
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

    [GeneratedRegex(@"^i-\d+$")]
    private static partial Regex ImageRefRegex();

    [GeneratedRegex(@"\[image i-\d+: [^\]]*\]")]
    private static partial Regex EntryRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^[^\s/]+\.[A-Za-z0-9]{1,5}$")]
    private static partial Regex FileNameRegex();
}