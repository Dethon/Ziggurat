using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Domain.Tools.Sugary.Watches;

// What the agent writes to `/watchers/<id>/watch.json` when a watch is about somebody's glucose: the
// home watch's shape (name, triggers, conditions, effects, once, enabled, deliverTo, userId), with
// triggers Sugary judges itself — a threshold, a rate, a forecast, the sensor's state — in mg/dL.
// Parsing names the field it refused, because the agent fixes the file from the message in the same
// turn; anything this file does not know is refused rather than kept and ignored, since a watch that
// silently drops a word is a watch that never fires the way it was asked.
public sealed class GlucoseWatchSpecException(string message) : Exception(message);

public enum GlucoseBound
{
    Below,
    Above
}

public enum GlucoseTrendDirection
{
    Falling,
    Rising
}

public enum SensorState
{
    NoReadings,
    LinkFailing,
    Ending
}

public abstract record GlucoseTrigger
{
    public const string GlucoseKind = "glucose";
    public const string TrendKind = "glucose_trend";
    public const string ForecastKind = "glucose_forecast";
    public const string SensorKind = "sensor";

    // The trigger kinds Sugary keeps watches for: what its member of /watchers claims.
    public static readonly IReadOnlyList<string> Kinds = [GlucoseKind, TrendKind, ForecastKind, SensorKind];

    // Whose sensor: a people/ folder name, or null for whoever is writing (the caller, or the watch's own
    // subject when the write replaces one).
    public string? Person { get; init; }

    public abstract string Kind { get; }

    public JsonObject ToJson()
    {
        var json = new JsonObject { ["trigger"] = Kind };
        foreach (var (field, value) in Fields())
        {
            json[field] = value;
        }

        if (Person is not null)
        {
            json["person"] = Person;
        }

        return json;
    }

    protected abstract IEnumerable<(string Field, JsonNode? Value)> Fields();

    // A level outside what any sensor reads is a value in the wrong unit: 3.9 is a low in mmol/L and,
    // read as mg/dL, an alarm that can never ring.
    internal const double LowestLevel = 20;
    internal const double HighestLevel = 600;

    public static GlucoseTrigger Parse(JsonNode? node, string where)
    {
        if (node is not JsonObject trigger)
        {
            throw new GlucoseWatchSpecException($"{where} must be an object with a trigger");
        }

        var kind = trigger["trigger"] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : throw new GlucoseWatchSpecException(
                $"{where}.trigger is required: one of {string.Join(", ", Kinds)}");

        GlucoseTrigger parsed = kind switch
        {
            GlucoseKind => ParseLevel(trigger, where),
            TrendKind => ParseTrend(trigger, where),
            ForecastKind => ParseForecast(trigger, where),
            SensorKind => ParseSensor(trigger, where),
            _ => throw new GlucoseWatchSpecException(
                $"{where}.trigger '{kind}' is not one of a glucose watch's: {string.Join(", ", Kinds)}. "
                + "A watch on anything else is a home watch, written with no glucose trigger in it.")
        };

        return parsed with
        {
            Person = trigger["person"] switch
            {
                null => null,
                JsonValue person when person.TryGetValue<string>(out var folder) && !string.IsNullOrWhiteSpace(folder) =>
                    folder.Trim(),
                _ => throw new GlucoseWatchSpecException(
                    $"{where}.person must be the name of a folder under /sugary/people (leave it out for your own sensor)")
            }
        };
    }

    private static GlucoseLevelTrigger ParseLevel(JsonObject trigger, string where)
    {
        Only(trigger, where, GlucoseKind, "below", "above", "for");
        var (bound, level) = Level(trigger, where);
        return new GlucoseLevelTrigger(bound, level, OptionalDuration(trigger, "for", where));
    }

    private static GlucoseTrendTrigger ParseTrend(JsonObject trigger, string where)
    {
        Only(trigger, where, TrendKind, "falling", "rising");
        var (field, direction) = OneOf(trigger, where, ("falling", GlucoseTrendDirection.Falling),
            ("rising", GlucoseTrendDirection.Rising));
        var rate = Number(trigger, field, where);
        return rate > 0
            ? new GlucoseTrendTrigger(direction, rate)
            : throw new GlucoseWatchSpecException(
                $"{where}.{field} must be a positive number: how many mg/dL a minute it changes by");
    }

