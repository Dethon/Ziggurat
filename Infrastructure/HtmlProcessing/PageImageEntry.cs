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

    public static string Write(string imageRef, string label) => $"{Open}{imageRef}: {label}{Close}";

    public static string RefFor(int number) => ImageRef.For(number);

    // Null when the image is page furniture rather than content. The label itself comes from the
    // one ladder, over facts read off the parsed page -- the same ladder the fetch side names
    // the note with, which is what keeps the two speaking the same words.
    public static string? LabelFor(IHtmlImageElement img) =>
        Survives(img) ? ImageLabel.From(FactsFor(img)) : null;

    private static ImageLabelFacts FactsFor(IHtmlImageElement img) => new(
        Alt: img.GetAttribute("alt"),
        Caption: img.Closest("figure")?.QuerySelector("figcaption")?.TextContent,
        Title: img.GetAttribute("title"),
        LinkText: img.Closest("a")?.TextContent,
        Src: img.GetAttribute("src"),
        Width: Side(img, WidthAttribute),
        Height: Side(img, HeightAttribute));

    // The entry a picture gets, or null when it gets none. The stamped ref is the only source of
    // an entry's ref: the fetch resolves refs against the live DOM, so a number invented here --
    // the old fallback counter, which only hand-written test markup ever took -- would be a
    // handle the model acts on and is refused by. An unstamped picture is a non-survivor.
    public static string? EntryFor(IHtmlImageElement img) =>
        LabelFor(img) is { } label && img.GetAttribute(RefAttribute) is { Length: > 0 } stamped
            ? Write(stamped, label)
            : null;

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

    [GeneratedRegex(@"\[image i-\d+: [^\]]*\]")]
    private static partial Regex EntryRegex();

    // The last entry opening in the text, whether or not it ever closes.
    [GeneratedRegex(@"\[image i-\d+:", RegexOptions.RightToLeft)]
    private static partial Regex PartialOpenRegex();
}