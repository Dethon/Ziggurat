using System.Xml.Linq;
using Domain.DTOs.WebChat;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.WebChat;

// A space's home screen icon and a built app's are one drawing, filled with the colour each was
// given: the chat client's host and the apps server both serve it.
public class SpaceIconTests
{
    [Fact]
    public void TheIcon_IsFilledWithTheColourGiven()
    {
        var svg = XDocument.Parse(SpaceIcon.Svg("#e9601f"));

        svg.Root!.Name.LocalName.ShouldBe("svg");
        svg.Descendants().ShouldContain(e => (string?)e.Attribute("fill") == "#e9601f");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("red")]
    [InlineData("#e9601f\" onload=\"alert(1)")]
    public void AnythingButAHexColour_DrawsTheDefaultAccent(string? color)
    {
        var svg = XDocument.Parse(SpaceIcon.Svg(color));

        svg.Descendants().ShouldContain(e => (string?)e.Attribute("fill") == SpaceConfig.DefaultAccentColor);
        svg.Descendants().ShouldNotContain(e => e.Attribute("onload") != null);
    }
}