    private static GlucoseForecastTrigger ParseForecast(JsonObject trigger, string where)
    {
        Only(trigger, where, ForecastKind, "below", "above", "within");
        var (bound, level) = Level(trigger, where);
        return new GlucoseForecastTrigger(bound, level, RequiredDuration(trigger, "within", where));
    }

    private static SensorTrigger ParseSensor(JsonObject trigger, string where)
    {
        var state = trigger["state"] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : throw new GlucoseWatchSpecException(
                $"{where}.state is required: one of no_readings, link_failing, ending");

        switch (state)
        {
            case "no_readings" or "link_failing":
                Only(trigger, where, $"{SensorKind} {state}", "state", "for");
                return new SensorTrigger(state == "no_readings" ? SensorState.NoReadings : SensorState.LinkFailing,
                    For: RequiredDuration(trigger, "for", where));
            case "ending":
                Only(trigger, where, $"{SensorKind} {state}", "state", "within");
                return new SensorTrigger(SensorState.Ending, Within: RequiredDuration(trigger, "within", where));
            default:
                throw new GlucoseWatchSpecException(
                    $"{where}.state '{state}' is not one of no_readings, link_failing, ending");
        }
    }

    private static (GlucoseBound, double) Level(JsonObject trigger, string where)
    {
        var (field, bound) = OneOf(trigger, where, ("below", GlucoseBound.Below), ("above", GlucoseBound.Above));
        var level = Number(trigger, field, where);
        return level is >= LowestLevel and <= HighestLevel
            ? (bound, level)
            : throw new GlucoseWatchSpecException(
                $"{where}.{field} is {level.ToString(CultureInfo.InvariantCulture)}, which no sensor reads in mg/dL "
                + $"({LowestLevel.ToString(CultureInfo.InvariantCulture)}-{HighestLevel.ToString(CultureInfo.InvariantCulture)}): "
                + "a glucose watch is in mg/dL, and mmol/L times 18 is mg/dL (3.9 mmol/L is 70)");
    }

    private static (string Field, T Value) OneOf<T>(JsonObject trigger, string where, (string Field, T Value) first,
        (string Field, T Value) second)
    {
        var named = new[] { first, second }.Where(choice => trigger.ContainsKey(choice.Field)).ToList();
        return named.Count == 1
            ? named[0]
            : throw new GlucoseWatchSpecException(
                $"{where} needs one of {first.Field} or {second.Field}" + (named.Count > 1 ? ", not both" : ""));
    }

    private static double Number(JsonObject trigger, string field, string where) =>
        trigger[field] is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            ? value.GetValue<double>()
            : throw new GlucoseWatchSpecException($"{where}.{field} must be a number");

    // Every field the trigger may carry beside its kind and person; anything else is named.
    private static void Only(JsonObject trigger, string where, string what, params string[] fields)
    {
        var unknown = trigger.Select(field => field.Key)
            .FirstOrDefault(key => key is not ("trigger" or "person") && !fields.Contains(key, StringComparer.Ordinal));
        if (unknown is not null)
        {
            throw new GlucoseWatchSpecException(
                $"{where}.{unknown} is not a field of a {what} trigger; its fields are "
                + string.Join(", ", new[] { "trigger" }.Concat(fields).Append("person")));
        }
    }

    private static TimeSpan? OptionalDuration(JsonObject trigger, string field, string where) =>
        trigger.ContainsKey(field) ? WatchDuration.Parse(trigger[field], $"{where}.{field}") : null;

    private static TimeSpan RequiredDuration(JsonObject trigger, string field, string where) =>
        trigger.ContainsKey(field)
            ? WatchDuration.Parse(trigger[field], $"{where}.{field}")
            : throw new GlucoseWatchSpecException(
                $"{where}.{field} is required: a duration such as {{\"minutes\": 15}}");
}

// Fires when her glucose crosses below (or above) the level and has stayed there `For`.
public sealed record GlucoseLevelTrigger(GlucoseBound Bound, double MgPerDl, TimeSpan? For = null) : GlucoseTrigger
{
    public override string Kind => GlucoseKind;

