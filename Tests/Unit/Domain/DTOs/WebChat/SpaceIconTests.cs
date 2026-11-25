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

    // An app's own drawing carries the same corner badge every app's icon does. It goes in as a picture,
    // never as markup: an image runs no script and loads nothing, and its ids cannot collide with the
    // badge's.
    [Fact]
    public void AnAppsOwnDrawing_IsShownWithTheBadge_AsAPictureNeverAsMarkup()
    {
        var own = "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 10 10\" onload=\"alert(1)\"><rect id=\"cut\" width=\"10\" height=\"10\" fill=\"#123456\"/></svg>"u8.ToArray();

        var svg = XDocument.Parse(SpaceIcon.Badged(own));

        svg.Root!.Name.LocalName.ShouldBe("svg");
        var picture = svg.Descendants().Where(e => e.Name.LocalName == "image").ShouldHaveSingleItem();
        ((string?)picture.Attribute("href")).ShouldBe($"data:image/svg+xml;base64,{Convert.ToBase64String(own)}");
        svg.Descendants().ShouldContain(e => (string?)e.Attribute("fill") == "#16192a");
        svg.Descendants().ShouldNotContain(e => (string?)e.Attribute("fill") == "#123456");
        svg.Descendants().ShouldNotContain(e => e.Attribute("onload") != null);
    }
}