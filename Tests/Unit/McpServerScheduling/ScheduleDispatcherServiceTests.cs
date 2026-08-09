using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Validation;
using Mcp.Hosting;
using McpServerScheduling.Services;
using McpServerScheduling.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Unit.Mcp.Hosting;
using Xunit;

namespace Tests.Unit.McpServerScheduling;

public class ScheduleDispatcherServiceTests
{
    [Fact]
    public async Task DispatchDueAsync_RecurringSchedule_RecomputesNextRunInLocalZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("test-plus2", TimeSpan.FromHours(2), "p2", "p2");
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        clock.SetLocalTimeZone(zone);
        var store = StoreWithDue(Recurring());
        var emitter = Probe(delivers: true);

        await BuildDispatcher(store.Object, emitter, new CronValidator(), clock).DispatchDueAsync(CancellationToken.None);

        // "0 8 * * *" at 08:00 local (+02:00) = 06:00 UTC; now is 00:00Z so next occurrence is same day
        store.Verify(
            s => s.UpdateLastRunAsync(
                "daily", It.IsAny<DateTime?>(), new DateTime(2026, 7, 1, 6, 0, 0, DateTimeKind.Utc), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DispatchDueAsync_WhenEmitFails_DoesNotMutateStore()
    {
        var oneShot = OneShot();
        var store = StoreWithDue(oneShot);
        var emitter = Probe(delivers: false);

        await BuildDispatcher(store.Object, emitter).DispatchDueAsync(CancellationToken.None);

        store.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(
            s => s.UpdateLastRunAsync(
                It.IsAny<string>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DispatchDueAsync_WhenEmitSucceeds_DeletesOneShotSchedule()
    {
        var store = StoreWithDue(OneShot());
        var emitter = Probe(delivers: true);

        await BuildDispatcher(store.Object, emitter).DispatchDueAsync(CancellationToken.None);

        store.Verify(s => s.DeleteAsync("once", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task DispatchDueAsync_WhenEmitSucceeds_AdvancesRecurringSchedule()
    {
        var store = StoreWithDue(Recurring());
        var next = new DateTime(2026, 5, 26, 8, 0, 0, DateTimeKind.Utc);
        var cron = new Mock<ICronValidator>();
        cron.Setup(c => c.GetNextOccurrence("0 8 * * *", It.IsAny<DateTimeOffset>(), It.IsAny<TimeZoneInfo>())).Returns(next);

        await BuildDispatcher(store.Object, Probe(delivers: true), cron.Object).DispatchDueAsync(CancellationToken.None);

        store.Verify(s => s.UpdateLastRunAsync("daily", It.IsAny<DateTime?>(), next, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // One warning per outage, not one per schedule per tick: an overnight disconnection with a
    // single due schedule used to produce thousands of identical lines. The retry semantics are
    // untouched — the schedule stays due either way.
    [Fact]
    public async Task DispatchDueAsync_AgentStaysDownAcrossTicks_WarnsOnce()
    {
        var store = StoreWithDue(OneShot());
        using var logs = CapturingLoggerProvider.ForLevel(LogLevel.Warning);
        var dispatcher = BuildDispatcher(store.Object, Probe(delivers: false), logs: logs);

        await dispatcher.DispatchDueAsync(CancellationToken.None);
        await dispatcher.DispatchDueAsync(CancellationToken.None);
        await dispatcher.DispatchDueAsync(CancellationToken.None);

        logs.Messages.ShouldHaveSingleItem().ShouldContain("until delivery resumes");
    }

    // The other half of the muted warning: an operator who saw the outage line gets exactly one
    // line saying it ended, and a healthy dispatcher never mentions it at all.
    [Fact]
    public async Task DispatchDueAsync_DeliveryResumes_SaysSoOnce()
    {
        var store = StoreWithDue(OneShot());
        var probe = Probe(delivers: false);
        using var logs = CapturingLoggerProvider.ForLevel(LogLevel.Information);
        var dispatcher = BuildDispatcher(store.Object, probe, logs: logs);
        await dispatcher.DispatchDueAsync(CancellationToken.None);

        probe.GoLive();
        await dispatcher.DispatchDueAsync(CancellationToken.None);
        await dispatcher.DispatchDueAsync(CancellationToken.None);

        logs.Messages.Count(m => m.Contains("resumed")).ShouldBe(1);
    }

    // "Once per outage" has to hold for the second outage too. Recovery is not always a delivered
    // fire: the due one-shot is often gone by the time the agent is back, so the dispatcher sees
    // only quiet ticks. If the latch waited for a successful emit to clear, the next outage would
    // be silent — the one an operator most needs to see.
    [Fact]
    public async Task DispatchDueAsync_ASecondOutageAfterAQuietRecovery_WarnsAgain()
    {
        var due = new List<Schedule> { OneShot() };
        var store = new Mock<IScheduleStore>();
        store.Setup(s => s.GetDueSchedulesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => due);
        using var logs = CapturingLoggerProvider.ForLevel(LogLevel.Warning);
        var dispatcher = BuildDispatcher(store.Object, Probe(delivers: false), logs: logs);

        await dispatcher.DispatchDueAsync(CancellationToken.None);

        // The agent comes back with nothing due — the fire it missed was handled elsewhere.
        due.Clear();
        await dispatcher.DispatchDueAsync(CancellationToken.None);

        // And goes away again.
        due.Add(OneShot());
        await dispatcher.DispatchDueAsync(CancellationToken.None);

        logs.Messages.Count(m => m.Contains("until delivery resumes")).ShouldBe(2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ResolveInterval_NonPositive_ClampsToOneSecond(int seconds) =>
        ScheduleDispatcherService.ResolveInterval(seconds).ShouldBe(TimeSpan.FromSeconds(1));

    [Fact]
    public void ResolveInterval_Positive_IsUnchanged() =>
        ScheduleDispatcherService.ResolveInterval(30).ShouldBe(TimeSpan.FromSeconds(30));

    // The return value is what lets the loop back off the schedule-store query without a separate
    // liveness property: a failed delivery says so directly.
    [Fact]
    public async Task DispatchDueAsync_NoLiveSubscriber_ReturnsFalse()
    {
        var store = StoreWithDue(OneShot());

        var delivered = await BuildDispatcher(store.Object, Probe(delivers: false)).DispatchDueAsync(CancellationToken.None);

        delivered.ShouldBeFalse();
    }

    [Fact]
    public async Task DispatchDueAsync_DeliveredSuccessfully_ReturnsTrue()
    {
        var store = StoreWithDue(OneShot());

        var delivered = await BuildDispatcher(store.Object, Probe(delivers: true)).DispatchDueAsync(CancellationToken.None);

        delivered.ShouldBeTrue();
    }

    [Fact]
    public async Task DispatchDueAsync_NothingDue_ReturnsTrue()
    {
        var store = new Mock<IScheduleStore>();
        store.Setup(s => s.GetDueSchedulesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var delivered = await BuildDispatcher(store.Object, Probe(delivers: false)).DispatchDueAsync(CancellationToken.None);

        delivered.ShouldBeTrue();
    }

    // The whole fix: while nobody is listening, the loop waits the backed-off interval instead of
    // the normal one, so a tick that would just be told "no" again by the emitter never happens.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NextDelay_UsesTheBackedOffIntervalOnlyWhenNothingWasDelivered(bool delivered)
    {
        var interval = TimeSpan.FromSeconds(30);

        var next = ScheduleDispatcherService.NextDelay(interval, delivered);

        next.ShouldBe(delivered ? interval : interval * ScheduleDispatcherService.IdleBackoffMultiplier);
    }

    private static Schedule OneShot() =>
        new() { Id = "once", AgentId = "jack", Prompt = "p", RunAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow };

    private static Schedule Recurring() =>
        new() { Id = "daily", AgentId = "jonas", Prompt = "p", CronExpression = "0 8 * * *", CreatedAt = DateTime.UtcNow };

    private static Mock<IScheduleStore> StoreWithDue(Schedule schedule)
    {
        var store = new Mock<IScheduleStore>();
        store.Setup(s => s.GetDueSchedulesAsync(It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([schedule]);
        return store;
    }

    // A real inbox and the real emitter rather than a mock: "delivers" is expressed the way
    // production expresses it — whether anyone has actually polled — so the test cannot claim a
    // combination the shared emitter would never produce.
    private static ChannelInboxProbe Probe(bool delivers) =>
        new("scheduling", DeliveryPolicy.GateOnLive, live: delivers);

    private static ScheduleDispatcherService BuildDispatcher(
        IScheduleStore store,
        ChannelInboxProbe emitter,
        ICronValidator? cron = null,
        TimeProvider? clock = null,
        CapturingLoggerProvider? logs = null) =>
        new(
            store,
            cron ?? new Mock<ICronValidator>().Object,
            emitter.Emitter,
            new SchedulingSettings { RedisConnectionString = "x", DefaultDeliverTo = ["signalr"] },
            logs is null
                ? new Mock<ILogger<ScheduleDispatcherService>>().Object
                : new LoggerFactory([logs]).CreateLogger<ScheduleDispatcherService>(),
            clock ?? TimeProvider.System);
}