    protected override IEnumerable<(string, JsonNode?)> Fields() =>
        For is { } span
            ? [(BoundField(Bound), MgPerDl), ("for", WatchDuration.ToJson(span))]
            : [(BoundField(Bound), MgPerDl)];

    internal static string BoundField(GlucoseBound bound) => bound == GlucoseBound.Below ? "below" : "above";
}

// Fires when her glucose starts changing at least this fast, in mg/dL a minute.
public sealed record GlucoseTrendTrigger(GlucoseTrendDirection Direction, double MgPerDlPerMinute) : GlucoseTrigger
{
    public override string Kind => TrendKind;

    protected override IEnumerable<(string, JsonNode?)> Fields() =>
        [(Direction == GlucoseTrendDirection.Falling ? "falling" : "rising", MgPerDlPerMinute)];
}

// Fires when her glucose, carried on as it is going, would cross the level `Within` from now.
public sealed record GlucoseForecastTrigger(GlucoseBound Bound, double MgPerDl, TimeSpan Within) : GlucoseTrigger
{
    public override string Kind => ForecastKind;

    protected override IEnumerable<(string, JsonNode?)> Fields() =>
        [(GlucoseLevelTrigger.BoundField(Bound), MgPerDl), ("within", WatchDuration.ToJson(Within))];
}

// Fires on the sensor rather than the glucose: no readings for `For`, the link failing for `For`, or the
// sensor's end `Within` from now.
public sealed record SensorTrigger(SensorState State, TimeSpan? For = null, TimeSpan? Within = null) : GlucoseTrigger
{
    public override string Kind => SensorKind;

    protected override IEnumerable<(string, JsonNode?)> Fields() => State switch
    {
        SensorState.NoReadings => [("state", "no_readings"), ("for", WatchDuration.ToJson(For!.Value))],
        SensorState.LinkFailing => [("state", "link_failing"), ("for", WatchDuration.ToJson(For!.Value))],
        _ => [("state", "ending"), ("within", WatchDuration.ToJson(Within!.Value))]
    };
}

// A span written as a home watch writes one: {"hours": h, "minutes": m, "seconds": s}, any of them.
public static class WatchDuration
{
    private static readonly string[] _units = ["hours", "minutes", "seconds"];

    public static TimeSpan Parse(JsonNode? node, string where)
    {
        if (node is not JsonObject duration || duration.Count == 0)
        {
            throw new GlucoseWatchSpecException($"{where} must be a duration such as {{\"minutes\": 15}}");
        }

        if (duration.Select(part => part.Key).FirstOrDefault(key => !_units.Contains(key, StringComparer.Ordinal)) is { } unknown)
        {
            throw new GlucoseWatchSpecException(
                $"{where}.{unknown} is not a unit of a duration; use hours, minutes or seconds");
        }

        var span = duration.Aggregate(TimeSpan.Zero, (sum, part) =>
            sum + (part.Value is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.GetValue<double>() >= 0
                ? part.Key switch
                {
                    "hours" => TimeSpan.FromHours(value.GetValue<double>()),
                    "minutes" => TimeSpan.FromMinutes(value.GetValue<double>()),
                    _ => TimeSpan.FromSeconds(value.GetValue<double>())
                }
                : throw new GlucoseWatchSpecException($"{where}.{part.Key} must be a number of zero or more")));

        return span > TimeSpan.Zero
            ? span
            : throw new GlucoseWatchSpecException($"{where} must be longer than zero");
    }

    // The largest whole unit, so a span reads back as it was most likely written.
    public static JsonObject ToJson(TimeSpan span) =>
        span.Ticks % TimeSpan.TicksPerHour == 0 ? new JsonObject { ["hours"] = (long)span.TotalHours }
        : span.Ticks % TimeSpan.TicksPerMinute == 0 ? new JsonObject { ["minutes"] = (long)span.TotalMinutes }
        : new JsonObject { ["seconds"] = span.TotalSeconds };
}

