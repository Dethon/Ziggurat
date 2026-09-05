using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Exceptions;
using Domain.Tools;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

// A watch is a Home Assistant automation the agent writes as a file. What these pin is what the
// agent reads and the errors it gets, and the automation the home received — never how the file
// was rendered on the way.
public class HaWatchesTests
{
    private static readonly DateTimeOffset _now = new(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);

    private const string BlindsWatch = """
        {
          "name": "Close the blinds when the living room is hot",
          "triggers": [{"trigger": "numeric_state", "entity_id": "sensor.living_room_temperature", "above": 27}],
          "conditions": [{"condition": "sun", "after": "sunrise"}],
          "effects": [{"kind": "actions", "actions": [{"action": "cover.close_cover", "target": {"entity_id": "cover.living_room_blinds"}}]}]
        }
        """;

    private static HaFileSystem Build(out FakeHaClient client, string? agentId = "jonas", ReplyTarget? origin = null)
    {
        client = new FakeHaClient
        {
            States =
            {
                Entity("sensor.living_room_temperature", "24", ("friendly_name", JsonValue.Create("Living room temperature"))),
                Entity("cover.living_room_blinds", "open")
            },
            Services = { Service("cover", "close_cover", DomainTarget("cover")) }
        };
        var local = client;
        var time = new FakeTimeProvider(_now);
        var provider = new HaCatalogProvider(() => local, time);
        return new HaFileSystem(provider, () => local, timeProvider: time,
            caller: () => agentId is null ? null : new ConversationContext(agentId, "conv-1", "fran", origin ?? new ReplyTarget("telegram", "conv-1")));
    }

    // The model is never told which channel a turn came from, so "warn me where I asked" is the
    // mount's to keep: a file naming no delivery takes the caller's origin, address included.
    [Theory]
    [InlineData("telegram", null, "telegram")]
    [InlineData("voice", "kitchen-01", "voice:kitchen-01")]
    [InlineData("signalr", null, "signalr")]
    public async Task Create_WithoutDeliverTo_TakesTheCallersOwnChannel(string channel, string? address, string expected)
    {
        var fs = Build(out var client, origin: new ReplyTarget(channel, "conv-1", address));

        await Ok(Create(fs, "blinds-when-hot", BlindsWatch));

        var meta = JsonNode.Parse(client.UpsertedAutomations.Single().Config["description"]!.GetValue<string>())!["watch"]!;
        meta["deliverTo"]!.AsArray().Select(d => d!.GetValue<string>()).ShouldBe([expected]);
        (await Read(fs, "watches/blinds-when-hot/watch.json"))["deliverTo"]!.AsArray().Count.ShouldBe(1);
    }

    [Fact]
    public async Task Create_WithADeliverTo_KeepsIt()
    {
        var fs = Build(out var client, origin: new ReplyTarget("voice", "conv-1", "kitchen-01"));

        await Ok(Create(fs, "blinds-when-hot", BlindsWatch.Replace("\"conditions\"", "\"deliverTo\": [\"telegram\"], \"userId\": \"fran\", \"conditions\"")));

        var meta = JsonNode.Parse(client.UpsertedAutomations.Single().Config["description"]!.GetValue<string>())!["watch"]!;
        meta["deliverTo"]!.ToJsonString().ShouldBe("""["telegram"]""");
        meta["userId"]!.GetValue<string>().ShouldBe("fran");
    }

    private static async Task<T> Ok<T>(Task<FsResult<T>> result) where T : class =>
        (await result).ShouldBeOfType<FsResult<T>.Ok>().Value;

    private static async Task<ToolErrorResult> Err<T>(Task<FsResult<T>> result) where T : class =>
        (await result).ShouldBeOfType<FsResult<T>.Err>().Error;

    private static Task<FsResult<FsCreateResult>> Create(HaFileSystem fs, string id, string content, bool overwrite = false) =>
        fs.CreateAsync($"watches/{id}/watch.json", content, overwrite, true, CancellationToken.None);

    private static async Task<JsonObject> Read(HaFileSystem fs, string path)
    {
        var read = await Ok(fs.ReadAsync(path, null, null, CancellationToken.None));
        // The read is line-numbered like every other file on the mount; strip the numbers back off.
        var text = string.Join("\n", read.Content.Split('\n').Select(l => l[(l.IndexOf(": ", StringComparison.Ordinal) + 2)..]));
        return JsonNode.Parse(text)!.AsObject();
    }

