using System.Reflection;
using System.Text.RegularExpressions;
using Shouldly;
using WebChat.Client.Components;

namespace Tests.Unit.WebChat.Client.Components;

// An id inside a glyph is looked up across the whole document, so two icons drawn from the same
// glyph share whichever definition comes first, and the second loses its paint when the first is
// removed. Every id a glyph declares, and every reference to one, carries the wrapper's
// per-instance token, which the wrapper replaces with a value of its own for each rendered icon.
public sealed class IconGlyphIdTests
{
    private static readonly Regex _id = new(@"\bid=""([^""]+)""");
    private static readonly Regex _reference = new(@"url\(#([^)]+)\)");

    public static IEnumerable<object[]> Glyphs => typeof(Icons)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(IconGlyph))
        .Select(f => new object[] { f.Name, ((IconGlyph)f.GetValue(null)!).Body });

    [Theory]
    [MemberData(nameof(Glyphs))]
    public void EveryIdAGlyphDeclaresOrReferences_CarriesThePerInstanceToken(string name, string body)
    {
        var ids = _id.Matches(body).Select(m => m.Groups[1].Value)
            .Concat(_reference.Matches(body).Select(m => m.Groups[1].Value))
            .ToList();

        ids.ShouldAllBe(id => id.EndsWith("-__icon__"), $"{name} declares or references an id without the token");
    }
}