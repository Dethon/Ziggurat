using Domain.Exceptions;
using Infrastructure.Clients.HomeAssistant;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Tests.Unit.Infrastructure;

// GET /api/history/period/{start} is the one read of the recorder: the service catalog has nothing
// that answers "what was this entity's state over the last day". Asked minimally (state and
// last_changed only, no attributes) it answers one array per entity, whose first element is the
// state at the window's start and the rest are the changes inside it.
public class HomeAssistantClientHistoryTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly HomeAssistantClient _client;

    public HomeAssistantClientHistoryTests()
    {
        _server = WireMockServer.Start();
        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        _client = new HomeAssistantClient(http, "test-token");
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task ListHistoryAsync_AsksMinimally_AndReadsStateAndInstant()
    {
        _server.Given(Request.Create()
                .WithPath("/api/history/period/2026-09-03 16:00:00")
                .WithParam("filter_entity_id", "sensor.glucose")
                .WithParam("end_time", "2026-09-04 16:00:00")
                .WithParam("minimal_response")
                .WithParam("no_attributes")
                .WithHeader("Authorization", "Bearer test-token")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""
                    [[
                      {"entity_id":"sensor.glucose","state":"104","attributes":{},"last_changed":"2026-09-04T12:35:31.584749+00:00","last_updated":"2026-09-04T12:35:31.584749+00:00"},
                      {"state":"102","last_changed":"2026-09-04T12:36:30.881304+00:00"},
                      {"state":"100","last_changed":"2026-09-04T12:41:22.034522+00:00"}
                    ]]
                    """));

        var changes = await _client.ListHistoryAsync("sensor.glucose", "2026-09-03 16:00:00", "2026-09-04 16:00:00");

        changes.Count.ShouldBe(3);
        changes[0].State.ShouldBe("104");
        changes[0].At.ShouldBe(DateTimeOffset.Parse("2026-09-04T12:35:31.584749+00:00"));
        changes[2].State.ShouldBe("100");
        changes[2].At.ShouldBe(DateTimeOffset.Parse("2026-09-04T12:41:22.034522+00:00"));
    }

    // An entity the recorder never saw answers an empty outer array, not an empty inner one.
    [Fact]
    public async Task ListHistoryAsync_NothingRecorded_IsEmpty()
    {
        _server.Given(Request.Create().WithPath("/api/history/period/2026-09-03 16:00:00").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("[]"));

        var changes = await _client.ListHistoryAsync("sensor.nothing", "2026-09-03 16:00:00", "2026-09-04 16:00:00");

        changes.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListHistoryAsync_BadStart_ThrowsWith400()
    {
        _server.Given(Request.Create().WithPath("/api/history/period/nonsense").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(400).WithBody("""{"message":"Invalid datetime"}"""));

        var ex = await Should.ThrowAsync<HomeAssistantException>(
            () => _client.ListHistoryAsync("sensor.glucose", "nonsense", "2026-09-04 16:00:00"));

        ex.StatusCode.ShouldBe(400);
    }
}