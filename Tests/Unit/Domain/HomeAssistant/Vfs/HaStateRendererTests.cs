using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
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

    // HA freezes media_position between state transitions, so the live number comes from Music
    // Assistant's queue instead. When it is supplied it replaces HA's, labelled with its source.
    [Fact]
    public void ToJson_LivePositionSupplied_ReplacesTheStaleOne()
    {
        var entity = Entity("media_player.office", "playing",
            ("media_position", JsonValue.Create(0)),
            ("media_duration", JsonValue.Create(5891)));

        var json = HaStateRenderer.ToJson(entity, new MaQueuePosition
        {
            ElapsedTime = 4243.6,
            LastUpdated = DateTimeOffset.Parse("2026-05-23T09:24:02Z")
        });

        json.ShouldContain("\"media_position\": 4244");
        json.ShouldContain("\"media_position_source\": \"music_assistant\"");
    }

    [Fact]
    public void ToJson_NoLivePosition_LeavesHomeAssistantsValueAlone()
    {
        var entity = Entity("media_player.office", "playing", ("media_position", JsonValue.Create(12)));

        var json = HaStateRenderer.ToJson(entity);

        json.ShouldContain("\"media_position\": 12");
        json.ShouldNotContain("media_position_source");
    }
}