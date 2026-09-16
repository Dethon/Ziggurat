using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

public static class HaStateRenderer
{
    // A file a model reads, not a page a browser renders: an offset's `+` and a name's accent are
    // written as themselves, not as `\u002B` and `\u00E1`.
    private static readonly JsonSerializerOptions _indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public const string PositionKey = "media_position";
    public const string PositionSourceKey = "media_position_source";

    // Overwritten alongside the position: HA's own stamp describes HA's value, and leaving it in
    // place beside a substituted number would date it wrongly.
    public const string PositionUpdatedKey = "media_position_updated_at";

    // The stamps are said on the home's clock (HaDateTimeText.Stamp): Home Assistant's are UTC,
    // and a reader told the local time by its turn prefix takes a raw one as local.
    public static string ToJson(HaEntityState entity, MaQueuePosition? livePosition = null, TimeZoneInfo? homeZone = null)
    {
        var attributes = new JsonObject(
            entity.Attributes
                .OrderBy(a => a.Key, StringComparer.Ordinal)
                .Select(a => new KeyValuePair<string, JsonNode?>(a.Key, a.Value?.DeepClone())));

        ApplyLivePosition(attributes, livePosition, homeZone);

        var root = new JsonObject
        {
            ["entity_id"] = entity.EntityId,
            ["state"] = entity.State
        };
        if (entity.LastChanged is { } changed)
        {
            root["last_changed"] = HaDateTimeText.Stamp(changed, homeZone);
        }
        if (entity.LastUpdated is { } updated)
        {
            root["last_updated"] = HaDateTimeText.Stamp(updated, homeZone);
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
    private static void ApplyLivePosition(JsonObject attributes, MaQueuePosition? livePosition, TimeZoneInfo? homeZone)
    {
        if (livePosition is null)
        {
            return;
        }

        attributes[PositionKey] = JsonValue.Create(Math.Round(livePosition.ElapsedTime));
        attributes[PositionSourceKey] = JsonValue.Create("music_assistant");
        attributes[PositionUpdatedKey] = JsonValue.Create(HaDateTimeText.Stamp(livePosition.LastUpdated, homeZone));
    }
}