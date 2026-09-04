using System.Text.Json.Nodes;
using Domain.Exceptions;
using Infrastructure.Clients.HomeAssistant;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Clients;

// The statistics read is a WebSocket command (`recorder/statistics_during_period`), so it runs
// against the server that speaks the protocol, the way the calendar's commands do. The rows come
// back as Home Assistant sends them: `start`/`end` as epoch milliseconds, the measures nullable —
// a measured sensor has mean/min/max and a total has state/sum/change.
public class HomeAssistantStatisticsClientTests
{
    private static HomeAssistantClient Client(FakeHomeAssistantSocket server) =>
        new(new HttpClient { BaseAddress = new Uri(server.BaseUrl + "/") }, FakeHomeAssistantSocket.ValidToken);

    [Fact]
    public async Task ListStatisticsAsync_SendsTheCommand_AndReadsTheRows()
    {
        await using var server = await FakeHomeAssistantSocket.StartAsync();
        server.Statistics["sensor.temperature"] =
        [
            new JsonObject { ["start"] = 1788386400000, ["end"] = 1788390000000, ["min"] = 28.0, ["max"] = 29.0, ["last_reset"] = null, ["mean"] = 28.5 },
            new JsonObject { ["start"] = 1788390000000, ["end"] = 1788393600000, ["min"] = 28.0, ["max"] = 28.0, ["last_reset"] = null, ["mean"] = 28.0 }
        ];

        var rows = await Client(server).ListStatisticsAsync("sensor.temperature", "2026-09-02 00:00:00", "2026-09-04T12:00:00+00:00", "hour");

        rows.Count.ShouldBe(2);
        rows[0].Start.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1788386400000));
        rows[0].End.ShouldBe(DateTimeOffset.FromUnixTimeMilliseconds(1788390000000));
        rows[0].Mean.ShouldBe(28.5);
        rows[0].Min.ShouldBe(28.0);
        rows[0].Max.ShouldBe(29.0);
        rows[0].Sum.ShouldBeNull();
        var request = server.LastStatisticsRequest.ShouldNotBeNull();
        request.StatisticIds.ShouldBe(["sensor.temperature"]);
        request.Start.ShouldBe("2026-09-02 00:00:00");
        request.End.ShouldBe("2026-09-04T12:00:00+00:00");
        request.Period.ShouldBe("hour");
    }

    [Fact]
    public async Task ListStatisticsAsync_ATotal_CarriesSumAndChange()
    {
        await using var server = await FakeHomeAssistantSocket.StartAsync();
        server.Statistics["sensor.energy"] =
        [
            new JsonObject { ["start"] = 1788386400000, ["end"] = 1788390000000, ["last_reset"] = null, ["state"] = 1200.5, ["sum"] = 1200.5, ["change"] = 0.7 }
        ];

        var rows = await Client(server).ListStatisticsAsync("sensor.energy", "2026-09-02 00:00:00", "2026-09-04 00:00:00", "hour");

        rows.ShouldHaveSingleItem();
        rows[0].Mean.ShouldBeNull();
        rows[0].State.ShouldBe(1200.5);
        rows[0].Sum.ShouldBe(1200.5);
        rows[0].Change.ShouldBe(0.7);
    }

    // An id the recorder has no statistics for is simply absent from the result object.
    [Fact]
    public async Task ListStatisticsAsync_NothingCompiled_IsEmpty()
    {
        await using var server = await FakeHomeAssistantSocket.StartAsync();

        var rows = await Client(server).ListStatisticsAsync("sensor.nothing", "2026-09-02 00:00:00", "2026-09-04 00:00:00", "hour");

        rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListStatisticsAsync_BadStart_ThrowsWithHomeAssistantsReason()
    {
        await using var server = await FakeHomeAssistantSocket.StartAsync();

        var ex = await Should.ThrowAsync<HomeAssistantException>(
            () => Client(server).ListStatisticsAsync("sensor.temperature", "nonsense", "2026-09-04 00:00:00", "hour"));

        ex.Message.ShouldContain("Invalid start_time");
    }
}