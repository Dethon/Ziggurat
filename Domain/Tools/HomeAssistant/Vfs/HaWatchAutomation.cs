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
    DateTimeOffset CreatedAt,
    // The on/off the agent last wrote, kept apart from the entity's own: a spent one-shot is off
    // by its own hand, a paused one by the agent's, and the entity state cannot tell the two apart
    // once a refused fire has stamped `last_triggered` on a watch that stayed armed.
    bool Enabled = true)
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
            ["createdAt"] = CreatedAt.ToString("o"),
            ["enabled"] = Enabled
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

            if (watch["agentId"]?.GetValue<string>() is not { Length: > 0 } agentId)
            {
                return null;
            }

            return new HaWatchMetadata(
                agentId,
                (watch["effects"] as JsonArray ?? [])
                    .Select((effect, index) => HaWatchEffect.Parse(effect, $"effects[{index}]"))
                    .ToList(),
                watch["once"]?.GetValue<bool>() ?? false,
                watch["deliverTo"] is JsonArray deliverTo ? deliverTo.Select(d => d!.GetValue<string>()).ToList() : null,
                watch["userId"]?.GetValue<string>(),
                DateTimeOffset.TryParse(watch["createdAt"]?.GetValue<string>(), null,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var at)
                    ? at
                    : DateTimeOffset.MinValue,
                watch["enabled"]?.GetValue<bool>() ?? true);
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

    // Spent is "fired and turned itself off", never "paused": the guide tells the agent to remove
    // spent watches, so a paused one-shot that read as spent would be deleted for being paused. Off
    // with a fire on record is not enough to tell them apart — a fire the callback refused stamps
    // `last_triggered` and leaves the watch armed, and a pause after it looks exactly like a spend —
    // so spent also asks that the agent last wrote it armed: only the automation itself turns off
    // a watch nobody paused.
    public bool Spent => Meta.Once && Meta.Enabled && State is { IsOn: false, LastTriggered: not null };
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
        var prompts = spec.Once ? spec.Effects.OfType<HaPromptEffect>().Count() : 0;
        var actions = spec.Effects
            .Select((effect, index) => (effect, promptIndex: spec.Once && effect is HaPromptEffect
                ? spec.Effects.Take(index).OfType<HaPromptEffect>().Count()
                : (int?)null))
            .SelectMany(e => RenderEffect(e.effect, watchId, spec, meta, e.promptIndex))
            .ToList();
        if (spec.Once)
        {
            // Home Assistant offers no self-delete, so a one-shot turns itself off as its last
            // action and is listed as spent until the agent removes it. `this` is the automation's
            // own state object, which spares knowing the entity id before the automation exists.
            //
            // Only on a fire the stack took: the callback answers 503 when no agent is connected,
            // and the rest_command does not abort the sequence on an error status, so an unguarded
            // turn_off would spend the watch on a fire nobody received. Each prompt's answer is kept
            // in its own variable and the guard wants every one under 400; an answer that never
            // came (the command itself failed and was continued past) is undefined, and fails it.
            if (prompts > 0)
            {
                var taken = string.Join(" and ", Enumerable.Range(0, prompts)
                    .Select(i => $"{ResponseVariable(i)} is defined and {ResponseVariable(i)}.status is defined and {ResponseVariable(i)}.status < 400"));
                actions.Add(new JsonObject
                {
                    ["condition"] = "template",
                    ["value_template"] = $"{{{{ {taken} }}}}"
                });
            }
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

    // `promptIndex` is set only for a one-shot: the index of this prompt among the watch's prompts,
    // naming the variable its callback answer is kept in for the turn_off guard.
    private static IEnumerable<JsonObject> RenderEffect(HaWatchEffect effect, string watchId, HaWatchSpec spec, HaWatchMetadata meta, int? promptIndex) =>
        effect switch
        {
            HaActionsEffect actions => actions.Actions.Select(a => a!.DeepClone().AsObject()),
            HaAnnounceEffect announce => RenderAnnounce(announce),
            HaPromptEffect prompt => RenderPrompt(prompt, watchId, spec, meta, promptIndex),
            _ => throw new InvalidOperationException($"Unknown effect kind {effect.Kind}")
        };

    private static string ResponseVariable(int promptIndex) => $"watch_fire_{promptIndex}";

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
    private static IEnumerable<JsonObject> RenderPrompt(HaPromptEffect prompt, string watchId, HaWatchSpec spec, HaWatchMetadata meta, int? promptIndex)
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
        var call = new JsonObject
        {
            ["action"] = WatchFiredCommand,
            ["data"] = new JsonObject { ["payload"] = $"{{{{ {payload} | to_json }}}}" }
        };
        if (promptIndex is { } index)
        {
            // The answer is kept for the one-shot's turn_off guard, and a command that fails
            // outright is continued past so the guard — not the failure — decides. A watch that
            // stays armed asks for neither: an error status must not abort the effects after it.
            call["response_variable"] = ResponseVariable(index);
            call["continue_on_error"] = true;
        }
        yield return call;
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