// When a glucose watch may fire: Home Assistant's time condition, the one kind Sugary judges itself.
// `After` and `Before` are wall times on the household's clock; an `After` later than `Before` spans
// midnight, as Home Assistant reads it.
public sealed record GlucoseTimeCondition(TimeOnly? After, TimeOnly? Before, IReadOnlyList<DayOfWeek>? Weekdays)
{
    private static readonly string[] _fields = ["condition", "after", "before", "weekday"];

    private static readonly IReadOnlyDictionary<string, DayOfWeek> _days = new Dictionary<string, DayOfWeek>(StringComparer.Ordinal)
    {
        ["mon"] = DayOfWeek.Monday, ["tue"] = DayOfWeek.Tuesday, ["wed"] = DayOfWeek.Wednesday,
        ["thu"] = DayOfWeek.Thursday, ["fri"] = DayOfWeek.Friday, ["sat"] = DayOfWeek.Saturday, ["sun"] = DayOfWeek.Sunday
    };

    public static GlucoseTimeCondition Parse(JsonNode? node, string where)
    {
        if (node is not JsonObject condition)
        {
            throw new GlucoseWatchSpecException($"{where} must be an object with a condition");
        }

        var kind = condition["condition"] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : throw new GlucoseWatchSpecException($"{where}.condition is required: \"time\"");
        if (kind != "time")
        {
            throw new GlucoseWatchSpecException(
                $"{where}.condition '{kind}' is not one a glucose watch knows: only \"time\" (after, before, weekday)");
        }

        if (condition.Select(field => field.Key).FirstOrDefault(key => !_fields.Contains(key, StringComparer.Ordinal)) is { } unknown)
        {
            throw new GlucoseWatchSpecException(
                $"{where}.{unknown} is not a field of a time condition; its fields are {string.Join(", ", _fields)}");
        }

        var parsed = new GlucoseTimeCondition(
            Time(condition, "after", where), Time(condition, "before", where), Days(condition["weekday"], where));
        return parsed is { After: null, Before: null, Weekdays: null }
            ? throw new GlucoseWatchSpecException($"{where} needs after, before or weekday")
            : parsed;
    }

    private static TimeOnly? Time(JsonObject condition, string field, string where) =>
        condition[field] switch
        {
            null => null,
            JsonValue value when value.TryGetValue<string>(out var text)
                                 && TimeOnly.TryParseExact(text, ["HH:mm", "HH:mm:ss"], CultureInfo.InvariantCulture,
                                     DateTimeStyles.None, out var time) => time,
            _ => throw new GlucoseWatchSpecException($"{where}.{field} must be a time as \"HH:MM\" or \"HH:MM:SS\"")
        };

    private static IReadOnlyList<DayOfWeek>? Days(JsonNode? node, string where)
    {
        IReadOnlyList<JsonNode?> named = node switch
        {
            null => [],
            JsonArray list => [.. list],
            _ => [node]
        };
        var days = named.Select(day => day is JsonValue value && value.TryGetValue<string>(out var text) && _days.TryGetValue(text, out var found)
                ? found
                : throw new GlucoseWatchSpecException(
                    $"{where}.weekday must be a day or a list of days, each one of {string.Join(", ", _days.Keys)}"))
            .ToList();
        return days.Count == 0 ? null : days;
    }

    public JsonObject ToJson()
    {
        var json = new JsonObject { ["condition"] = "time" };
        if (After is { } after)
        {
            json["after"] = after.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        if (Before is { } before)
        {
            json["before"] = before.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        }

        if (Weekdays is { } weekdays)
        {
            json["weekday"] = new JsonArray([.. weekdays.Select(day => (JsonNode)_days.First(d => d.Value == day).Key)]);
        }

        return json;
    }

    public bool Equals(GlucoseTimeCondition? other) =>
        other is not null && After == other.After && Before == other.Before
        && (Weekdays ?? []).SequenceEqual(other.Weekdays ?? []);

    public override int GetHashCode() => HashCode.Combine(After, Before, Weekdays?.Count);
}

public abstract record GlucoseWatchEffect
{
    public const string PromptKind = "prompt";
    public const string AnnounceKind = "announce";

    // Home Assistant's own effect, which a glucose watch cannot have: Sugary acts on nothing in the home.
    private const string ActionsKind = "actions";

