using System.Text.Json.Nodes;
using Domain.Exceptions;
using Infrastructure.Clients.HomeAssistant;
using Shouldly;
using Tests.Integration.Fixtures;
using Xunit.Abstractions;

namespace Tests.Integration.Clients;

public class HomeAssistantClientTests(HomeAssistantFixture fixture, ITestOutputHelper output) : IClassFixture<HomeAssistantFixture>
{
    [Fact]
    public async Task ListStatesAsync_ReturnsSeededTestEntity()
    {
        var client = fixture.CreateClient();

        var states = await client.ListStatesAsync();

        states.ShouldNotBeEmpty();
        states.Select(e => e.EntityId).ShouldContain(HomeAssistantFixture.TestEntityId);
    }

    [Fact]
    public async Task GetStateAsync_KnownEntity_ReturnsState()
    {
        var client = fixture.CreateClient();

        var state = await client.GetStateAsync(HomeAssistantFixture.TestEntityId);

        state.ShouldNotBeNull();
        state!.EntityId.ShouldBe(HomeAssistantFixture.TestEntityId);
        state.State.ShouldBeOneOf("on", "off");
    }

    [Fact]
    public async Task GetStateAsync_UnknownEntity_ReturnsNull()
    {
        var client = fixture.CreateClient();

        var state = await client.GetStateAsync("input_boolean.does_not_exist");

        state.ShouldBeNull();
    }

    [Fact]
    public async Task ListServicesAsync_ContainsCoreInputBooleanServices()
    {
        var client = fixture.CreateClient();

        var services = await client.ListServicesAsync();

        var domains = services.Select(s => s.Domain).Distinct().OrderBy(d => d).ToList();
        output.WriteLine($"Service domains ({domains.Count}): {string.Join(", ", domains)}");
        foreach (var s in services.Where(x => x.Domain is "input_boolean" or "homeassistant"))
        {
            output.WriteLine($"  {s.Domain}.{s.Service}");
        }

        services.ShouldContain(s => s.Domain == "input_boolean" && s.Service == "toggle");
        services.ShouldContain(s => s.Domain == "input_boolean" && s.Service == "turn_on");
        services.ShouldContain(s => s.Domain == "homeassistant" && s.Service == "update_entity");
    }

    [Fact]
    public async Task ListServicesAsync_PopulatesTargetForEntityTargetedService()
    {
        var client = fixture.CreateClient();

        var services = await client.ListServicesAsync();

        // input_boolean.toggle is entity-targeted — HA exposes `target.entity[*].domain` so
        // the agent can tell an `entity_id` is required.
        var toggle = services.Single(s => s.Domain == "input_boolean" && s.Service == "toggle");
        toggle.Target.ShouldNotBeNull();
        toggle.Target!["entity"].ShouldNotBeNull();

        // homeassistant.restart is fire-and-forget — no target block in the HA response.
        var restart = services.SingleOrDefault(s => s.Domain == "homeassistant" && s.Service == "restart");
        if (restart is not null)
        {
            restart.Target.ShouldBeNull();
        }
    }

    [Fact]
    public async Task CallServiceAsync_TogglesEntityAndReturnsChangedEntity()
    {
        var client = fixture.CreateClient();
        var initial = await client.GetStateAsync(HomeAssistantFixture.TestEntityId);
        initial.ShouldNotBeNull();
        var before = initial!.State;

        var result = await client.CallServiceAsync(
            domain: "input_boolean",
            service: "toggle",
            entityId: HomeAssistantFixture.TestEntityId,
            data: null);

        result.ChangedEntities.ShouldContain(e => e.EntityId == HomeAssistantFixture.TestEntityId);

        var after = await client.GetStateAsync(HomeAssistantFixture.TestEntityId);
        after.ShouldNotBeNull();
        after!.State.ShouldNotBe(before);
    }

    [Fact]
    public async Task CallServiceAsync_ResponseSupportingService_ReturnsServiceResponse()
    {
        var client = fixture.CreateClient();

        var data = new Dictionary<string, JsonNode?>
        {
            ["value"] = JsonValue.Create("hello-from-test")
        };

        var result = await client.CallServiceAsync(
            domain: "script",
            service: "echo",
            entityId: null,
            data: data);

        result.Response.ShouldNotBeNull();
        result.Response!["echoed"]!.GetValue<string>().ShouldBe("hello-from-test");
    }

