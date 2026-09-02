using Domain.Exceptions;
using Infrastructure.Clients.HomeAssistant;
using Shouldly;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Tests.Unit.Infrastructure;

// GET /api/calendars/{entity} is the one read that carries an event's uid — the get_events
// service's response is filtered down to start/end/summary/description/location/status, which is
// why an agent could never address an event to delete it.
public class HomeAssistantClientCalendarTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly HomeAssistantClient _client;

    public HomeAssistantClientCalendarTests()
    {
        _server = WireMockServer.Start();
        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        _client = new HomeAssistantClient(http, "test-token");
    }

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task ListCalendarEventsAsync_ReadsUidRruleAndBothInstantShapes()
    {
        _server.Given(Request.Create()
                .WithPath("/api/calendars/calendar.alarms")
                .WithParam("start", "2026-09-02 00:00:00")
                .WithParam("end", "2026-09-09 00:00:00")
                .WithHeader("Authorization", "Bearer test-token")
                .UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithBody("""
                    [
                      {"start":{"dateTime":"2026-09-02T10:30:00+02:00"},"end":{"dateTime":"2026-09-02T10:31:00+02:00"},
                       "summary":"Llama al administrador","description":"{\"target\":{\"satelliteId\":\"FRAN-OFFICE-01\"},\"insistent\":{}}",
                       "location":null,"uid":"fd96f9b2-a666-11f1-8c54-ce24ff4dd9fd","recurrence_id":null,"rrule":null},
                      {"start":{"date":"2026-09-05"},"end":{"date":"2026-09-06"},
                       "summary":"Gym","description":null,"location":null,"uid":"gym","recurrence_id":"20260905","rrule":"FREQ=WEEKLY"}
                    ]
                    """));

        var events = await _client.ListCalendarEventsAsync("calendar.alarms", "2026-09-02 00:00:00", "2026-09-09 00:00:00");

        events.Count.ShouldBe(2);
        events[0].Uid.ShouldBe("fd96f9b2-a666-11f1-8c54-ce24ff4dd9fd");
        events[0].Start.ShouldBe("2026-09-02T10:30:00+02:00");
        events[0].AllDay.ShouldBeFalse();
        events[0].Description.ShouldContain("FRAN-OFFICE-01");
        events[0].Rrule.ShouldBeNull();
        events[1].AllDay.ShouldBeTrue();
        events[1].Start.ShouldBe("2026-09-05");
        events[1].Rrule.ShouldBe("FREQ=WEEKLY");
        events[1].RecurrenceId.ShouldBe("20260905");
    }

    // Home Assistant answers a window it cannot parse, or one that ends before it starts, with a
    // bare 400 — surfaced as the same exception the service path raises for a rejected payload.
    [Fact]
    public async Task ListCalendarEventsAsync_BadWindow_ThrowsWith400()
    {
        _server.Given(Request.Create().WithPath("/api/calendars/calendar.alarms").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(400));

        var ex = await Should.ThrowAsync<HomeAssistantException>(
            () => _client.ListCalendarEventsAsync("calendar.alarms", "nonsense", "2026-09-09 00:00:00"));

        ex.StatusCode.ShouldBe(400);
    }
}