    public abstract string Kind { get; }

    public abstract JsonObject ToJson();

    public static GlucoseWatchEffect Parse(JsonNode? node, string where)
    {
        if (node is not JsonObject effect)
        {
            throw new GlucoseWatchSpecException($"{where} must be an object with a kind");
        }

        var kind = effect["kind"] is JsonValue value && value.TryGetValue<string>(out var text)
            ? text
            : throw new GlucoseWatchSpecException($"{where}.kind is required: {PromptKind} or {AnnounceKind}");

        switch (kind)
        {
            case PromptKind:
                Fields(effect, where, "prompt");
                return new GlucosePromptEffect(Text(effect, "prompt", where));
            case AnnounceKind:
                Fields(effect, where, "text", "target", "insistent");
                return new GlucoseAnnounceEffect(
                    Text(effect, "text", where),
                    effect["target"] switch
                    {
                        null => null,
                        JsonObject target => target.DeepClone().AsObject(),
                        _ => throw new GlucoseWatchSpecException(
                            $"{where}.target must be an object with satelliteId, satelliteIds, room or all "
                            + "(or left out to ring on the person's own channels)")
                    },
                    effect["insistent"] switch
                    {
                        null => null,
                        JsonObject insistent => insistent.DeepClone().AsObject(),
                        _ => throw new GlucoseWatchSpecException($"{where}.insistent must be an object (use {{}} for the defaults)")
                    });
            case ActionsKind:
                throw new GlucoseWatchSpecException(
                    $"{where}.kind 'actions' acts on the home, which a glucose watch cannot: for that, write a home watch "
                    + "on the home's own glucose entity, with no glucose trigger in it");
            default:
                throw new GlucoseWatchSpecException($"{where}.kind '{kind}' is not one of {PromptKind}, {AnnounceKind}");
        }
    }

    private static void Fields(JsonObject effect, string where, params string[] fields)
    {
        if (effect.Select(field => field.Key).FirstOrDefault(key => key != "kind" && !fields.Contains(key, StringComparer.Ordinal)) is { } unknown)
        {
            throw new GlucoseWatchSpecException(
                $"{where}.{unknown} is not a field of a {effect["kind"]} effect; its fields are kind, {string.Join(", ", fields)}");
        }
    }

    private static string Text(JsonObject effect, string field, string where)
    {
        var text = effect[field] is JsonValue value && value.TryGetValue<string>(out var found)
            ? found
            : throw new GlucoseWatchSpecException($"{where}.{field} is required");
        return string.IsNullOrWhiteSpace(text)
            ? throw new GlucoseWatchSpecException($"{where}.{field} must not be empty")
            : text;
    }
}

// A turn the agent runs when the watch fires, and delivers as a home watch's prompt is delivered.
public sealed record GlucosePromptEffect(string Prompt) : GlucoseWatchEffect
{
    public override string Kind => PromptKind;

    public override JsonObject ToJson() => new() { ["kind"] = Kind, ["prompt"] = Prompt };
}

// An alarm rung on the household's satellites, or with no target on the people's own channels.
public sealed record GlucoseAnnounceEffect(string Text, JsonObject? Target, JsonObject? Insistent) : GlucoseWatchEffect
{
    public override string Kind => AnnounceKind;

    public override JsonObject ToJson()
    {
        var json = new JsonObject { ["kind"] = Kind, ["text"] = Text };
        if (Target is not null)
        {
            json["target"] = Target.DeepClone();
        }

        if (Insistent is not null)
        {
            json["insistent"] = Insistent.DeepClone();
        }

        return json;
    }

    public bool Equals(GlucoseAnnounceEffect? other) =>
        other is not null && Text == other.Text
        && JsonNode.DeepEquals(Target, other.Target) && JsonNode.DeepEquals(Insistent, other.Insistent);

