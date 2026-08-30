using System.Text.RegularExpressions;

namespace Infrastructure.HtmlProcessing;

// The strings a page offers about one picture, and the dimensions it rendered at. One record,
// two builders: the extraction side fills it from the parsed page's elements, the fetch side
// from the probe's answer -- so the ladder below can be the only thing that turns either into
// a label.
public record ImageLabelFacts(
    string? Alt,
    string? Caption,
    string? Title,
    string? LinkText,
    string? Src,
    int Width,
    int Height);

// The one label ladder. It used to exist twice -- once here, once in the fetch script's
// JavaScript -- and the two engines disagreed about whitespace, anchoring and the cut without
// any commit choosing to. Now the fetch script answers only facts, and every label the model
// ever reads -- the entry's and the note's -- comes out of this function.
public static partial class ImageLabel
{
    // A safeguard against a pathological attribute, not an editor: a carefully written alt runs
    // a few hundred characters and is exactly what the model picks a picture by, so a real label
    // should never meet this.
    private const int MaxLength = 500;

    // Falls back on blank, not on falsy: a whitespace-only rung once short-circuited every rung
    // after it to an empty label. Dimensions close the ladder, so a picture with nothing to say
    // is still named by its size rather than by nothing.
    public static string From(ImageLabelFacts facts) =>
        Sanitize(
            NonEmpty(facts.Alt)
            ?? NonEmpty(facts.Caption)
            ?? NonEmpty(facts.Title)
            ?? NonEmpty(facts.LinkText)
            ?? FileName(facts.Src)
            ?? $"{facts.Width}x{facts.Height}");

    // A name only where the address actually carries one. A data URI has no filename, and the
    // tail of its payload read as one is worse than no label at all.
    private static string? FileName(string? src)
    {
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
        return flat.Length <= MaxLength ? flat : flat[..MaxLength].TrimEnd() + "…";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^[^\s/]+\.[A-Za-z0-9]{1,5}$")]
    private static partial Regex FileNameRegex();
}