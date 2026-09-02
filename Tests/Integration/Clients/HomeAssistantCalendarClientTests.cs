using Domain.Contracts;
using Domain.Exceptions;
using Infrastructure.Clients.HomeAssistant;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// Runs the real client's calendar commands against a server that speaks Home Assistant's websocket
// protocol. The REST listing is covered by the unit suite (WireMock) and the whole round trip by
// HomeAssistantClientTests against a real container.
public class HomeAssistantCalendarClientTests
{
    private const string Alarms = "calendar.alarms";

    private static HomeAssistantClient Client(FakeHomeAssistantSocket server, string? token = null) =>
        new(new HttpClient { BaseAddress = new Uri(server.BaseUrl + "/") }, token ?? FakeHomeAssistantSocket.ValidToken);

    [Fact]
    public async Task CreateCalendarEventAsync_SendsTheWebSocketCreate_WithTheDraftsFields()
    {
        await using var server = await FakeHomeAssistantSocket.StartAsync();

        await Client(server).CreateCalendarEventAsync(Alarms, new HaCalendarEventDraft
        {
            Summary = "Wake up",
            Start = "2026-09-03 07:00:00",
            End = "2026-09-03 07:01:00",
            Description = """{"target":{"room":"bedroom"},"insistent":{}}""",
            Rrule = "FREQ=DAILY"
        });

        var created = server.Calendar.Events.ShouldHaveSingleItem();
        created.EntityId.ShouldBe(Alarms);
        created.Summary.ShouldBe("Wake up");
        created.Start.ShouldBe("2026-09-03 07:00:00");
        created.End.ShouldBe("2026-09-03 07:01:00");
        created.Description.ShouldContain("insistent");
        created.Rrule.ShouldBe("FREQ=DAILY");
        server.AuthCount.ShouldBe(1);
    }

    [Fact]
    public async Task DeleteCalendarEventAsync_RemovesTheEventByUid()
    {
        await using var server = await FakeHomeAssistantSocket.StartAsync();
        var seeded = server.Calendar.Seed(Alarms, "Trash", "2026-09-02 21:30:00", "2026-09-02 21:31:00");

        await Client(server).DeleteCalendarEventAsync(Alarms, seeded.Uid);

        server.Calendar.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task DeleteCalendarEventAsync_UnknownUid_ThrowsWithHomeAssistantsReason()
    {
        await using var server = await FakeHomeAssistantSocket.StartAsync();

        var ex = await Should.ThrowAsync<HomeAssistantException>(
            () => Client(server).DeleteCalendarEventAsync(Alarms, "nope"));

        ex.Message.ShouldContain("nope not found");
    }

    [Fact]
    public async Task DeleteCalendarEventAsync_UnknownCalendar_ThrowsNotFound()
    {
        await using var server = await FakeHomeAssistantSocket.StartAsync();
        server.KnownCalendars.Add(Alarms);

        await Should.ThrowAsync<HomeAssistantNotFoundException>(
            () => Client(server).DeleteCalendarEventAsync("calendar.other", "x"));
    }

    [Fact]
    public async Task DeleteCalendarEventAsync_BadToken_ThrowsUnauthorized()
    {
        await using var server = await FakeHomeAssistantSocket.StartAsync();

        await Should.ThrowAsync<HomeAssistantUnauthorizedException>(
            () => Client(server, "wrong").DeleteCalendarEventAsync(Alarms, "x"));
    }
}