    public override int GetHashCode() => Text.GetHashCode(StringComparison.Ordinal);
}

public sealed record GlucoseWatchSpec
{
    private static readonly string[] _fields =
        ["name", "triggers", "conditions", "effects", "once", "enabled", "deliverTo", "userId"];

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public required string Name { get; init; }
    public required IReadOnlyList<GlucoseTrigger> Triggers { get; init; }
    public IReadOnlyList<GlucoseTimeCondition>? Conditions { get; init; }
    public required IReadOnlyList<GlucoseWatchEffect> Effects { get; init; }
    public bool Once { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<string>? DeliverTo { get; init; }
    public string? UserId { get; init; }

    public static GlucoseWatchSpec Parse(string content)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(content);
        }
        catch (JsonException ex)
        {
            throw new GlucoseWatchSpecException($"watch.json is not valid JSON: {ex.Message}");
        }

        if (root is not JsonObject watch)
        {
            throw new GlucoseWatchSpecException("watch.json must be a JSON object");
        }

        if (watch.Select(field => field.Key).FirstOrDefault(key => !_fields.Contains(key, StringComparer.Ordinal)) is { } unknown)
        {
            throw new GlucoseWatchSpecException($"unknown field '{unknown}'; the fields are {string.Join(", ", _fields)}");
        }

        return new GlucoseWatchSpec
        {
            Name = watch["name"] is JsonValue name && name.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                ? text
                : throw new GlucoseWatchSpecException("name is required"),
            Triggers = watch["triggers"] is JsonArray { Count: > 0 } triggers
                ? triggers.Select((trigger, index) => GlucoseTrigger.Parse(trigger, $"triggers[{index}]")).ToList()
                : throw new GlucoseWatchSpecException(
                    $"triggers must be a non-empty list, each {{trigger: {string.Join("|", GlucoseTrigger.Kinds)}, ...}}"),
            Conditions = watch["conditions"] switch
            {
                null or JsonArray { Count: 0 } => null,
                JsonArray conditions => conditions
                    .Select((condition, index) => GlucoseTimeCondition.Parse(condition, $"conditions[{index}]")).ToList(),
                _ => throw new GlucoseWatchSpecException("conditions must be a list of time conditions")
            },
            Effects = watch["effects"] is JsonArray { Count: > 0 } effects
                ? effects.Select((effect, index) => GlucoseWatchEffect.Parse(effect, $"effects[{index}]")).ToList()
                : throw new GlucoseWatchSpecException("effects must be a non-empty list, each {kind: prompt|announce, ...}"),
            Once = Flag(watch, "once", false),
            Enabled = Flag(watch, "enabled", true),
            DeliverTo = watch["deliverTo"] switch
            {
                null or JsonArray { Count: 0 } => null,
                JsonArray targets when targets.All(t => t is JsonValue v && v.TryGetValue<string>(out _)) =>
                    targets.Select(t => t!.GetValue<string>()).ToList(),
                _ => throw new GlucoseWatchSpecException(
                    "deliverTo must be a list of channel ids, e.g. [\"telegram\"] or [\"voice:office-01\"]")
            },
            UserId = watch["userId"] switch
            {
                null => null,
                JsonValue user when user.TryGetValue<string>(out var id) => id,
                _ => throw new GlucoseWatchSpecException("userId must be a string")
            }
        };
    }

    private static bool Flag(JsonObject watch, string field, bool fallback) =>
        watch[field] switch
        {
            null => fallback,
            JsonValue value when value.TryGetValue<bool>(out var flag) => flag,
            _ => throw new GlucoseWatchSpecException($"{field} must be true or false")
        };

    // The file as the agent reads it back: every field it may write, in the order the guide lists them,
    // with the optional ones present as null so an edit can find them.
    public string ToJson() => new JsonObject
    {
        ["name"] = Name,
        ["triggers"] = new JsonArray([.. Triggers.Select(t => (JsonNode)t.ToJson())]),
        ["conditions"] = new JsonArray([.. (Conditions ?? []).Select(c => (JsonNode)c.ToJson())]),
        ["effects"] = new JsonArray([.. Effects.Select(e => (JsonNode)e.ToJson())]),
        ["once"] = Once,
        ["enabled"] = Enabled,
        ["deliverTo"] = DeliverTo is null ? null : new JsonArray([.. DeliverTo.Select(d => (JsonNode)d)]),
        ["userId"] = UserId
    }.ToJsonString(_json);
}