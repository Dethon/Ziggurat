using System.Text.Json.Nodes;
using Domain.Exceptions;
using Infrastructure.Clients.HomeAssistant;
using Shouldly;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Tests.Unit.Infrastructure;

// The automation config API is how a watch reaches the home: `/api/config/automation/config/{id}`
// is read, written (create or replace, which reloads the automation) and deleted, and the
// automations' entity states carry what the config cannot — whether each is on and when it last
// fired. These pin the HTTP shape the way the calendar and history suites do.
public class HomeAssistantClientAutomationTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly HomeAssistantClient _client;

    public HomeAssistantClientAutomationTests()
    {
        _server = WireMockServer.Start();
        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        _client = new HomeAssistantClient(http, "test-token");
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task ListAutomationsAsync_ReadsTheAutomationEntities_WithTheirConfigIdStateAndLastTriggered()
    {
        _server.Given(Request.Create().WithPath("/api/states").WithHeader("Authorization", "Bearer test-token").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""
                    [
                      {"entity_id":"light.kitchen","state":"on","attributes":{}},
                      {"entity_id":"automation.laura_s_sugar","state":"on",
                       "attributes":{"id":"assistant_watch_laura-sugar-high","last_triggered":"2026-09-05T02:14:00+00:00","mode":"single","friendly_name":"Laura's sugar"}},
                      {"entity_id":"automation.voice_alarm_bridge","state":"off",
                       "attributes":{"id":"voice_alarm_bridge","last_triggered":null,"friendly_name":"Voice alarm bridge"}},
                      {"entity_id":"automation.legacy_yaml","state":"on","attributes":{"friendly_name":"Legacy"}}
                    ]
                    """));

        var automations = await _client.ListAutomationsAsync();

        automations.Count.ShouldBe(3);
        var watch = automations.Single(a => a.EntityId == "automation.laura_s_sugar");
        watch.ConfigId.ShouldBe("assistant_watch_laura-sugar-high");
        watch.IsOn.ShouldBeTrue();
        watch.FriendlyName.ShouldBe("Laura's sugar");
        watch.LastTriggered.ShouldBe(new DateTimeOffset(2026, 9, 5, 2, 14, 0, TimeSpan.Zero));
        var bridge = automations.Single(a => a.EntityId == "automation.voice_alarm_bridge");
        bridge.IsOn.ShouldBeFalse();
        bridge.LastTriggered.ShouldBeNull();
        automations.Single(a => a.EntityId == "automation.legacy_yaml").ConfigId.ShouldBeNull();
    }

    [Fact]
    public async Task GetAutomationConfigAsync_ReadsTheConfigById()
    {
        _server.Given(Request.Create().WithPath("/api/config/automation/config/assistant_watch_x").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""{"id":"assistant_watch_x","alias":"X","description":"{}","triggers":[],"actions":[],"mode":"single"}"""));

        var config = await _client.GetAutomationConfigAsync("assistant_watch_x");

        config.ShouldNotBeNull();
        config["alias"]!.GetValue<string>().ShouldBe("X");
        config["mode"]!.GetValue<string>().ShouldBe("single");
    }

    [Fact]
    public async Task GetAutomationConfigAsync_UnknownId_IsNull()
    {
        _server.Given(Request.Create().WithPath("/api/config/automation/config/nope").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("""{"message":"Resource not found"}"""));

        (await _client.GetAutomationConfigAsync("nope")).ShouldBeNull();
    }

    [Fact]
    public async Task UpsertAutomationConfigAsync_PostsTheConfigUnderItsId()
    {
        _server.Given(Request.Create().WithPath("/api/config/automation/config/assistant_watch_x")
                .WithHeader("Authorization", "Bearer test-token")
                .WithBody(new JsonPartialMatcher("""{"alias":"X","mode":"single"}"""))
                .UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""{"result":"ok"}"""));

        await _client.UpsertAutomationConfigAsync("assistant_watch_x", new JsonObject
        {
            ["alias"] = "X",
            ["mode"] = "single",
            ["triggers"] = new JsonArray(),
            ["actions"] = new JsonArray()
        });

        _server.LogEntries.ShouldHaveSingleItem();
    }

    // Home Assistant validates the config before it writes it and answers a 400 whose body names
    // what was wrong. That message is the whole point: the agent fixes its trigger in the same
    // turn, so it must travel as a typed rejection and never as a bare status code.
    [Fact]
    public async Task UpsertAutomationConfigAsync_RejectedConfig_ThrowsWithHomeAssistantsOwnMessage()
    {
        _server.Given(Request.Create().WithPath("/api/config/automation/config/assistant_watch_x").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/json")
                .WithBody("""{"message":"Message malformed: required key not provided @ data['triggers'][0]['trigger']"}"""));

        var ex = await Should.ThrowAsync<HomeAssistantConfigRejectedException>(
            () => _client.UpsertAutomationConfigAsync("assistant_watch_x", new JsonObject { ["alias"] = "X" }));

        ex.Message.ShouldBe("Message malformed: required key not provided @ data['triggers'][0]['trigger']");
        ex.StatusCode.ShouldBe(400);
    }

    [Fact]
    public async Task DeleteAutomationConfigAsync_DeletesById()
    {
        _server.Given(Request.Create().WithPath("/api/config/automation/config/assistant_watch_x").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"result":"ok"}"""));

        await _client.DeleteAutomationConfigAsync("assistant_watch_x");

        _server.LogEntries.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task DeleteAutomationConfigAsync_UnknownId_ThrowsNotFound()
    {
        _server.Given(Request.Create().WithPath("/api/config/automation/config/nope").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(404).WithBody("""{"message":"Resource not found"}"""));

        await Should.ThrowAsync<HomeAssistantNotFoundException>(() => _client.DeleteAutomationConfigAsync("nope"));
    }
}