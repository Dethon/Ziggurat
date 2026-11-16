using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Metrics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Observability.Hubs;
using Observability.Services;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Observability;

// What the operator's dashboard reads once events from two households and the deployment's own have
// been rolled up: each household's figures alone when one is asked for, and everybody's when none is.
public sealed class MetricsCollectorServiceHouseholdTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
    private static readonly DateTimeOffset _at = new(2026, 3, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly _day = DateOnly.FromDateTime(_at.UtcDateTime);

    private readonly string _home = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());
    private readonly string _other = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray());

    private MetricsCollectorService Collector() => new(
        fixture.Connection, Hub(), NullLogger<MetricsCollectorService>.Instance, new FakeTimeProvider(_at));

    private MetricsQueryService Query() => new(fixture.Connection, new FakeTimeProvider(_at));

    [Fact]
    public async Task TokenUsage_IsSummedPerHousehold_AndOverAllOfThemWhenNoneIsAsked()
    {
        var collector = Collector();
        await collector.ProcessEventAsync(Tokens(_home, 100), fixture.Connection.GetDatabase());
        await collector.ProcessEventAsync(Tokens(_other, 30), fixture.Connection.GetDatabase());
        await collector.ProcessEventAsync(Tokens(null, 5), fixture.Connection.GetDatabase());

        (await Query().GetSummaryAsync(_day, _day, _home)).InputTokens.ShouldBe(100);
        (await Query().GetSummaryAsync(_day, _day, _other)).InputTokens.ShouldBe(30);
        (await Query().GetSummaryAsync(_day, _day, MetricKeys.Deployment)).InputTokens.ShouldBe(5);
        (await Query().GetSummaryAsync(_day, _day)).InputTokens.ShouldBeGreaterThanOrEqualTo(135);
    }

    [Fact]
    public async Task AHouseholdsEvents_AreReadBackAsItsOwn()
    {
        var collector = Collector();
        await collector.ProcessEventAsync(Tokens(_home, 7), fixture.Connection.GetDatabase());
        await collector.ProcessEventAsync(Tokens(_other, 9), fixture.Connection.GetDatabase());

        var events = await Query().GetEventsAsync<TokenUsageEvent>(MetricKeys.Tokens, _day, _day, _other);

        events.ShouldHaveSingleItem().InputTokens.ShouldBe(9);
        events[0].TenantId.ShouldBe(_other);
    }

    [Fact]
    public async Task AFailedToolCallsError_CountsAgainstTheHouseholdWhoseCallItWas()
    {
        await Collector().ProcessEventAsync(
            new ToolCallEvent
            {
                ToolName = "mcp__vault__read", DurationMs = 3, Success = false, Error = "gone",
                TenantId = _home, Timestamp = _at
            },
            fixture.Connection.GetDatabase());

        (await Query().GetRecentErrorsAsync(household: _home)).ShouldHaveSingleItem().ErrorType.ShouldBe("mcp__vault__read");
        (await Query().GetRecentErrorsAsync(household: _other)).ShouldBeEmpty();
    }

    [Fact]
    public async Task TwoHouseholdsServersOfOneService_AreTwoRowsOfHealth()
    {
        var collector = Collector();
        await collector.ProcessEventAsync(Heartbeat(_home), fixture.Connection.GetDatabase());
        await collector.ProcessEventAsync(Heartbeat(_other), fixture.Connection.GetDatabase());

        var all = await Query().GetHealthAsync();
        all.Where(h => h.Service == "mcp-channel-voice").Select(h => h.Household)
            .ShouldBe([_home, _other], ignoreOrder: true);
        (await Query().GetHealthAsync(_home)).ShouldHaveSingleItem().ShouldSatisfyAllConditions(
            h => h.Service.ShouldBe("mcp-channel-voice"),
            h => h.IsHealthy.ShouldBeTrue());
    }

    [Fact]
    public async Task LemonadeUse_IsCountedPerHouseholdInEachOfItsUnits()
    {
        var collector = Collector();
        var db = fixture.Connection.GetDatabase();
        await collector.ProcessEventAsync(Lemonade(_home, LemonadeUse.Transcription, 2.5), db);
        await collector.ProcessEventAsync(Lemonade(_home, LemonadeUse.Transcription, 1.25), db);
        await collector.ProcessEventAsync(Lemonade(_home, LemonadeUse.Speech, 40), db);
        await collector.ProcessEventAsync(Lemonade(_other, LemonadeUse.Embedding, 3), db);

        (await Query().GetLemonadeUsageAsync(_day, _day, _home)).ShouldBe(new LemonadeUsage(3.75, 40, 0));
        (await Query().GetLemonadeUsageAsync(_day, _day, _other)).ShouldBe(new LemonadeUsage(0, 0, 3));
        var byHousehold = await Query().GetLemonadeByHouseholdAsync(_day, _day);
        byHousehold[_home].ShouldBe(new LemonadeUsage(3.75, 40, 0));
        byHousehold[_other].ShouldBe(new LemonadeUsage(0, 0, 3));
    }

    private static TokenUsageEvent Tokens(string? tenantId, int input) => new()
    {
        Sender = "fran", Model = "z-ai/glm-5", InputTokens = input, OutputTokens = 0, Cost = 0m,
        TenantId = tenantId, Timestamp = _at
    };

    private static HeartbeatEvent Heartbeat(string tenantId) =>
        new() { Service = "mcp-channel-voice", TenantId = tenantId, Timestamp = _at };

    private static LemonadeUsageEvent Lemonade(string tenantId, LemonadeUse use, double amount) =>
        new() { Use = use, Amount = amount, TenantId = tenantId, Timestamp = _at };

    private static IHubContext<MetricsHub> Hub()
    {
        var clients = new Mock<IHubClients>();
        clients.Setup(c => c.All).Returns(Mock.Of<IClientProxy>());
        var hub = new Mock<IHubContext<MetricsHub>>();
        hub.Setup(h => h.Clients).Returns(clients.Object);
        return hub.Object;
    }
}