    [Fact]
    public async Task Glob_TheRoot_ListsTheWatchesDirectoryBesideEntitiesAndAreas()
    {
        var fs = Build(out _);

        var entries = (await Ok(fs.GlobAsync("", "*/", CancellationToken.None))).Entries;

        entries.ShouldBe(["areas/", "entities/", "watches/"]);
    }

    [Fact]
    public async Task Create_AHomeActionWatch_BecomesAnAutomationTheHomeReceived()
    {
        var fs = Build(out var client);

        var created = await Ok(Create(fs, "blinds-when-hot", BlindsWatch));

        created.Status.ShouldBe("created");
        var (id, automation) = client.UpsertedAutomations.ShouldHaveSingleItem();
        id.ShouldBe("assistant_watch_blinds-when-hot");
        automation["id"]!.GetValue<string>().ShouldBe("assistant_watch_blinds-when-hot");
        automation["alias"]!.GetValue<string>().ShouldBe("Close the blinds when the living room is hot");
        automation["mode"]!.GetValue<string>().ShouldBe("single");
        automation["triggers"]!.ToJsonString().ShouldBe(
            """[{"trigger":"numeric_state","entity_id":"sensor.living_room_temperature","above":27}]""");
        automation["conditions"]!.ToJsonString().ShouldBe("""[{"condition":"sun","after":"sunrise"}]""");
        automation["actions"]!.ToJsonString().ShouldBe(
            """[{"action":"cover.close_cover","target":{"entity_id":"cover.living_room_blinds"}}]""");
        var meta = JsonNode.Parse(automation["description"]!.GetValue<string>())!["watch"]!;
        meta["agentId"]!.GetValue<string>().ShouldBe("jonas");
        meta["once"]!.GetValue<bool>().ShouldBeFalse();
        meta["createdAt"]!.GetValue<string>().ShouldStartWith("2026-09-05T10:00:00");
    }

    [Fact]
    public async Task Create_ThenGlobAndRead_RoundTripsTheFile()
    {
        var fs = Build(out _);
        await Ok(Create(fs, "blinds-when-hot", BlindsWatch));

        var entries = (await Ok(fs.GlobAsync("watches", "**", CancellationToken.None))).Entries;
        entries.ShouldBe(["watches/blinds-when-hot/", "watches/blinds-when-hot/status.json", "watches/blinds-when-hot/watch.json"]);

        var watch = await Read(fs, "watches/blinds-when-hot/watch.json");
        watch["name"]!.GetValue<string>().ShouldBe("Close the blinds when the living room is hot");
        watch["triggers"]!.ToJsonString().ShouldBe(
            """[{"trigger":"numeric_state","entity_id":"sensor.living_room_temperature","above":27}]""");
        watch["conditions"]!.ToJsonString().ShouldBe("""[{"condition":"sun","after":"sunrise"}]""");
        watch["effects"]!.ToJsonString().ShouldBe(
            """[{"kind":"actions","actions":[{"action":"cover.close_cover","target":{"entity_id":"cover.living_room_blinds"}}]}]""");
        watch["once"]!.GetValue<bool>().ShouldBeFalse();
        watch["enabled"]!.GetValue<bool>().ShouldBeTrue();
        // Named by nobody, so it is the caller's own channel — the Build helper's is Telegram.
        watch["deliverTo"]!.ToJsonString().ShouldBe("""["telegram"]""");
        watch["userId"].ShouldBeNull();
    }