    [Fact]
    public async Task RenderTemplateAsync_ReturnsRenderedString()
    {
        var client = fixture.CreateClient();

        // {{ now() }} is a stable HA built-in that returns a non-empty rendered value
        // regardless of the user's HA config.
        var rendered = await client.RenderTemplateAsync("{{ now() }}");

        rendered.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SetupSummary_BuildsAgainstRealHA_IncludesExpectedSections()
    {
        var client = fixture.CreateClient();
        var provider = new global::Domain.Tools.HomeAssistant.Vfs.HaCatalogProvider(() => client);
        var summary = new global::Domain.Prompts.HomeAssistantSetupSummary(provider);

        var rendered = await summary.GetAsync(CancellationToken.None);

        output.WriteLine(rendered);

        rendered.ShouldContain("## Current Home Assistant setup");
        // Each entity is listed once, as a bare segment under its room heading; the header
        // carries the rule for rebuilding either full path form.
        rendered.ShouldContain("### ");
        rendered.ShouldContain("/ha/areas/<room>/<entry>");
        // Both seeded entities — the input_boolean and the script — must still appear.
        rendered.ShouldContain("input_boolean.");
        rendered.ShouldContain("script.");
        rendered.ShouldNotContain("/ha/entities/input_boolean/");
    }

    // The whole calendar round trip against the real component: created over the websocket with a
    // recurrence rule, listed by the calendars endpoint with the uid, deleted by that uid, gone.
    [Fact]
    public async Task CalendarEvents_CreateListDelete_RoundTripsThroughTheRealComponent()
    {
        var client = fixture.CreateClient();
        var tag = Guid.NewGuid().ToString("N")[..6];
        const string window = "2031-03-01T00:00:00Z";
        const string windowEnd = "2031-03-31T00:00:00Z";

        await client.CreateCalendarEventAsync(HomeAssistantFixture.CalendarEntityId, new global::Domain.Contracts.HaCalendarEventDraft
        {
            Summary = $"Wake {tag}",
            Start = "2031-03-10 07:00:00",
            End = "2031-03-10 07:01:00",
            Description = """{"target":{"room":"bedroom"},"insistent":{}}""",
            Rrule = "FREQ=DAILY;COUNT=3"
        });

        var listed = await client.ListCalendarEventsAsync(HomeAssistantFixture.CalendarEntityId, window, windowEnd);
        var mine = listed.Where(e => e.Summary == $"Wake {tag}").ToList();
        mine.Count.ShouldBe(3, "a COUNT=3 rule expands to three occurrences in the window");
        mine.Select(e => e.Uid).Distinct().Count().ShouldBe(1, "every occurrence carries the master's uid");
        mine[0].Uid.ShouldNotBeNullOrWhiteSpace();
        mine[0].Rrule.ShouldBe("FREQ=DAILY;COUNT=3");
        mine[0].Description.ShouldNotBeNull().ShouldContain("insistent");
        mine[0].Start.ShouldStartWith("2031-03-10T07:00:00");
        mine[0].AllDay.ShouldBeFalse();
        mine.Select(e => e.RecurrenceId).ShouldAllBe(id => !string.IsNullOrEmpty(id));

        await client.DeleteCalendarEventAsync(HomeAssistantFixture.CalendarEntityId, mine[0].Uid);

        var after = await client.ListCalendarEventsAsync(HomeAssistantFixture.CalendarEntityId, window, windowEnd);
        after.ShouldNotContain(e => e.Summary == $"Wake {tag}");
    }

    // The recorder against the real component: toggle the seeded entity, then read the window back
    // and find the flips in it, in order, on the instants they happened.
    [Fact]
    public async Task ListHistoryAsync_ReadsTheChangesTheRecorderKept()
    {
        var client = fixture.CreateClient();
        var start = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

        await client.CallServiceAsync("input_boolean", "toggle", HomeAssistantFixture.TestEntityId, null);
        await client.CallServiceAsync("input_boolean", "toggle", HomeAssistantFixture.TestEntityId, null);
        var end = DateTimeOffset.UtcNow.AddMinutes(5).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

        // The recorder commits on its own schedule; the history read joins the queue, so a short
        // wait is what the real UI does too.
        IReadOnlyList<global::Domain.Contracts.HaStateChange> changes = [];
        foreach (var _ in Enumerable.Range(0, 20))
        {
            changes = await client.ListHistoryAsync(HomeAssistantFixture.TestEntityId, start, end);
            if (changes.Count >= 3)
            {
                break;
            }
            await Task.Delay(500);
        }

        output.WriteLine(string.Join("\n", changes.Select(c => $"{c.At:O} {c.State}")));
        changes.Count.ShouldBeGreaterThanOrEqualTo(3, "the state at the window's start plus two flips");
        changes.Select(c => c.State).ShouldAllBe(s => s == "on" || s == "off");
        changes[^1].State.ShouldNotBe(changes[^2].State, "the last two flips alternate");
        changes.Zip(changes.Skip(1)).ShouldAllBe(pair => pair.First.At <= pair.Second.At);
    }

    // Statistics against the real recorder: rows imported through the recorder's own WebSocket
    // command (the way an energy integration backfills; there is no service for it), then read back
    // by the command the mount uses.
    [Fact]
    public async Task ListStatisticsAsync_ReadsWhatTheRecorderCompiled()
    {
        var client = fixture.CreateClient();
        const string statisticId = "sensor.imported_temperature";

        await ImportStatisticsAsync(new JsonObject
        {
            ["type"] = "recorder/import_statistics",
            ["metadata"] = new JsonObject
            {
                ["statistic_id"] = statisticId,
                ["source"] = "recorder",
                ["name"] = null,
                ["unit_of_measurement"] = "°C",
                ["mean_type"] = 1,
                ["has_sum"] = false
            },
            ["stats"] = new JsonArray(
                new JsonObject { ["start"] = "2031-03-10T07:00:00+00:00", ["mean"] = 20.5, ["min"] = 19.0, ["max"] = 22.0 },
                new JsonObject { ["start"] = "2031-03-10T08:00:00+00:00", ["mean"] = 21.0, ["min"] = 20.0, ["max"] = 22.5 })
        });

        // The import is queued on the recorder thread and the read joins the same queue, but a
        // slow commit can still be overtaken, so a short retry is honest rather than a race.
        IReadOnlyList<global::Domain.Contracts.HaStatisticsRow> rows = [];
        foreach (var _ in Enumerable.Range(0, 20))
        {
            rows = await client.ListStatisticsAsync(statisticId, "2031-03-10T00:00:00Z", "2031-03-11T00:00:00Z", "hour");
            if (rows.Count == 2)
            {
                break;
            }
            await Task.Delay(500);
        }

        rows.Count.ShouldBe(2);
        rows[0].Start.ShouldBe(DateTimeOffset.Parse("2031-03-10T07:00:00+00:00"));
        rows[0].End.ShouldBe(DateTimeOffset.Parse("2031-03-10T08:00:00+00:00"));
        rows[0].Mean.ShouldBe(20.5);
        rows[0].Min.ShouldBe(19.0);
        rows[0].Max.ShouldBe(22.0);
        rows[0].Sum.ShouldBeNull();
        rows[1].Mean.ShouldBe(21.0);
    }

    // One authenticated WebSocket command, the protocol the client speaks, for a command the client
    // has no reason to offer.
    private async Task ImportStatisticsAsync(JsonObject command)
    {
        using var socket = new System.Net.WebSockets.ClientWebSocket();
        var uri = new Uri(fixture.BaseUrl.Replace("http://", "ws://") + "/api/websocket");
        await socket.ConnectAsync(uri, CancellationToken.None);

        (await ReceiveAsync(socket))["type"]!.GetValue<string>().ShouldBe("auth_required");
        await SendAsync(socket, new JsonObject { ["type"] = "auth", ["access_token"] = fixture.Token });
        (await ReceiveAsync(socket))["type"]!.GetValue<string>().ShouldBe("auth_ok");

        command["id"] = 1;
        await SendAsync(socket, command);
        var answer = await ReceiveAsync(socket);
        answer["success"]!.GetValue<bool>().ShouldBeTrue(answer.ToJsonString());
    }

    private static async Task SendAsync(System.Net.WebSockets.ClientWebSocket socket, JsonObject frame) =>
        await socket.SendAsync(
            System.Text.Encoding.UTF8.GetBytes(frame.ToJsonString()),
            System.Net.WebSockets.WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonObject> ReceiveAsync(System.Net.WebSockets.ClientWebSocket socket)
    {
        var buffer = new byte[64 * 1024];
        using var text = new MemoryStream();
        System.Net.WebSockets.WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            text.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonNode.Parse(System.Text.Encoding.UTF8.GetString(text.ToArray()))!.AsObject();
    }

    [Fact]
    public async Task DeleteCalendarEventAsync_UnknownUid_ThrowsWithTheComponentsReason()
    {
        var client = fixture.CreateClient();

        var ex = await Should.ThrowAsync<HomeAssistantException>(
            () => client.DeleteCalendarEventAsync(HomeAssistantFixture.CalendarEntityId, "no-such-uid"));

        output.WriteLine(ex.Message);
        ex.ShouldNotBeOfType<HomeAssistantUnauthorizedException>();
    }

    [Fact]
    public async Task ListStatesAsync_BadToken_ThrowsUnauthorized()
    {
        var http = new HttpClient { BaseAddress = new Uri(fixture.BaseUrl + "/") };
        var bad = new HomeAssistantClient(http, "definitely-not-a-valid-token");

        await Should.ThrowAsync<HomeAssistantUnauthorizedException>(() => bad.ListStatesAsync());
    }
}