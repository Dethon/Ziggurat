using Shouldly;
using WebChat.Client.Components.Chat;

namespace Tests.Unit.WebChat.Client.Components;

// Any answer that is one JSON object — a structured format the chat has no view of its own for — is
// drawn as an outline: its keys as labels, its lists as rows, empty and null parts left out.
public class JsonOutlineTests
{
    [Fact]
    public void AnObject_IsAnOutlineOfItsFields()
    {
        var outline = JsonOutline.Parse("""{"items":[{"name":"pasta","carbsPer100g":28,"note":null}],"notes":[],"done":true}""");

        outline.ShouldNotBeNull();
        outline.Select(n => n.Label).ShouldBe(["Items", "Done"]);
        var item = outline[0].Children.ShouldHaveSingleItem();
        item.Children.Select(c => (c.Label, c.Value)).ShouldBe([("Name", "pasta"), ("Carbs per 100g", "28")]);
        outline[1].Value.ShouldBe("yes");
    }

    [Theory]
    [InlineData("Lisboa.")]
    [InlineData("{ not json")]
    [InlineData("[1, 2]")]
    [InlineData("\"a string\"")]
    public void AnythingButAnObject_IsNoOutline(string content) => JsonOutline.Parse(content).ShouldBeNull();
}