    [Fact]
    public async Task Read_StatusFile_CarriesCreatedAtLastTriggeredEntityAndSpent()
    {
        var fs = Build(out var client);
        await Ok(Create(fs, "blinds-when-hot", BlindsWatch));
        client.Automations["assistant_watch_blinds-when-hot"].LastTriggered = _now.AddMinutes(30);

        var status = await Read(fs, "watches/blinds-when-hot/status.json");

        status["createdAt"]!.GetValue<string>().ShouldStartWith("2026-09-05T10:00:00");
        status["lastTriggeredAt"]!.GetValue<string>().ShouldStartWith("2026-09-05T10:30:00");
        status["automationEntity"]!.GetValue<string>().ShouldBe("automation.close_the_blinds_when_the_living_room_is_hot");
        status["spent"]!.GetValue<bool>().ShouldBeFalse();
        status["enabled"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task Info_TheWatchesRootADirectoryAndItsFiles_Exist_AndAnUnknownWatchDoesNot()
    {
        var fs = Build(out _);
        await Ok(Create(fs, "blinds-when-hot", BlindsWatch));

        (await Ok(fs.InfoAsync("watches", CancellationToken.None))).ShouldSatisfyAllConditions(
            i => i.Exists.ShouldBeTrue(), i => i.IsDirectory.ShouldBe(true));
        (await Ok(fs.InfoAsync("watches/blinds-when-hot", CancellationToken.None))).ShouldSatisfyAllConditions(
            i => i.Exists.ShouldBeTrue(), i => i.IsDirectory.ShouldBe(true));
        (await Ok(fs.InfoAsync("watches/blinds-when-hot/watch.json", CancellationToken.None))).ShouldSatisfyAllConditions(
            i => i.Exists.ShouldBeTrue(), i => i.IsDirectory.ShouldBe(false));
        (await Ok(fs.InfoAsync("watches/ghost/watch.json", CancellationToken.None))).Exists.ShouldBeFalse();
    }

    // The failure this pins is the one alarms had: a change that ends as two records. Editing a
    // watch replaces the same automation; the home holds one, under the same id, with the new value.
    [Fact]
    public async Task Edit_ChangesTheThresholdInPlace_LeavingOneAutomationUnderTheSameId()
    {
        var fs = Build(out var client);
        await Ok(Create(fs, "blinds-when-hot", BlindsWatch));

        var edited = await Ok(fs.EditAsync("watches/blinds-when-hot/watch.json",
            [new TextEdit("\"above\": 27", "\"above\": 29")], CancellationToken.None));

        edited.Status.ShouldBe("edited");
        client.Automations.Keys.ShouldBe(["assistant_watch_blinds-when-hot"]);
        client.UpsertedAutomations.Count.ShouldBe(2);
        client.UpsertedAutomations[1].Config["triggers"]![0]!["above"]!.GetValue<int>().ShouldBe(29);
        // The creator and the creation instant survive the replacement.
        var meta = JsonNode.Parse(client.UpsertedAutomations[1].Config["description"]!.GetValue<string>())!["watch"]!;
        meta["agentId"]!.GetValue<string>().ShouldBe("jonas");
        meta["createdAt"]!.GetValue<string>().ShouldStartWith("2026-09-05T10:00:00");
        (await Read(fs, "watches/blinds-when-hot/watch.json"))["triggers"]![0]!["above"]!.GetValue<int>().ShouldBe(29);
    }

    [Fact]
    public async Task Create_AnExistingWatch_RefusesWithoutOverwrite_AndReplacesWithIt()
    {
        var fs = Build(out var client);
        await Ok(Create(fs, "blinds-when-hot", BlindsWatch));

        var refused = await Err(Create(fs, "blinds-when-hot", BlindsWatch));
        refused.ErrorCode.ShouldBe("already_exists");
        refused.Hint.ShouldNotBeNull().ShouldContain("overwrite");

        var replaced = await Ok(Create(fs, "blinds-when-hot", BlindsWatch.Replace("\"above\": 27", "\"above\": 30"), overwrite: true));
        replaced.Status.ShouldBe("replaced");
        client.Automations.Count.ShouldBe(1);
        client.Automations["assistant_watch_blinds-when-hot"].Config["triggers"]![0]!["above"]!.GetValue<int>().ShouldBe(30);
    }

    [Theory]
    [InlineData("watches/blinds-when-hot")]
    [InlineData("watches/blinds-when-hot/watch.json")]
    public async Task Delete_TheDirectoryOrTheFile_RemovesTheAutomationFromTheHome(string path)
    {
        var fs = Build(out var client);
        await Ok(Create(fs, "blinds-when-hot", BlindsWatch));

        var deleted = await Ok(fs.DeleteAsync(path, CancellationToken.None));

        deleted.Status.ShouldBe("deleted");
        client.DeletedAutomations.ShouldBe(["assistant_watch_blinds-when-hot"]);
        client.Automations.ShouldBeEmpty();
        (await Ok(fs.GlobAsync("watches", "*/", CancellationToken.None))).Entries.ShouldBeEmpty();
        (await Err(fs.DeleteAsync(path, CancellationToken.None))).ErrorCode.ShouldBe("not_found");
    }

    [Theory]
    [InlineData("not json", "not valid JSON")]
    [InlineData("[]", "must be a JSON object")]
    [InlineData("""{"triggers":[{"trigger":"state"}],"effects":[{"kind":"prompt","prompt":"x"}]}""", "name is required")]
    [InlineData("""{"name":"x","triggers":[],"effects":[{"kind":"prompt","prompt":"x"}]}""", "triggers must be a non-empty list")]
    [InlineData("""{"name":"x","triggers":[{"trigger":"state"}],"effects":[]}""", "effects must be a non-empty list")]
    [InlineData("""{"name":"x","triggers":[{"trigger":"state"}],"effects":[{"kind":"email","to":"x"}]}""", "effects[0].kind 'email' is not one of prompt, announce, actions")]
    [InlineData("""{"name":"x","triggers":[{"trigger":"state"}],"effects":[{"kind":"announce","text":"hi"}]}""", "effects[0].target is required")]
    [InlineData("""{"name":"x","triggers":[{"trigger":"state"}],"effects":[{"kind":"actions","actions":[]}]}""", "effects[0].actions must be a non-empty list")]
    [InlineData("""{"name":"x","trigger":[{"trigger":"state"}],"effects":[{"kind":"prompt","prompt":"x"}]}""", "unknown field 'trigger'")]
    [InlineData("""{"name":"x","triggers":[{"trigger":"state"}],"effects":[{"kind":"prompt","prompt":"x"}],"deliverTo":"telegram"}""", "deliverTo must be a list")]
    public async Task Create_AMalformedFile_IsAnInvalidArgumentNamingTheField(string content, string expected)
    {
        var fs = Build(out var client);

        var error = await Err(Create(fs, "bad", content));

        error.ErrorCode.ShouldBe("invalid_argument");
        error.Message.ShouldContain(expected);
        client.UpsertedAutomations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_ATriggerHomeAssistantRejects_CarriesItsOwnMessage()
    {
        var fs = Build(out var client);
        client.AutomationRejection = new HomeAssistantConfigRejectedException(
            "Message malformed: required key not provided @ data['triggers'][0]['entity_id']");

        var error = await Err(Create(fs, "blinds-when-hot", BlindsWatch));

        error.ErrorCode.ShouldBe("invalid_argument");
        error.Message.ShouldContain("required key not provided @ data['triggers'][0]['entity_id']");
        client.Automations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Create_WithoutAConversationContext_IsRefused_BecauseAWatchNeedsItsCreator()
    {
        var fs = Build(out var client, agentId: null);

        var error = await Err(Create(fs, "blinds-when-hot", BlindsWatch));

        error.ErrorCode.ShouldBe("invalid_argument");
        error.Message.ShouldContain("creating agent");
        client.UpsertedAutomations.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Blinds When Hot")]
    [InlineData("blinds/when")]
    [InlineData("")]
    public async Task Create_ABadWatchId_IsAnInvalidArgument(string id)
    {
        var fs = Build(out _);

        var error = await Err(fs.CreateAsync($"watches/{id}/watch.json", BlindsWatch, false, true, CancellationToken.None));

        error.ErrorCode.ShouldBeOneOf("invalid_argument", "unsupported_operation");
    }

    // The alarm bridge and any blueprint are automations too. They are the operator's, so the
    // subtree never lists them and no write through it can reach them — while they stay visible
    // where every entity is.
    [Fact]
    public async Task Glob_AHandMadeAutomation_IsAbsentFromWatchesAndPresentUnderEntities()
    {
        var fs = Build(out var client);
        client.SeedAutomation("voice_alarm_bridge", new JsonObject
        {
            ["alias"] = "Voice alarm bridge",
            ["description"] = "Bridges the alarms calendar to the voice hub.",
            ["triggers"] = new JsonArray(new JsonObject { ["trigger"] = "time_pattern", ["seconds"] = 5 }),
            ["actions"] = new JsonArray()
        });
        await Ok(Create(fs, "blinds-when-hot", BlindsWatch));

        var watches = (await Ok(fs.GlobAsync("watches", "*/", CancellationToken.None))).Entries;
        watches.ShouldBe(["watches/blinds-when-hot/"]);
        (await Ok(fs.InfoAsync("watches/voice_alarm_bridge/watch.json", CancellationToken.None))).Exists.ShouldBeFalse();
        (await Err(fs.DeleteAsync("watches/voice_alarm_bridge", CancellationToken.None))).ErrorCode.ShouldBe("not_found");

        var entities = (await Ok(fs.GlobAsync("entities/automation", "*/", CancellationToken.None))).Entries;
        entities.ShouldContain(e => e.Contains("voice_alarm_bridge"));
    }

    // A prefixed automation whose description is not the metadata is not a watch either: somebody
    // edited it in the UI, or wrote one by hand under the prefix. It stays theirs.
    [Fact]
    public async Task Glob_APrefixedAutomationWithoutMetadata_IsNotAWatch()
    {
        var fs = Build(out var client);
        client.SeedAutomation("assistant_watch_handmade", new JsonObject
        {
            ["alias"] = "Hand made", ["description"] = "edited in the UI",
            ["triggers"] = new JsonArray(new JsonObject { ["trigger"] = "state" }), ["actions"] = new JsonArray()
        });

        (await Ok(fs.GlobAsync("watches", "*/", CancellationToken.None))).Entries.ShouldBeEmpty();
        (await Err(fs.ReadAsync("watches/handmade/watch.json", null, null, CancellationToken.None))).ErrorCode.ShouldBe("not_found");
    }

    [Theory]
    [InlineData("entities/cover/living_room_blinds/state.json")]
    [InlineData("entities/cover/living_room_blinds/close_cover.sh")]
    [InlineData("areas/unassigned/cover.living_room_blinds/state.json")]
    [InlineData("watches/blinds-when-hot/status.json")]
    [InlineData("watches/blinds-when-hot/notes.txt")]
    [InlineData("notes.txt")]
    public async Task CreateAndEdit_AnywhereButAWatchFile_AreRefused(string path)
    {
        var fs = Build(out var client);
        await Ok(Create(fs, "blinds-when-hot", BlindsWatch));

        var create = await Err(fs.CreateAsync(path, "{}", true, true, CancellationToken.None));
        var edit = await Err(fs.EditAsync(path, [new TextEdit("a", "b")], CancellationToken.None));

        create.ErrorCode.ShouldBe("unsupported_operation");
        create.Message.ShouldContain("/ha/watches/<id>/watch.json");
        edit.ErrorCode.ShouldBe("unsupported_operation");
        client.UpsertedAutomations.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("entities/cover/living_room_blinds")]
    [InlineData("entities/cover/living_room_blinds/state.json")]
    [InlineData("watches/blinds-when-hot/status.json")]
    [InlineData("watches")]
    public async Task Delete_AnywhereButAWatch_IsRefused(string path)
    {
        var fs = Build(out var client);
        await Ok(Create(fs, "blinds-when-hot", BlindsWatch));

        var error = await Err(fs.DeleteAsync(path, CancellationToken.None));

        error.ErrorCode.ShouldBe("unsupported_operation");
        client.DeletedAutomations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Glob_ScopedToAnEntity_DoesNotAskTheHomeForItsWatches()
    {
        var fs = Build(out var client);
        client.SeedAutomation("assistant_watch_x", new JsonObject
        {
            ["alias"] = "X", ["description"] = new HaWatchMetadata("jonas", [new HaPromptEffect("p")], false, null, null, _now).ToJson(),
            ["triggers"] = new JsonArray(new JsonObject { ["trigger"] = "state" }), ["actions"] = new JsonArray()
        });

        var entries = (await Ok(fs.GlobAsync("entities/cover", "*/", CancellationToken.None))).Entries;

        entries.ShouldBe(["entities/cover/living_room_blinds/"]);
        client.AutomationListings.ShouldBe(0);
    }
}