using System.Text;
using System.Text.RegularExpressions;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit;

// An icon in either Blazor client is drawn, never typed. A font glyph is rendered by whatever the
// platform picked for it, so it arrives at a different weight and often a different colour from the
// SVG icons sitting beside it, and an emoji arrives in full colour however the control is styled.
// The convention was written down and forgotten anyway, so it is walked here rather than
// remembered: the markup is the only place the mistake is visible, and it is invisible on the
// machine that makes it.
public class DrawnIconConformanceTests
{
    private static readonly string[] _clients = ["WebChat.Client", "Dashboard.Client"];

    // The one exemption, named rather than pattern-matched, so removing it is a decision. The
    // suggestion chips are content a person reads rather than controls, and their emoji are the
    // point of them.
    private static readonly string[] _exempt = ["SuggestionChips.razor"];

    // A right arrow inside a sentence is punctuation, not an icon: the dashboard labels a span by
    // its two ends ("speech end → audio"), and drawing that would be absurd. It is the only
    // character above the threshold that reads as prose here.
    private const int RightArrow = 0x2192;

    private static readonly Regex _entity = new(@"&(#[0-9]+|#x[0-9a-fA-F]+|[a-zA-Z]+);");

    // The named entities that spell an icon. The escaping ones (amp, lt, gt, quot, apos, nbsp)
    // resolve below the threshold and need no entry.
    private static readonly Dictionary<string, int> _namedGlyphs = new()
    {
        ["times"] = 0x00D7,
        ["divide"] = 0x00F7,
        ["larr"] = 0x2190,
        ["rarr"] = 0x2192,
        ["uarr"] = 0x2191,
        ["darr"] = 0x2193,
        ["check"] = 0x2713
    };

    [Fact]
    public void EveryIconInTheMarkup_IsDrawnRatherThanTyped()
    {
        var offenders = MarkupFiles()
            .SelectMany(GlyphsIn)
            .ToList();

        offenders.ShouldBeEmpty(
            "An icon is inline SVG here, on the convention ChatInput.razor already follows. "
            + "These are font glyphs:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    private static IEnumerable<string> MarkupFiles() =>
        _clients
            .SelectMany(client => Directory.EnumerateFiles(
                Path.Combine(TestHelpers.FindSolutionRoot(), client),
                "*.razor",
                SearchOption.AllDirectories))
            .Where(f => !_exempt.Contains(Path.GetFileName(f)))
            .Order();

    private static IEnumerable<string> GlyphsIn(string file) =>
        File.ReadAllLines(file)
            .SelectMany((line, index) => CodePointsIn(line)
                .Where(IsIcon)
                .Select(cp => $"{Path.GetFileName(file)}:{index + 1} U+{cp:X4} {char.ConvertFromUtf32(cp)}"));

    // Literal characters and the entities that stand for one, read the same way: what the
    // browser ends up drawing is the same either way, and both spellings are in use here.
    private static IEnumerable<int> CodePointsIn(string line) =>
        line.EnumerateRunes().Select(r => r.Value)
            .Concat(_entity.Matches(line).Select(m => Resolve(m.Groups[1].Value)));

    private static int Resolve(string entity) => entity switch
    {
        ['#', 'x', .. var hex] => Convert.ToInt32(hex, 16),
        ['#', .. var digits] => int.Parse(digits),
        var named => _namedGlyphs.GetValueOrDefault(named, 0)
    };

    // Everything a keyboard and ordinary typography reach stays below the threshold: the dashes,
    // the quotes and the ellipsis are all under U+2190. Above it are the arrows, the geometric
    // shapes, the dingbats and the emoji — the ranges an icon gets picked from. The multiplication
    // sign is the one straggler below, because a close button is written as one.
    private static bool IsIcon(int codePoint) =>
        codePoint != RightArrow && (codePoint == 0x00D7 || codePoint >= 0x2190);
}
