using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

public static class HaStateRenderer
{
    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true };

    public const string PositionKey = "media_position";
    public const string PositionSourceKey = "media_position_source";

    // Overwritten alongside the position: HA's own stamp describes HA's value, and leaving it in
    // place beside a substituted number would date it wrongly.
    public const string PositionUpdatedKey = "media_position_updated_at";

    public static string ToJson(HaEntityState entity, MaQueuePosition? livePosition = null)
    {
        var attributes = new JsonObject(
            entity.Attributes
                .OrderBy(a => a.Key, StringComparer.Ordinal)
                .Select(a => new KeyValuePair<string, JsonNode?>(a.Key, a.Value?.DeepClone())));

        ApplyLivePosition(attributes, livePosition);

        var root = new JsonObject
        {
            ["entity_id"] = entity.EntityId,
            ["state"] = entity.State
        };
        if (entity.LastChanged is { } changed)
        {
            root["last_changed"] = changed.ToString("O");
        }
        if (entity.LastUpdated is { } updated)
        {
            root["last_updated"] = updated.ToString("O");
        }
        root["attributes"] = attributes;

        return root.ToJsonString(_indented);
    }

    // Home Assistant refreshes `media_position` only on a state transition — it does not tick while
    // a track plays — so a read during steady playback returns the position as of when playback
    // started, normally 0. A reader that takes it at face value computes a relative seek from the
    // wrong origin and clamps to the start of the episode. Music Assistant's queue keeps the real
    // number, so when it answers for this player its value replaces HA's stale one, labelled so the
    // reader knows which of the two it got.
    private static void ApplyLivePosition(JsonObject attributes, MaQueuePosition? livePosition)
    {
        if (livePosition is null)
        {
            return;
        }

        attributes[PositionKey] = JsonValue.Create(Math.Round(livePosition.ElapsedTime));
        attributes[PositionSourceKey] = JsonValue.Create("music_assistant");
        attributes[PositionUpdatedKey] = JsonValue.Create(livePosition.LastUpdated.ToString("O"));
    }
}