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

    // HA refreshes media_position only on a state transition, stamping media_position_updated_at at
    // that moment. During steady playback the field stays frozen at whatever it was when playback
    // started — normally 0 — so a reader that takes it literally computes a relative seek from the
    // wrong origin and clamps to the start of the episode. Project it forward for a playing entity.
    [Fact]
    public void ToJson_PlayingMedia_ProjectsPositionToNow()
    {
        var entity = Playing(4243, "2026-05-23T09:14:02Z");

        var json = HaStateRenderer.ToJson(entity, At("2026-05-23T09:24:02Z"));

        // 4243 + 600s elapsed.
        json.ShouldContain("\"media_position\": 4843");
    }

    [Fact]
    public void ToJson_PlayingMedia_MarksPositionAsProjected()
    {
        var json = HaStateRenderer.ToJson(Playing(10, "2026-05-23T09:14:02Z"), At("2026-05-23T09:14:12Z"));

        // The reader must be able to tell a computed value from HA's stored one.
        json.ShouldContain("\"media_position_is_projected\": true");
        json.ShouldContain("\"media_position_reported_by_ha\": 10");
    }

    [Fact]
    public void ToJson_PausedMedia_LeavesPositionUntouched()
    {
        // A paused/idle entity's stored position is already correct as of the transition that set
        // it; projecting it forward would invent progress that never played.
        var entity = Entity("media_player.office", "paused",
            ("media_position", JsonValue.Create(71)),
            ("media_position_updated_at", JsonValue.Create("2026-05-23T09:14:02Z")));

        var json = HaStateRenderer.ToJson(entity, At("2026-05-23T09:24:02Z"));

        json.ShouldContain("\"media_position\": 71");
        json.ShouldNotContain("media_position_is_projected");
    }

    [Fact]
    public void ToJson_PlayingBeyondDuration_ClampsToDuration()
    {
        var entity = Entity("media_player.office", "playing",
            ("media_duration", JsonValue.Create(100)),
            ("media_position", JsonValue.Create(90)),
            ("media_position_updated_at", JsonValue.Create("2026-05-23T09:14:02Z")));

        var json = HaStateRenderer.ToJson(entity, At("2026-05-23T09:24:02Z"));

        json.ShouldContain("\"media_position\": 100");
    }

    [Fact]
    public void ToJson_PlayingWithoutUpdatedAt_LeavesPositionUntouched()
    {
        // Nothing to project from; inventing an origin would be worse than the stale value.
        var entity = Entity("media_player.office", "playing", ("media_position", JsonValue.Create(12)));

        var json = HaStateRenderer.ToJson(entity, At("2026-05-23T09:24:02Z"));

        json.ShouldContain("\"media_position\": 12");
        json.ShouldNotContain("media_position_is_projected");
    }

    [Fact]
    public void ToJson_NonMediaEntity_Unaffected()
    {
        var json = HaStateRenderer.ToJson(Entity("light.kitchen", "on"), At("2026-05-23T09:24:02Z"));

        json.ShouldNotContain("media_position");
    }

    private static HaEntityState Playing(int position, string updatedAt) =>
        Entity("media_player.office", "playing",
            ("media_duration", JsonValue.Create(5891)),
            ("media_position", JsonValue.Create(position)),
            ("media_position_updated_at", JsonValue.Create(updatedAt)));

    private static TimeProvider At(string instant)
    {
        var t = new FakeTimeProvider();
        t.SetUtcNow(DateTimeOffset.Parse(instant));
        return t;
    }
}