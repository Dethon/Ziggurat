using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.HomeAssistant.Vfs;

// What the automation cannot say for itself, kept as JSON in its description (the alarm calendar's
// idiom): who created the watch and therefore runs its prompts, the effects as they were authored,
// and the fields Home Assistant has no slot for.
public sealed record HaWatchMetadata(
    string AgentId,
    IReadOnlyList<HaWatchEffect> Effects,
    bool Once,
    IReadOnlyList<string>? DeliverTo,
    string? UserId,
    DateTimeOffset CreatedAt)
{
    private const string Key = "watch";

    public string ToJson() => new JsonObject
    {
        [Key] = new JsonObject
        {
            ["agentId"] = AgentId,
            ["effects"] = new JsonArray([.. Effects.Select(e => (JsonNode)e.ToJson())]),
            ["once"] = Once,
            ["deliverTo"] = DeliverTo is null ? null : new JsonArray([.. DeliverTo.Select(d => (JsonNode)d)]),
            ["userId"] = UserId,
            ["createdAt"] = CreatedAt.ToString("o")
        }
    }.ToJsonString();

    // Null for anything that is not a watch's description: a hand-made automation's prose, an
    // empty string, JSON of another shape. Such an automation is invisible to the subtree.
    public static HaWatchMetadata? TryParse(string? description)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(description) is not JsonObject root || root[Key] is not JsonObject watch)
            {
                return null;
            }

            return new HaWatchMetadata(
                watch["agentId"]?.GetValue<string>() ?? "default",
                (watch["effects"] as JsonArray ?? [])
                    .Select((effect, index) => HaWatchEffect.Parse(effect, $"effects[{index}]"))
                    .ToList(),
                watch["once"]?.GetValue<bool>() ?? false,
                watch["deliverTo"] is JsonArray deliverTo ? deliverTo.Select(d => d!.GetValue<string>()).ToList() : null,
                watch["userId"]?.GetValue<string>(),
                DateTimeOffset.TryParse(watch["createdAt"]?.GetValue<string>(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                    ? at
                    : DateTimeOffset.MinValue);
        }
        catch (Exception ex) when (ex is JsonException or HaWatchSpecException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}

// One watch as the home holds it: the file the agent wrote, the metadata beside it, and the
// automation entity's state — null until Home Assistant has loaded the entity.
public sealed record HaWatch(string Id, HaWatchSpec Spec, HaWatchMetadata Meta, HaAutomationState? State)
{
    public bool Enabled => State?.IsOn ?? true;

    public bool Spent => Meta.Once && State is { IsOn: false };
}

// The rendering of a watch into a Home Assistant automation, and the reading of one back. The
// automation is what the home evaluates and runs; the file is a view of it, stored nowhere else.
public static class HaWatchAutomation
{
    // The marker that makes an automation a watch: its config id starts with this. Hand-made
    // automations — the alarm bridge, blueprints — carry no prefix and are invisible to
    // `/ha/watches`, while still appearing under `/ha/entities/automation/` as entities.
    public const string IdPrefix = "assistant_watch_";

    // The two hand-provisioned rest_commands the home carries (docs/home-assistant-bridges.md).
    public const string AnnounceCommand = "rest_command.voice_announce";
    public const string WatchFiredCommand = "rest_command.assistant_watch_fired";

    public static string AutomationId(string watchId) => IdPrefix + watchId;

    public static bool IsWatch(string? automationId) =>
        automationId is not null && automationId.StartsWith(IdPrefix, StringComparison.Ordinal);

    public static string WatchId(string automationId) => automationId[IdPrefix.Length..];

    // A watch id is a path segment and an automation id at once: a descriptive slug, nothing that
    // needs escaping in either place.
    public static bool IsValidWatchId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 80
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static JsonObject Render(string watchId, HaWatchSpec spec, HaWatchMetadata meta)
    {
        var actions = spec.Effects.SelectMany(effect => RenderEffect(effect, watchId, spec, meta)).ToList();
        if (spec.Once)
        {
            // Home Assistant offers no self-delete, so a one-shot turns itself off as its last
            // action and is listed as spent until the agent removes it. `this` is the automation's
            // own state object, which spares knowing the entity id before the automation exists.
            actions.Add(new JsonObject
            {
                ["action"] = "automation.turn_off",
                ["target"] = new JsonObject { ["entity_id"] = "{{ this.entity_id }}" }
            });
        }

        var automation = new JsonObject
        {
            ["id"] = AutomationId(watchId),
            ["alias"] = spec.Name,
            ["description"] = meta.ToJson(),
            // A slow prompt callback must not stack runs; the callback is one POST answered in
            // milliseconds, so a fire arriving during it is not expected to be lost.
            ["mode"] = "single",
            ["triggers"] = spec.Triggers.DeepClone()
        };
        if (spec.Conditions is { Count: > 0 } conditions)
        {
            automation["conditions"] = conditions.DeepClone();
        }
        automation["actions"] = new JsonArray([.. actions.Select(a => (JsonNode)a)]);
        return automation;
    }

    private static IEnumerable<JsonObject> RenderEffect(HaWatchEffect effect, string watchId, HaWatchSpec spec, HaWatchMetadata meta) =>
        effect switch
        {
            HaActionsEffect actions => actions.Actions.Select(a => a!.DeepClone().AsObject()),
            HaAnnounceEffect announce => RenderAnnounce(announce),
            HaPromptEffect prompt => RenderPrompt(prompt, watchId, spec, meta),
            _ => throw new InvalidOperationException($"Unknown effect kind {effect.Kind}")
        };

    // Text crosses to Home Assistant as a template and is rendered there — a `variables` step is
    // where the home renders it, and the payload is then composed from the rendered variable with
    // `to_json`, so a value with quotes or braces cannot break the JSON the rest_command sends. The
    // bridge's rest_command takes the whole body as one `payload` string.
    private static IEnumerable<JsonObject> RenderAnnounce(HaAnnounceEffect announce)
    {
        var variables = new JsonObject
        {
            ["watch_announce_text"] = announce.Text,
            ["watch_announce_target"] = announce.Target.DeepClone()
        };
        var payload = "{'text': watch_announce_text, 'target': watch_announce_target";
        if (announce.Insistent is not null)
        {
            variables["watch_announce_insistent"] = announce.Insistent.DeepClone();
            payload += ", 'insistent': watch_announce_insistent";
        }
        payload += "}";

        yield return new JsonObject { ["variables"] = variables };
        yield return new JsonObject
        {
            ["action"] = AnnounceCommand,
            ["data"] = new JsonObject { ["payload"] = $"{{{{ {payload} | to_json }}}}" }
        };
    }

    // The callback's body: the watch's identity and delivery as literals, the prompt rendered by
    // the home, and the firing facts from the `trigger` variable — each guarded, because a template
    // or time trigger carries no entity and an unguarded read would render as an error.
    private static IEnumerable<JsonObject> RenderPrompt(HaPromptEffect prompt, string watchId, HaWatchSpec spec, HaWatchMetadata meta)
    {
        var variables = new JsonObject
        {
            ["watch_id"] = watchId,
            ["watch_name"] = spec.Name,
            ["watch_agent"] = meta.AgentId,
            ["watch_prompt"] = prompt.Prompt
        };
        var deliverTo = "none";
        if (meta.DeliverTo is not null)
        {
            variables["watch_deliver_to"] = new JsonArray([.. meta.DeliverTo.Select(d => (JsonNode)d)]);
            deliverTo = "watch_deliver_to";
        }
        var userId = "none";
        if (meta.UserId is not null)
        {
            variables["watch_user"] = meta.UserId;
            userId = "watch_user";
        }

        var payload =
            "{'watchId': watch_id, 'name': watch_name, 'agentId': watch_agent, "
            + $"'deliverTo': {deliverTo}, 'userId': {userId}, 'prompt': watch_prompt, "
            + "'entityId': (trigger.entity_id if trigger.entity_id is defined else none), "
            + "'friendlyName': (trigger.to_state.name if trigger.to_state is defined and trigger.to_state else none), "
            + "'fromState': (trigger.from_state.state if trigger.from_state is defined and trigger.from_state else none), "
            + "'toState': (trigger.to_state.state if trigger.to_state is defined and trigger.to_state else none), "
            + "'description': (trigger.description if trigger.description is defined else none), "
            + "'firedAt': now().isoformat()}";

        yield return new JsonObject { ["variables"] = variables };
        yield return new JsonObject
        {
            ["action"] = WatchFiredCommand,
            ["data"] = new JsonObject { ["payload"] = $"{{{{ {payload} | to_json }}}}" }
        };
    }

    // The automation read back as the file the agent wrote. Null when the automation is not a watch:
    // no prefix, or a description that is not the metadata above.
    public static HaWatch? Project(string automationId, JsonObject config, HaAutomationState? state)
    {
        if (!IsWatch(automationId) || HaWatchMetadata.TryParse(config["description"]?.GetValue<string>()) is not { } meta)
        {
            return null;
        }

        var id = automationId;
        var spec = new HaWatchSpec
        {
            Name = config["alias"]?.GetValue<string>() ?? WatchId(id),
            Triggers = config["triggers"] as JsonArray ?? [],
            Conditions = config["conditions"] is JsonArray { Count: > 0 } conditions ? conditions : null,
            Effects = meta.Effects,
            Once = meta.Once,
            Enabled = state?.IsOn ?? true,
            DeliverTo = meta.DeliverTo,
            UserId = meta.UserId
        };
        return new HaWatch(WatchId(id), spec, meta, state);
    }
}