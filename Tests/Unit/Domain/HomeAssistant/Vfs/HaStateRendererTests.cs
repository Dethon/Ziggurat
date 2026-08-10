using System.Text.Json.Nodes;
using Domain.Tools.HomeAssistant.Vfs;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaStateRendererTests
{
    [Fact]
    public void ToJson_RendersScalarsAndAttributes_AsIndentedJson()
    {
        var entity = Entity("light.kitchen", "off",
            ("friendly_name", JsonValue.Create("Kitchen")),
            ("brightness", JsonValue.Create((int?)null)),
            ("modes", JsonNode.Parse("""["color_temp","xy"]""")));

        var json = HaStateRenderer.ToJson(entity);

        json.ShouldContain("\"entity_id\": \"light.kitchen\"");
        json.ShouldContain("\"state\": \"off\"");
        json.ShouldContain("\"last_changed\": \"2026-05-23T09:14:02");
        json.ShouldContain("\"attributes\": {");
        json.ShouldContain("\"brightness\": null");
        json.ShouldContain("\"friendly_name\": \"Kitchen\"");
        json.ShouldContain("\"color_temp\"");
        // Indented (2-space) pretty-print, not a single compact line.
        json.ShouldContain("\n  \"entity_id\"");
    }

    [Fact]
    public void ToJson_NoAttributes_EmitsEmptyObject()
    {
        HaStateRenderer.ToJson(Entity("sun.sun", "above_horizon"))
            .ShouldContain("\"attributes\": {}");
    }
}