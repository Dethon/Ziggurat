using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

public static class HaStateRenderer
{
    private static readonly JsonSerializerOptions _indented = new() { WriteIndented = true };

    private const string PositionKey = "media_position";
    private const string PositionUpdatedKey = "media_position_updated_at";
    private const string DurationKey = "media_duration";
    private const string ProjectedKey = "media_position_is_projected";
    private const string ReportedKey = "media_position_reported_by_ha";

    public static string ToJson(HaEntityState entity, TimeProvider? timeProvider = null)
    {
        var attributes = new JsonObject(
            entity.Attributes
                .OrderBy(a => a.Key, StringComparer.Ordinal)
                .Select(a => new KeyValuePair<string, JsonNode?>(a.Key, a.Value?.DeepClone())));

        ProjectMediaPosition(entity, attributes, timeProvider ?? TimeProvider.System);

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

    // Home Assistant refreshes `media_position` only on a state transition, stamping
    // `media_position_updated_at` at that moment — it does NOT tick while a track plays. So a read
    // during steady playback returns the position as of when playback started, normally 0. A reader
    // that takes it at face value computes a relative seek ("rewind three minutes") from the wrong
    // origin and clamps to 0, throwing a podcast episode back to its beginning. Only a playing
    // entity is projected: a paused or idle one had its value refreshed by the very transition that
    // stopped it, and advancing that would invent progress nobody heard.
    private static void ProjectMediaPosition(HaEntityState entity, JsonObject attributes, TimeProvider time)
    {
        if (!entity.State.Equals("playing", StringComparison.Ordinal)
            || !TryGetSeconds(attributes, PositionKey, out var position)
            || attributes[PositionUpdatedKey]?.GetValueKind() is not JsonValueKind.String
            || !DateTimeOffset.TryParse(attributes[PositionUpdatedKey]!.GetValue<string>(), out var updatedAt))
        {
            return;
        }

        var elapsed = (time.GetUtcNow() - updatedAt).TotalSeconds;
        if (elapsed <= 0)
        {
            return;
        }

        var projected = position + elapsed;
        if (TryGetSeconds(attributes, DurationKey, out var duration))
        {
            projected = Math.Min(projected, duration);
        }

        attributes[ReportedKey] = JsonValue.Create(position);
        attributes[ProjectedKey] = JsonValue.Create(true);
        attributes[PositionKey] = JsonValue.Create(Math.Round(projected));
    }

    // A number reaches here two ways: parsed from HA's response (a JsonElement, which converts
    // freely) or constructed in a test (a boxed int, which does not). TryGetValue<double> answers
    // false for the latter, so fall back to the invariant text both shapes agree on.
    private static bool TryGetSeconds(JsonObject attributes, string key, out double value)
    {
        value = 0;
        if (attributes[key] is not JsonValue node || node.GetValueKind() is not JsonValueKind.Number)
        {
            return false;
        }

        return node.TryGetValue(out value)
               || double.TryParse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}