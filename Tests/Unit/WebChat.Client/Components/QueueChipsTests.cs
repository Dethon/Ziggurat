using System.Reflection;
using System.Text.RegularExpressions;
using Shouldly;
using Tests.E2E.Fixtures;
using WebChat.Client.Components;

namespace Tests.Unit.WebChat.Client.Components;

// A chip is one control, and its actions are stroked glyphs, so whatever else it draws is stroked too:
// a filled glyph beside them reads as a rendering bug (blazor-client-ui.md). Read off the markup, the
// only place the pairing is visible.
public sealed class QueueChipsTests
{
    private static readonly Regex _glyph = new(@"Glyph=""Icons\.(\w+)""");

    [Fact]
    public void EveryIconOnAChip_IsAStrokedGlyph()
    {
        var markup = File.ReadAllText(Path.Combine(
            TestHelpers.FindSolutionRoot(), "WebChat.Client", "Components", "QueueChips.razor"));

        var names = _glyph.Matches(markup).Select(m => m.Groups[1].Value).Distinct().ToList();

        names.ShouldNotBeEmpty();
        names.Where(name => Glyph(name).StrokeWidth is null).ShouldBeEmpty("filled glyphs on a queue chip");
    }

    private static IconGlyph Glyph(string name) =>
        (IconGlyph)(typeof(Icons).GetField(name, BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException($"no glyph Icons.{name}"));
}