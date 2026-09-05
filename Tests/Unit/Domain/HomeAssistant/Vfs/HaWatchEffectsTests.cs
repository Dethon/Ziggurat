using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

// The three effect kinds, one-shots and pauses, asserted on the automation the home received: an
// announcement is the voice bridge's rest_command, a prompt is the watch-fired rest_command with
// every firing fact as a template, a one-shot ends by turning itself off, and a pause is the
// automation switched off rather than removed.
public class HaWatchEffectsTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);

    private static HaFileSystem Build(out FakeHaClient client, ReplyTarget? origin = null)
    {
        client = new FakeHaClient
        {
            States = { Entity("sensor.laura_glucose", "112", ("state_class", JsonValue.Create("measurement"))) }
        };
        var local = client;
        var time = new FakeTimeProvider(Now);
        return new HaFileSystem(new HaCatalogProvider(() => local, time), () => local, timeProvider: time,
            caller: () => new ConversationContext("jonas", "conv-1", "fran", origin ?? new ReplyTarget("telegram", "conv-1")));
    }

    private static async Task<JsonObject> Written(HaFileSystem fs, FakeHaClient client, string id, string effects, string extra = "")
    {
        var content = $$"""
            {"name": "Laura's sugar",
             "triggers": [{"trigger": "numeric_state", "entity_id": "sensor.laura_glucose", "below": 60}],
             "effects": {{effects}}{{extra}}}
            """;
        (await fs.CreateAsync($"watches/{id}/watch.json", content, false, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        return client.UpsertedAutomations.Last().Config;
    }

    private static JsonArray Actions(JsonObject automation) => automation["actions"]!.AsArray();

    private const string Announce =
        """[{"kind": "announce", "text": "Her sugar is {{ trigger.to_state.state }}", "target": {"room": "bedroom"}, "insistent": {"gapSeconds": 30}}]""";

    [Fact]
    public async Task AnAnnounceEffect_RendersTheVoiceAnnounceCall_WithTextTargetAndInsistent()
    {
        var fs = Build(out var client);

        var actions = Actions(await Written(fs, client, "sugar-low", Announce));

        actions.Count.ShouldBe(2);
        var variables = actions[0]!["variables"]!.AsObject();
        variables["watch_announce_text"]!.GetValue<string>().ShouldBe("Her sugar is {{ trigger.to_state.state }}");
        variables["watch_announce_target"]!.ToJsonString().ShouldBe("""{"room":"bedroom"}""");
        variables["watch_announce_insistent"]!.ToJsonString().ShouldBe("""{"gapSeconds":30}""");
        actions[1]!["action"]!.GetValue<string>().ShouldBe("rest_command.voice_announce");
        actions[1]!["data"]!["payload"]!.GetValue<string>().ShouldBe(
            "{{ {'text': watch_announce_text, 'target': watch_announce_target, 'insistent': watch_announce_insistent} | to_json }}");
    }

    [Fact]
    public async Task AnAnnounceWithoutInsistent_LeavesItOutOfThePayload()
    {
        var fs = Build(out var client);

        var actions = Actions(await Written(fs, client, "sugar-low",
            """[{"kind": "announce", "text": "hi", "target": {"all": true}}]"""));

        actions[0]!["variables"]!["watch_announce_insistent"].ShouldBeNull();
        actions[1]!["data"]!["payload"]!.GetValue<string>().ShouldNotContain("insistent");
    }

    [Fact]
    public async Task AnAnnounceFollowedByActions_RendersInThatOrder()
    {
        var fs = Build(out var client);

        var actions = Actions(await Written(fs, client, "sugar-low",
            """[{"kind": "announce", "text": "hi", "target": {"room": "bedroom"}}, {"kind": "actions", "actions": [{"action": "light.turn_on", "target": {"entity_id": "light.bedroom"}}]}]"""));

        actions.Select(a => a!["action"]?.GetValue<string>()).ShouldBe([null, "rest_command.voice_announce", "light.turn_on"]);
    }

    [Fact]
    public async Task APromptEffect_RendersTheWatchFiredCall_WithEveryFiringFactAsATemplate()
    {
        var fs = Build(out var client, origin: new ReplyTarget("voice", "conv-1", "kitchen-01"));

        var actions = Actions(await Written(fs, client, "sugar-low",
            """[{"kind": "prompt", "prompt": "Look into it"}]""", """, "userId": "fran" """));

        actions.Count.ShouldBe(2);
        var variables = actions[0]!["variables"]!.AsObject();
        variables["watch_id"]!.GetValue<string>().ShouldBe("sugar-low");
        variables["watch_name"]!.GetValue<string>().ShouldBe("Laura's sugar");
        variables["watch_agent"]!.GetValue<string>().ShouldBe("jonas");
        variables["watch_prompt"]!.GetValue<string>().ShouldBe("Look into it");
        variables["watch_deliver_to"]!.ToJsonString().ShouldBe("""["voice:kitchen-01"]""");
        variables["watch_user"]!.GetValue<string>().ShouldBe("fran");
        actions[1]!["action"]!.GetValue<string>().ShouldBe("rest_command.assistant_watch_fired");
        var payload = actions[1]!["data"]!["payload"]!.GetValue<string>();
        payload.ShouldStartWith("{{ {");
        payload.ShouldEndWith("| to_json }}");
        foreach (var fact in new[]
        {
            "'watchId': watch_id", "'name': watch_name", "'agentId': watch_agent", "'deliverTo': watch_deliver_to",
            "'userId': watch_user", "'prompt': watch_prompt",
            "'entityId': (trigger.entity_id if trigger.entity_id is defined else none)",
            "'friendlyName': (trigger.to_state.name if trigger.to_state is defined and trigger.to_state else none)",
            "'fromState': (trigger.from_state.state if trigger.from_state is defined and trigger.from_state else none)",
            "'toState': (trigger.to_state.state if trigger.to_state is defined and trigger.to_state else none)",
            "'description': (trigger.description if trigger.description is defined else none)",
            "'firedAt': now().isoformat()"
        })
        {
            payload.ShouldContain(fact);
        }
    }

    [Fact]
    public async Task DeliverToAndUserId_LandInTheDescription_AndRoundTripThroughTheFile()
    {
        var fs = Build(out var client);

        var automation = await Written(fs, client, "sugar-low",
            """[{"kind": "prompt", "prompt": "Look into it"}]""", """, "deliverTo": ["signalr", "voice:office-01"], "userId": "fran" """);

        var meta = JsonNode.Parse(automation["description"]!.GetValue<string>())!["watch"]!;
        meta["deliverTo"]!.ToJsonString().ShouldBe("""["signalr","voice:office-01"]""");
        meta["userId"]!.GetValue<string>().ShouldBe("fran");

        var read = (await fs.ReadAsync("watches/sugar-low/watch.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content;
        read.ShouldContain("\"signalr\"");
        read.ShouldContain("\"voice:office-01\"");
        read.ShouldContain("\"userId\": \"fran\"");
    }

    [Theory]
    [InlineData("""[{"kind": "prompt", "prompt": "p"}]""")]
    [InlineData("""[{"kind": "announce", "text": "t", "target": {"all": true}}]""")]
    [InlineData("""[{"kind": "actions", "actions": [{"action": "light.turn_on"}]}]""")]
    public async Task Once_AppendsAFinalTurnOffOfTheWatchsOwnAutomation_ForEveryEffectKind(string effects)
    {
        var fs = Build(out var client);

        var actions = Actions(await Written(fs, client, "washing-done", effects, """, "once": true"""));

        var last = actions[^1]!;
        last["action"]!.GetValue<string>().ShouldBe("automation.turn_off");
        last["target"]!["entity_id"]!.GetValue<string>().ShouldBe("{{ this.entity_id }}");
    }

    [Fact]
    public async Task NotOnce_AppendsNoTurnOff()
    {
        var fs = Build(out var client);

        var actions = Actions(await Written(fs, client, "sugar-low", """[{"kind": "prompt", "prompt": "p"}]"""));

        actions.Select(a => a!["action"]?.GetValue<string>()).ShouldNotContain("automation.turn_off");
    }

    // A spent one-shot: the automation is off, the file reads enabled false, the status says spent.
    [Fact]
    public async Task ASpentOneShot_ReadsEnabledFalseAndSpent_WhileAPausedWatchIsOnlyOff()
    {
        var fs = Build(out var client);
        await Written(fs, client, "washing-done", """[{"kind": "prompt", "prompt": "p"}]""", """, "once": true""");
        await Written(fs, client, "sugar-low", """[{"kind": "prompt", "prompt": "p"}]""");
        client.Automations["assistant_watch_washing-done"].IsOn = false;
        client.Automations["assistant_watch_sugar-low"].IsOn = false;

        var spent = await Status(fs, "washing-done");
        spent["enabled"]!.GetValue<bool>().ShouldBeFalse();
        spent["spent"]!.GetValue<bool>().ShouldBeTrue();
        (await Watch(fs, "washing-done"))["enabled"]!.GetValue<bool>().ShouldBeFalse();

        var paused = await Status(fs, "sugar-low");
        paused["enabled"]!.GetValue<bool>().ShouldBeFalse();
        paused["spent"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task EnabledFalse_TurnsTheAutomationOff_AndTrueTurnsItBackOn()
    {
        var fs = Build(out var client);
        await Written(fs, client, "sugar-low", """[{"kind": "prompt", "prompt": "p"}]""");
        client.Calls.Clear();

        (await fs.EditAsync("watches/sugar-low/watch.json",
            [new TextEdit("\"enabled\": true", "\"enabled\": false")], CancellationToken.None))
            .ShouldBeOfType<FsResult<FsEditResult>.Ok>();

        client.Calls.ShouldBe([("automation", "turn_off", "automation.laura_s_sugar")]);
        client.Automations["assistant_watch_sugar-low"].IsOn.ShouldBeFalse();
        (await Watch(fs, "sugar-low"))["enabled"]!.GetValue<bool>().ShouldBeFalse();
        client.Calls.Clear();

        (await fs.EditAsync("watches/sugar-low/watch.json",
            [new TextEdit("\"enabled\": false", "\"enabled\": true")], CancellationToken.None))
            .ShouldBeOfType<FsResult<FsEditResult>.Ok>();

        client.Calls.ShouldBe([("automation", "turn_on", "automation.laura_s_sugar")]);
        client.Automations["assistant_watch_sugar-low"].IsOn.ShouldBeTrue();
    }

    [Fact]
    public async Task AWriteThatLeavesEnabledAlone_MakesNoServiceCall()
    {
        var fs = Build(out var client);
        await Written(fs, client, "sugar-low", """[{"kind": "prompt", "prompt": "p"}]""");

        client.Calls.ShouldBeEmpty();
    }

    private static async Task<JsonObject> Status(HaFileSystem fs, string id) => await Parse(fs, $"watches/{id}/status.json");

    private static async Task<JsonObject> Watch(HaFileSystem fs, string id) => await Parse(fs, $"watches/{id}/watch.json");

    private static async Task<JsonObject> Parse(HaFileSystem fs, string path)
    {
        var read = (await fs.ReadAsync(path, null, null, CancellationToken.None)).ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        var text = string.Join("\n", read.Content.Split('\n').Select(l => l[(l.IndexOf(": ", StringComparison.Ordinal) + 2)..]));
        return JsonNode.Parse(text)!.AsObject();
    }
}