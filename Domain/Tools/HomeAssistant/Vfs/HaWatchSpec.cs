using System.Text.Json;
using System.Text.Json.Nodes;

namespace Domain.Tools.HomeAssistant.Vfs;

// What the agent writes to `/ha/watches/<id>/watch.json`: a human name, Home Assistant's own
// triggers and conditions passed through as written, and the ordered effects. Parsing names the
// field it refused, because the agent fixes the file from the message in the same turn.
public sealed class HaWatchSpecException(string message) : Exception(message);

public abstract record HaWatchEffect
{
    public const string PromptKind = "prompt";
    public const string AnnounceKind = "announce";
    public const string ActionsKind = "actions";

    public abstract string Kind { get; }

    // The effect exactly as it was authored: what the automation's description keeps and what
    // watch.json reads back.
    public abstract JsonObject ToJson();

    public static HaWatchEffect Parse(JsonNode? node, string where)
    {
        if (node is not JsonObject effect)
        {
            throw new HaWatchSpecException($"{where} must be an object with a kind");
        }

        var kind = StringOf(effect, "kind", where);
        return kind switch
        {
            PromptKind => new HaPromptEffect(RequiredText(effect, "prompt", where)),
            AnnounceKind => new HaAnnounceEffect(
                RequiredText(effect, "text", where),
                effect["target"] as JsonObject ?? throw new HaWatchSpecException(
                    $"{where}.target is required: an object with satelliteId, satelliteIds, room or all"),
                effect["insistent"] switch
                {
                    null => null,
                    JsonObject insistent => insistent.DeepClone().AsObject(),
                    _ => throw new HaWatchSpecException($"{where}.insistent must be an object (use {{}} for the defaults)")
                }),
            ActionsKind => new HaActionsEffect(
                effect["actions"] is JsonArray { Count: > 0 } actions
                    ? actions.DeepClone().AsArray()
                    : throw new HaWatchSpecException($"{where}.actions must be a non-empty list of Home Assistant actions")),
            _ => throw new HaWatchSpecException(
                $"{where}.kind '{kind}' is not one of {PromptKind}, {AnnounceKind}, {ActionsKind}")
        };
    }

    private static string StringOf(JsonObject effect, string field, string where) =>
        effect[field] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : throw new HaWatchSpecException($"{where}.{field} is required");

    private static string RequiredText(JsonObject effect, string field, string where)
    {
        var text = StringOf(effect, field, where);
        return string.IsNullOrWhiteSpace(text)
            ? throw new HaWatchSpecException($"{where}.{field} must not be empty")
            : text;
    }
}

public sealed record HaPromptEffect(string Prompt) : HaWatchEffect
{
    public override string Kind => PromptKind;

    public override JsonObject ToJson() => new() { ["kind"] = Kind, ["prompt"] = Prompt };
}

public sealed record HaAnnounceEffect(string Text, JsonObject Target, JsonObject? Insistent) : HaWatchEffect
{
    public override string Kind => AnnounceKind;

    public override JsonObject ToJson()
    {
        var json = new JsonObject { ["kind"] = Kind, ["text"] = Text, ["target"] = Target.DeepClone() };
        if (Insistent is not null)
        {
            json["insistent"] = Insistent.DeepClone();
        }
        return json;
    }
}

public sealed record HaActionsEffect(JsonArray Actions) : HaWatchEffect
{
    public override string Kind => ActionsKind;

    public override JsonObject ToJson() => new() { ["kind"] = Kind, ["actions"] = Actions.DeepClone() };
}

public sealed record HaWatchSpec
{
    private static readonly string[] _fields =
        ["name", "triggers", "conditions", "effects", "once", "enabled", "deliverTo", "userId"];

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public required string Name { get; init; }
    public required JsonArray Triggers { get; init; }
    public JsonArray? Conditions { get; init; }
    public required IReadOnlyList<HaWatchEffect> Effects { get; init; }
    public bool Once { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<string>? DeliverTo { get; init; }
    public string? UserId { get; init; }

    public static HaWatchSpec Parse(string content)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(content);
        }
        catch (JsonException ex)
        {
            throw new HaWatchSpecException($"watch.json is not valid JSON: {ex.Message}");
        }

        if (root is not JsonObject watch)
        {
            throw new HaWatchSpecException("watch.json must be a JSON object");
        }

        var unknown = watch.Select(field => field.Key).Where(key => !_fields.Contains(key, StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
        {
            throw new HaWatchSpecException(
                $"unknown field '{unknown[0]}'; the fields are {string.Join(", ", _fields)}");
        }

        var effects = watch["effects"] is JsonArray { Count: > 0 } list
            ? list.Select((effect, index) => HaWatchEffect.Parse(effect, $"effects[{index}]")).ToList()
            : throw new HaWatchSpecException("effects must be a non-empty list, each {kind: prompt|announce|actions, ...}");

        return new HaWatchSpec
        {
            Name = watch["name"] is JsonValue name && name.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                ? text
                : throw new HaWatchSpecException("name is required"),
            Triggers = watch["triggers"] is JsonArray { Count: > 0 } triggers
                ? triggers.DeepClone().AsArray()
                : throw new HaWatchSpecException("triggers must be a non-empty list of Home Assistant triggers"),
            Conditions = watch["conditions"] switch
            {
                null => null,
                JsonArray { Count: 0 } => null,
                JsonArray conditions => conditions.DeepClone().AsArray(),
                _ => throw new HaWatchSpecException("conditions must be a list of Home Assistant conditions")
            },
            Effects = effects,
            Once = Flag(watch, "once", false),
            Enabled = Flag(watch, "enabled", true),
            DeliverTo = watch["deliverTo"] switch
            {
                null => null,
                JsonArray targets when targets.All(t => t is JsonValue v && v.TryGetValue<string>(out _)) =>
                    targets.Select(t => t!.GetValue<string>()).ToList(),
                _ => throw new HaWatchSpecException("deliverTo must be a list of channel ids, e.g. [\"telegram\"] or [\"voice:office-01\"]")
            },
            UserId = watch["userId"] switch
            {
                null => null,
                JsonValue user when user.TryGetValue<string>(out var id) => id,
                _ => throw new HaWatchSpecException("userId must be a string")
            }
        };
    }

    private static bool Flag(JsonObject watch, string field, bool fallback) =>
        watch[field] switch
        {
            null => fallback,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            _ => throw new HaWatchSpecException($"{field} must be true or false")
        };

    // The file as the agent reads it back: every field it may write, in the order the guide lists
    // them, with the optional ones present as null so an edit can find them.
    public string ToJson() => new JsonObject
    {
        ["name"] = Name,
        ["triggers"] = Triggers.DeepClone(),
        ["conditions"] = Conditions?.DeepClone() ?? new JsonArray(),
        ["effects"] = new JsonArray([.. Effects.Select(e => (JsonNode)e.ToJson())]),
        ["once"] = Once,
        ["enabled"] = Enabled,
        ["deliverTo"] = DeliverTo is null ? null : new JsonArray([.. DeliverTo.Select(d => (JsonNode)d)]),
        ["userId"] = UserId
    }.ToJsonString(_json);
}