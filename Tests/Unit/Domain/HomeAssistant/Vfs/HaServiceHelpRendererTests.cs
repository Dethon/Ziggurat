using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaServiceHelpRendererTests
{
    private static HaServiceField Field(string? desc, bool required, JsonNode? selector) =>
        new() { Description = desc, Required = required, Selector = selector };

    [Fact]
    public void Render_HeaderFieldsAndTypes()
    {
        var svc = Service("light", "turn_on", AnyEntityTarget(),
            ("brightness_pct", Field("Brightness", false, JsonNode.Parse("""{"number":{"min":1,"max":100}}"""))),
            ("flash", Field(null, false, JsonNode.Parse("""{"select":{"options":["short","long"]}}"""))));

        var help = HaServiceHelpRenderer.Render("light.kitchen", svc);

        help.ShouldContain("turn_on.sh — call light.turn_on on light.kitchen");
        help.ShouldContain("--brightness_pct");
        help.ShouldContain("1-100");
        help.ShouldContain("--flash");
        help.ShouldContain("short");
    }

    [Fact]
    public void Render_NoFields_SaysNoArguments()
    {
        HaServiceHelpRenderer.Render("light.kitchen", Service("light", "toggle", AnyEntityTarget()))
            .ShouldContain("(no arguments)");
    }

    [Fact]
    public void Render_AreaSelector_FlagsSlugType()
    {
        var svc = Service("vacuum", "clean_segment", AnyEntityTarget(),
            ("cleaning_area_id", Field("Area to clean", true, JsonNode.Parse("""{"area":{}}"""))));

        var help = HaServiceHelpRenderer.Render("vacuum.roborock", svc);

        help.ShouldContain("--cleaning_area_id");
        help.ShouldContain("AREA_ID");
        help.ShouldContain("slug");
        // The one moment the model reliably looks is this help line, so it says where the slug
        // is read from — armed runs showed the prompt's own version of the rule skipped one run
        // in three, the model passing a slugified synonym of the user's words instead.
        help.ShouldContain("setup index");
        help.ShouldContain("never derived");
    }

    [Fact]
    public void Render_ObjectSelector_SaysTextOrJson()
    {
        var svc = Service("music_assistant", "play_media", AnyEntityTarget(),
            ("media_id", Field(null, true, JsonNode.Parse("""{"object":{"multiple":false}}"""))));

        HaServiceHelpRenderer.Render("media_player.office", svc).ShouldContain("TEXT or JSON");
    }
}