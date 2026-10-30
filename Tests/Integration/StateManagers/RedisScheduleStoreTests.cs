using Domain.DTOs;
using Domain.DTOs.Channel;
using Infrastructure.StateManagers;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.StateManagers;

public class RedisScheduleStoreTests(RedisFixture fixture) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private readonly RedisScheduleStore _store = new(fixture.Connection);
    private readonly List<string> _createdIds = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var id in _createdIds)
        {
            await _store.DeleteAsync(id);
        }
    }

    [Fact]
    public async Task CreateAsync_StoresSchedule()
    {
        var schedule = CreateTestSchedule();
        _createdIds.Add(schedule.Id);

        var result = await _store.CreateAsync(schedule);

        result.Id.ShouldBe(schedule.Id);
        var stored = await _store.GetAsync(schedule.Id);
        stored.ShouldNotBeNull();
        stored.Prompt.ShouldBe(schedule.Prompt);
    }

    [Fact]
    public async Task GetAsync_NonExistent_ReturnsNull()
    {
        var result = await _store.GetAsync("non_existent_id");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ListAsync_ReturnsAllSchedules()
    {
        var schedule1 = CreateTestSchedule();
        var schedule2 = CreateTestSchedule();
        _createdIds.Add(schedule1.Id);
        _createdIds.Add(schedule2.Id);

        await _store.CreateAsync(schedule1);
        await _store.CreateAsync(schedule2);

        var list = await _store.ListAsync();

        list.ShouldContain(s => s.Id == schedule1.Id);
        list.ShouldContain(s => s.Id == schedule2.Id);
    }

    [Fact]
    public async Task ListAsync_ReturnsOrderedByNextRunAt()
    {
        var laterSchedule = CreateTestSchedule() with { NextRunAt = DateTime.UtcNow.AddHours(2) };
        var earlierSchedule = CreateTestSchedule() with { NextRunAt = DateTime.UtcNow.AddHours(1) };
        _createdIds.Add(laterSchedule.Id);
        _createdIds.Add(earlierSchedule.Id);

        await _store.CreateAsync(laterSchedule);
        await _store.CreateAsync(earlierSchedule);

        var list = await _store.ListAsync();

        var laterIndex = list.ToList().FindIndex(s => s.Id == laterSchedule.Id);
        var earlierIndex = list.ToList().FindIndex(s => s.Id == earlierSchedule.Id);
        earlierIndex.ShouldBeLessThan(laterIndex);
    }

    [Fact]
    public async Task DeleteAsync_RemovesSchedule()
    {
        var schedule = CreateTestSchedule();
        await _store.CreateAsync(schedule);

        await _store.DeleteAsync(schedule.Id);

        var stored = await _store.GetAsync(schedule.Id);
        stored.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_NonExistent_DoesNotThrow()
    {
        await Should.NotThrowAsync(() => _store.DeleteAsync("non_existent_id"));
    }

    [Fact]
    public async Task GetDueSchedulesAsync_ReturnsDueOnly()
    {
        var dueSchedule = CreateTestSchedule() with { NextRunAt = DateTime.UtcNow.AddMinutes(-5) };
        var futureSchedule = CreateTestSchedule() with { NextRunAt = DateTime.UtcNow.AddHours(1) };
        _createdIds.Add(dueSchedule.Id);
        _createdIds.Add(futureSchedule.Id);

        await _store.CreateAsync(dueSchedule);
        await _store.CreateAsync(futureSchedule);

        var due = await _store.GetDueSchedulesAsync(DateTime.UtcNow);

        due.ShouldContain(s => s.Id == dueSchedule.Id);
        due.ShouldNotContain(s => s.Id == futureSchedule.Id);
    }

    [Fact]
    public async Task GetDueSchedulesAsync_IncludesExactlyDueSchedules()
    {
        var now = DateTime.UtcNow;
        var exactlyDueSchedule = CreateTestSchedule() with { NextRunAt = now };
        _createdIds.Add(exactlyDueSchedule.Id);

        await _store.CreateAsync(exactlyDueSchedule);

        var due = await _store.GetDueSchedulesAsync(now);

        due.ShouldContain(s => s.Id == exactlyDueSchedule.Id);
    }

    [Fact]
    public async Task UpdateLastRunAsync_UpdatesSchedule()
    {
        var schedule = CreateTestSchedule() with { NextRunAt = DateTime.UtcNow.AddMinutes(-5) };
        _createdIds.Add(schedule.Id);
        await _store.CreateAsync(schedule);

        var lastRun = DateTime.UtcNow;
        var newNextRun = DateTime.UtcNow.AddHours(1);
        await _store.UpdateLastRunAsync(schedule.Id, lastRun, newNextRun);

        var updated = await _store.GetAsync(schedule.Id);
        updated.ShouldNotBeNull();
        updated.LastRunAt.ShouldNotBeNull();
        updated.LastRunAt.Value.ShouldBe(lastRun, TimeSpan.FromSeconds(1));
        updated.NextRunAt.ShouldBe(newNextRun);
    }

    [Fact]
    public async Task UpdateLastRunAsync_WithNullNextRun_RemovesFromDueSet()
    {
        var schedule = CreateTestSchedule() with { NextRunAt = DateTime.UtcNow.AddMinutes(-5) };
        _createdIds.Add(schedule.Id);
        await _store.CreateAsync(schedule);

        await _store.UpdateLastRunAsync(schedule.Id, DateTime.UtcNow, null);

        var updated = await _store.GetAsync(schedule.Id);
        updated.ShouldNotBeNull();
        updated.NextRunAt.ShouldBeNull();

        var due = await _store.GetDueSchedulesAsync(DateTime.UtcNow.AddYears(1));
        due.ShouldNotContain(s => s.Id == schedule.Id);
    }

    [Fact]
    public async Task UpdateLastRunAsync_NonExistent_DoesNotThrow()
    {
        await Should.NotThrowAsync(() => _store.UpdateLastRunAsync("non_existent_id", DateTime.UtcNow, DateTime.UtcNow.AddHours(1)));
    }

    [Fact]
    public async Task CreateAsync_WithNoNextRun_DoesNotAddToDueSet()
    {
        var schedule = CreateTestSchedule() with { NextRunAt = null };
        _createdIds.Add(schedule.Id);

        await _store.CreateAsync(schedule);

        var due = await _store.GetDueSchedulesAsync(DateTime.UtcNow.AddYears(1));
        due.ShouldNotContain(s => s.Id == schedule.Id);
    }

    [Fact]
    public async Task CreateAsync_OneShot_SetsExpiry()
    {
        var runAt = DateTime.UtcNow.AddMinutes(5);
        var schedule = CreateTestSchedule() with
        {
            CronExpression = null,
            RunAt = runAt,
            NextRunAt = runAt
        };
        _createdIds.Add(schedule.Id);

        await _store.CreateAsync(schedule);

        var stored = await _store.GetAsync(schedule.Id);
        stored.ShouldNotBeNull();
        stored.RunAt.ShouldBe(runAt);
    }

    // Who a schedule runs as is read back on every fire, so the account its creator named must
    // survive the store.
    [Fact]
    public async Task CreateAsync_KeepsTheCreatorsAccount()
    {
        var creator = new Principal("signalr", "u-1", new Account("t-1", "u-1"));
        var schedule = CreateTestSchedule() with { CreatedBy = creator };
        _createdIds.Add(schedule.Id);

        await _store.CreateAsync(schedule);

        (await _store.GetAsync(schedule.Id))!.CreatedBy.ShouldBe(creator);
    }

    // A schedule written before the principal carried an account is still the channel's user.
    [Fact]
    public async Task GetAsync_ACreatorRecordedWithoutAnAccount_ReadsAsTheChannelsUser()
    {
        var id = $"test_{Guid.NewGuid():N}";
        _createdIds.Add(id);
        await fixture.Connection.GetDatabase().StringSetAsync($"schedule:{id}",
            $$"""
            {"Id":"{{id}}","AgentId":"jonas","Prompt":"p","CronExpression":"0 9 * * *",
             "CreatedBy":{"ChannelId":"telegram","UserId":"FranTG"},"CreatedAt":"2026-10-01T00:00:00Z"}
            """);

        (await _store.GetAsync(id))!.CreatedBy.ShouldBe(new Principal("telegram", "FranTG"));
    }

    private static Schedule CreateTestSchedule()
    {
        return new Schedule
        {
            Id = $"test_{Guid.NewGuid():N}",
            AgentId = "test",
            Prompt = "Test prompt",
            CronExpression = "0 9 * * *",
            UserId = "testuser",
            CreatedAt = DateTime.UtcNow,
            NextRunAt = DateTime.UtcNow.AddHours(1)
        };
    }
}