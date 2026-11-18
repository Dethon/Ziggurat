using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using Domain.Extensions;
using Domain.Prompts;
using Domain.Tools.Sugary;
using McpServerSugary.Modules;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Eval.Scenarios;
using Tests.Integration.McpServers;

namespace Tests.Eval.Fixtures;

// The Sugary server as the sugary agent's runs find it, without a model: its estimating prompt and
// its skill served, and her seeded log readable under the mount. A stack that lost either would
// have a paid pass measure an agent with no estimating prompt, or a log question with no log.
public sealed class EvalSugaryLogTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"eval-sugary-test-{Guid.NewGuid():N}");
    private RunningServer _server = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var settings = EvalSugaryLog.Settings(_directory);
        // As the eval stack runs it for a household that lets the run's person read everyone's log, with a
        // bedroom among its satellites: what the glucose watch scenarios that need either declare.
        _server = await InMemoryMcpServer.StartAsync(services => EvalSugaryLog.Configure(
            services, settings, EvalSugaryLog.EveryoneReadable,
            new FakeVoiceHub([.. EvalStack.Roster, new SatelliteDescriptor("bedroom-01", "bedroom")])));
        await EvalSugaryLog.SeedAsync(_server.App.Services);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task TheServer_ServesTheEstimatingPrompt()
    {
        var prompts = await _server.Client.ListPromptsAsync();

        prompts.Select(prompt => prompt.Name).ShouldContain(SugaryPrompt.Name);
    }

    [Fact]
    public async Task TheSeededLog_IsReadUnderTheMount_LastPaellaInAugust()
    {
        var august = await QueryAsync("""
            select name, eaten_at from items
            where name ilike 'paella%' and eaten_at_local >= '2026-08-01' and eaten_at_local < '2026-09-01'
            """);

        august.ShouldHaveSingleItem().ShouldBe(["Paella valenciana", "2026-08-09T14:30:00+02:00"]);
    }

    // The doses a question about her insulin reads: the rapid she took for the last paella, linked
    // to it, and her basal that night, which belongs to no meal.
    [Fact]
    public async Task TheSeededDoses_AreReadUnderTheMount_TheLastPaellasLinkedToIt()
    {
        var rows = await QueryAsync("""
            select d.taken_at, d.kind, d.units, l.meal_id, m.items, m.outcome
            from doses d left join dose_meals l using (dose_id) left join meals m using (meal_id)
            where d.taken_at_local::date = DATE '2026-08-09' order by d.taken_at
            """);

        rows[0].ShouldBe(["2026-08-09T14:30:00+02:00", "rapid", "7", "6b1f0c2e-0000-4000-8000-000000000103",
            "Paella valenciana", "high"]);
        rows.ShouldContain(row => row[1] == "basal" && row[2] == "14" && row[3] == null);
    }

    [Fact]
    public async Task TheSeededFoods_AreHers()
    {
        var names = (await QueryAsync("select name from foods")).Select(row => row[0]).ToList();

        names.ShouldContain("Pan de molde integral");
        names.ShouldContain("Yogur natural");
    }

    // Any whole week of her readings is three quarters in range, whichever clock its edges are drawn on,
    // so the analysis scenario's answer is one figure; the week the scenario names is read here through
    // the README's own statement, which is what a model is told to copy.
    [Theory]
    [InlineData("TIMESTAMPTZ '2026-08-10 00:00:00'", "TIMESTAMPTZ '2026-08-17 00:00:00'")]
    [InlineData("TIMESTAMPTZ '2026-08-10 00:00:00+00'", "TIMESTAMPTZ '2026-08-17 00:00:00+00'")]
    public async Task TheSeededGlucose_AnyWholeWeek_IsThreeQuartersInRange(string since, string until)
    {
        var rows = await QueryAsync(GlucoseQueries.Statistics(since, until, 70, 180));
        var columns = await ColumnsAsync(GlucoseQueries.Statistics(since, until, 70, 180));

        var row = rows.ShouldHaveSingleItem();
        double.Parse(row[columns.IndexOf("in_70_180_pct")]!, CultureInfo.InvariantCulture)
            .ShouldBe(EvalSugaryLog.TimeInRangePercent);
        double.Parse(row[columns.IndexOf("coverage_pct")]!, CultureInfo.InvariantCulture).ShouldBe(100);
    }

    // The newest reading is just past 126 (7 mmol/L) and climbing, which the mmol scenario's crossing
    // note is about, and in range, so nothing else meets a high nobody mentioned.
    [Fact]
    public async Task HerNewestReading_IsInRange_JustPast7Mmol_AndRising()
    {
        var newest = await QueryAsync("select mg_dl, trend from glucose order by read_at desc limit 1");

        newest.ShouldHaveSingleItem().ShouldBe([EvalSugaryLog.NewestMgPerDl.ToString(CultureInfo.InvariantCulture), "rising"]);
    }

    // A watch on Laura's glucose by the person the household let read everyone's log is stored as hers,
    // and the snapshot a scenario declares its change against says so.
    [Fact]
    public async Task AWatchOnLaurasGlucose_IsStoredForHer_AndTheSnapshotNamesHerAndItsTrigger()
    {
        var answer = await CreateWatchAsync("laura-low", $$"""
            {"name": "Laura low", "triggers": [{"trigger": "glucose", "below": 70, "person": "{{EvalSugaryLog.LauraFolder}}"}],
             "effects": [{"kind": "prompt", "prompt": "Laura went low."}]}
            """);

        answer["ok"]?.GetValue<bool>().ShouldNotBe(false, answer.ToJsonString());
        var snapshot = await EvalSugaryLog.SnapshotAsync(_server.App.Services);
        snapshot[EvalSugaryLog.WatchCountKey].ShouldBe("1");
        snapshot[EvalSugaryLog.SubjectKey("laura-low")].ShouldBe(EvalSugaryLog.LauraUserId);
        snapshot[EvalSugaryLog.TriggersKey("laura-low")].ShouldBe("glucose below 70");
    }

    // The bedroom alarm's target is resolved against the run's hub, which knows the room.
    [Fact]
    public async Task AnAlarmInTheBedroom_IsAccepted_AndALevelInMmolIsRoundedInTheSnapshot()
    {
        var answer = await CreateWatchAsync("night-low", """
            {"name": "Night low", "triggers": [{"trigger": "glucose", "below": 60.04}],
             "conditions": [{"condition": "time", "after": "23:00", "before": "07:00"}],
             "effects": [{"kind": "announce", "text": "Glucose is low", "target": {"room": "bedroom"}, "insistent": {}}]}
            """);

        answer["ok"]?.GetValue<bool>().ShouldNotBe(false, answer.ToJsonString());
        (await EvalSugaryLog.SnapshotAsync(_server.App.Services))[EvalSugaryLog.TriggersKey("night-low")]
            .ShouldBe("glucose below 60");
    }

    private async Task<JsonNode> CreateWatchAsync(string id, string content)
    {
        var answer = await _server.Client.CallToolAsync(new CallToolRequestParams
        {
            Name = "fs_create",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["filesystem"] = JsonSerializer.SerializeToElement("sugary-watches"),
                ["path"] = JsonSerializer.SerializeToElement($"{id}/watch.json"),
                ["content"] = JsonSerializer.SerializeToElement(content)
            },
            Meta = Meta()
        });
        return JsonNode.Parse(string.Join("", answer.Content.OfType<TextContentBlock>().Select(block => block.Text)))!;
    }

    private async Task<List<string>> ColumnsAsync(string sql)
    {
        var json = await CallQueryAsync(sql);
        return [.. json["columns"]!.AsArray().Select(column => column!["name"]?.GetValue<string>() ?? column.ToString())];
    }

    private static JsonObject Meta()
    {
        var turn = EvalRun.Turn(SugaryScenarios.HerLogIsReadThroughTheSkill, "conversation-1").GetConversationContext()!;
        return new JsonObject
        {
            [ChannelProtocol.ConversationContextMetaKey] =
                JsonSerializer.SerializeToNode(turn, ChannelProtocol.SerializerOptions)
        };
    }

    // Asked as a scenario's turn asks it: the turn's context on `_meta`, built as a run builds it, so
    // the account the mount reads by is the one the log was seeded under.
    private async Task<List<string?[]>> QueryAsync(string sql)
    {
        var json = await CallQueryAsync(sql);
        return [.. (json["rows"] ?? throw new InvalidOperationException(json.ToJsonString())).AsArray()
            .Select(row => row!.AsArray().Select(cell => cell?.ToString()).ToArray())];
    }

    private async Task<JsonNode> CallQueryAsync(string sql)
    {
        var answer = await _server.Client.CallToolAsync(new CallToolRequestParams
        {
            Name = "fs_query",
            Arguments = new Dictionary<string, JsonElement>
            {
                ["path"] = JsonSerializer.SerializeToElement("/"),
                ["sql"] = JsonSerializer.SerializeToElement(sql)
            },
            Meta = Meta()
        });
        return JsonNode.Parse(string.Join("", answer.Content.OfType<TextContentBlock>().Select(block => block.Text)))!;
    }
}