using Domain.DTOs.Voice;
using Infrastructure.Timers;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class InMemoryTimerStoreTests
{
    private const string Home = "0a1b";

    private static ArmedTimer Timer(string id, DateTime firesAtUtc, string household = Home) => new()
    {
        Id = id,
        TenantId = household,
        Target = new AnnounceTarget { Room = "Kitchen" },
        DurationSeconds = 300,
        CreatedAtUtc = firesAtUtc.AddSeconds(-300),
        FiresAtUtc = firesAtUtc
    };

    [Fact]
    public async Task TakeDueAsync_RemovesAndReturnsOnlyDueTimers()
    {
        var store = new InMemoryTimerStore();
        var now = DateTime.UtcNow;
        await store.ArmAsync(Timer("due", now.AddSeconds(-1)));
        await store.ArmAsync(Timer("later", now.AddMinutes(5)));

        var due = await store.TakeDueAsync(now);

        due.ShouldHaveSingleItem();
        due[0].Id.ShouldBe("due");
        (await store.GetAsync(Home, "due")).ShouldBeNull();          // removed — fires once
        (await store.GetAsync(Home, "later")).ShouldNotBeNull();
        (await store.TakeDueAsync(now)).ShouldBeEmpty();
    }

    [Fact]
    public async Task CancelAsync_RemovesTimer_AndReportsMisses()
    {
        var store = new InMemoryTimerStore();
        await store.ArmAsync(Timer("pasta", DateTime.UtcNow.AddMinutes(5)));

        (await store.CancelAsync(Home, "pasta")).ShouldBeTrue();
        (await store.CancelAsync(Home, "pasta")).ShouldBeFalse();
        (await store.ListAsync(Home)).ShouldBeEmpty();
    }

    // An id is the model's to choose, so two households' "pasta" are two timers: arming one never
    // replaces the other, and each is listed, read and cancelled by its own household alone.
    [Fact]
    public async Task TwoHouseholdsTimersOfTheSameId_AreTwoTimers()
    {
        var store = new InMemoryTimerStore();
        var now = DateTime.UtcNow;
        await store.ArmAsync(Timer("pasta", now.AddMinutes(5)));
        await store.ArmAsync(Timer("pasta", now.AddMinutes(9), household: "0c2d"));

        (await store.CancelAsync(Home, "pasta")).ShouldBeTrue();

        (await store.ListAsync(Home)).ShouldBeEmpty();
        (await store.GetAsync("0c2d", "pasta")).ShouldNotBeNull().FiresAtUtc.ShouldBe(now.AddMinutes(9));
        (await store.TakeDueAsync(now.AddMinutes(10))).ShouldHaveSingleItem().TenantId.ShouldBe("0c2d");
    }

    [Fact]
    public async Task ListAsync_OrdersByFireTime()
    {
        var store = new InMemoryTimerStore();
        var now = DateTime.UtcNow;
        await store.ArmAsync(Timer("second", now.AddMinutes(10)));
        await store.ArmAsync(Timer("first", now.AddMinutes(1)));

        (await store.ListAsync(Home)).Select(t => t.Id).ShouldBe(["first", "second"]);
    }
}