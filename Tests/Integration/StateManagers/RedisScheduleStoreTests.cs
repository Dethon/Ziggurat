using Domain.DTOs;
using Domain.DTOs.Channel;
using Infrastructure.StateManagers;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.StateManagers;

public class RedisScheduleStoreTests(RedisFixture fixture) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private readonly RedisScheduleStore _store = new(fixture.Connection);
    private readonly List<(string Household, string Id)> _createdIds = [];

    // Each test's own households, so a list read here holds only what this test wrote.
    private readonly string _home = $"home-{Guid.NewGuid():N}";
    private readonly string _away = $"away-{Guid.NewGuid():N}";

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (household, id) in _createdIds)
        {
            await _store.DeleteAsync(household, id);
        }
    }

    // A one-shot is kept until an hour after it is due, counted on the store's own clock: a stack
    // whose clock is pinned (the eval's, months behind the wall) writes one-shots that are due on its
    // day, and a horizon read off the wall clock had them expire before anything could read them.
    [Fact]
    public async Task CreateAsync_AOneShot_ExpiresAnHourAfterItIsDue_OnTheStoresClock()
    {
        var pinned = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            new DateTimeOffset(2026, 8, 17, 18, 0, 0, TimeSpan.Zero));
        var store = new RedisScheduleStore(fixture.Connection, pinned);
        var oneShot = CreateTestSchedule() with
        {
            CronExpression = null,
            RunAt = pinned.GetUtcNow().UtcDateTime.AddHours(1),
            NextRunAt = null
        };
        _createdIds.Add((_home, oneShot.Id));

        await store.CreateAsync(oneShot);

        var ttl = await fixture.Connection.GetDatabase().KeyTimeToLiveAsync(RedisScheduleStore.ScheduleKey(_home, oneShot.Id));
        ttl.ShouldNotBeNull();
        ttl.Value.ShouldBeInRange(TimeSpan.FromMinutes(119), TimeSpan.FromMinutes(120));
    }

    // Every household keeps its own: a list, a read and a delete name the household, so one never
    // answers with another's schedule, even under the same id.
    [Fact]
    public async Task ListAsync_ListsOnlyItsOwnHouseholdsSchedules()
    {
        var ours = CreateTestSchedule();
        var theirs = CreateTestSchedule() with { TenantId = _away };
        _createdIds.Add((_home, ours.Id));
        _createdIds.Add((_away, theirs.Id));

        await _store.CreateAsync(ours);
        await _store.CreateAsync(theirs);

        (await _store.ListAsync(_home)).Select(s => s.Id).ShouldBe([ours.Id]);
        (await _store.ListAsync(_away)).Select(s => s.Id).ShouldBe([theirs.Id]);
    }

    [Fact]
    public async Task GetAsync_AnotherHouseholdsSchedule_ReturnsNull()
    {
        var theirs = CreateTestSchedule() with { TenantId = _away };
        _createdIds.Add((_away, theirs.Id));
        await _store.CreateAsync(theirs);

        (await _store.GetAsync(_home, theirs.Id)).ShouldBeNull();
    }

    [Fact]
    public async Task CreateAsync_TheSameIdInTwoHouseholds_KeepsBoth()
    {
        var ours = CreateTestSchedule() with { Prompt = "ours" };
        var theirs = ours with { TenantId = _away, Prompt = "theirs" };
        _createdIds.Add((_home, ours.Id));
        _createdIds.Add((_away, ours.Id));

        await _store.CreateAsync(ours);
        await _store.CreateAsync(theirs);
        await _store.DeleteAsync(_away, ours.Id);

        (await _store.GetAsync(_home, ours.Id))!.Prompt.ShouldBe("ours");
        (await _store.GetAsync(_away, ours.Id)).ShouldBeNull();
    }

    // The dispatcher is the deployment's, so what is due is every household's, each schedule saying
    // whose it is.
    [Fact]
    public async Task GetDueSchedulesAsync_ReturnsEveryHouseholdsDueSchedules()
    {
        var ours = CreateTestSchedule() with { NextRunAt = DateTime.UtcNow.AddMinutes(-5) };
        var theirs = ours with { TenantId = _away };
        _createdIds.Add((_home, ours.Id));
        _createdIds.Add((_away, ours.Id));
        await _store.CreateAsync(ours);
        await _store.CreateAsync(theirs);

        await _store.UpdateLastRunAsync(_away, ours.Id, DateTime.UtcNow, null);

        var due = (await _store.GetDueSchedulesAsync(DateTime.UtcNow)).Where(s => s.Id == ours.Id).ToList();
        due.Select(s => s.TenantId).ShouldBe([_home]);
        (await _store.GetAsync(_away, ours.Id))!.NextRunAt.ShouldBeNull();
    }

    [Fact]
    public async Task ListEveryHouseholdAsync_ListsEachHouseholdsSchedules()
    {
        var ours = CreateTestSchedule();
        var theirs = CreateTestSchedule() with { TenantId = _away };
        _createdIds.Add((_home, ours.Id));
        _createdIds.Add((_away, theirs.Id));
        await _store.CreateAsync(ours);
        await _store.CreateAsync(theirs);

        (await _store.ListEveryHouseholdAsync())
            .Where(s => s.TenantId == _home || s.TenantId == _away)
            .Select(s => (s.TenantId, s.Id))
            .ShouldBe([(_home, ours.Id), (_away, theirs.Id)], ignoreOrder: true);
    }

    [Fact]
    public async Task CreateAsync_StoresSchedule()
    {
        var schedule = CreateTestSchedule();
        _createdIds.Add((_home, schedule.Id));

        var result = await _store.CreateAsync(schedule);

        result.Id.ShouldBe(schedule.Id);
        var stored = await _store.GetAsync(_home, schedule.Id);
        stored.ShouldNotBeNull();
        stored.Prompt.ShouldBe(schedule.Prompt);
    }

    [Fact]
    public async Task GetAsync_NonExistent_ReturnsNull()
    {
        var result = await _store.GetAsync(_home, "non_existent_id");

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ListAsync_ReturnsAllSchedules()
    {
        var schedule1 = CreateTestSchedule();
        var schedule2 = CreateTestSchedule();
        _createdIds.Add((_home, schedule1.Id));
        _createdIds.Add((_home, schedule2.Id));

        await _store.CreateAsync(schedule1);
        await _store.CreateAsync(schedule2);

        var list = await _store.ListAsync(_home);

        list.ShouldContain(s => s.Id == schedule1.Id);
        list.ShouldContain(s => s.Id == schedule2.Id);
    }

    [Fact]
    public async Task ListAsync_ReturnsOrderedByNextRunAt()
    {
        var laterSchedule = CreateTestSchedule() with { NextRunAt = DateTime.UtcNow.AddHours(2) };
        var earlierSchedule = CreateTestSchedule() with { NextRunAt = DateTime.UtcNow.AddHours(1) };
        _createdIds.Add((_home, laterSchedule.Id));
        _createdIds.Add((_home, earlierSchedule.Id));

        await _store.CreateAsync(laterSchedule);
        await _store.CreateAsync(earlierSchedule);

        var list = await _store.ListAsync(_home);

        var laterIndex = list.ToList().FindIndex(s => s.Id == laterSchedule.Id);
        var earlierIndex = list.ToList().FindIndex(s => s.Id == earlierSchedule.Id);
        earlierIndex.ShouldBeLessThan(laterIndex);
    }

    [Fact]
    public async Task DeleteAsync_RemovesSchedule()
    {
        var schedule = CreateTestSchedule();
        await _store.CreateAsync(schedule);

        await _store.DeleteAsync(_home, schedule.Id);

        var stored = await _store.GetAsync(_home, schedule.Id);
        stored.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_NonExistent_DoesNotThrow()
    {
        await Should.NotThrowAsync(() => _store.DeleteAsync(_home, "non_existent_id"));
    }

    [Fact]
    public async Task GetDueSchedulesAsync_ReturnsDueOnly()
    {
        var dueSchedule = CreateTestSchedule() with { NextRunAt = DateTime.UtcNow.AddMinutes(-5) };
        var futureSchedule = CreateTestSchedule() with { NextRunAt = DateTime.UtcNow.AddHours(1) };
        _createdIds.Add((_home, dueSchedule.Id));
        _createdIds.Add((_home, futureSchedule.Id));

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
        _createdIds.Add((_home, exactlyDueSchedule.Id));

        await _store.CreateAsync(exactlyDueSchedule);

        var due = await _store.GetDueSchedulesAsync(now);

        due.ShouldContain(s => s.Id == exactlyDueSchedule.Id);
    }

    [Fact]
    public async Task UpdateLastRunAsync_UpdatesSchedule()
    {
        var schedule = CreateTestSchedule() with { NextRunAt = DateTime.UtcNow.AddMinutes(-5) };
        _createdIds.Add((_home, schedule.Id));
        await _store.CreateAsync(schedule);

        var lastRun = DateTime.UtcNow;
        var newNextRun = DateTime.UtcNow.AddHours(1);
        await _store.UpdateLastRunAsync(_home, schedule.Id, lastRun, newNextRun);

        var updated = await _store.GetAsync(_home, schedule.Id);
        updated.ShouldNotBeNull();
        updated.LastRunAt.ShouldNotBeNull();
        updated.LastRunAt.Value.ShouldBe(lastRun, TimeSpan.FromSeconds(1));
        updated.NextRunAt.ShouldBe(newNextRun);
    }

    [Fact]
    public async Task UpdateLastRunAsync_WithNullNextRun_RemovesFromDueSet()
    {
        var schedule = CreateTestSchedule() with { NextRunAt = DateTime.UtcNow.AddMinutes(-5) };
        _createdIds.Add((_home, schedule.Id));
        await _store.CreateAsync(schedule);

        await _store.UpdateLastRunAsync(_home, schedule.Id, DateTime.UtcNow, null);

        var updated = await _store.GetAsync(_home, schedule.Id);
        updated.ShouldNotBeNull();
        updated.NextRunAt.ShouldBeNull();

        var due = await _store.GetDueSchedulesAsync(DateTime.UtcNow.AddYears(1));
        due.ShouldNotContain(s => s.Id == schedule.Id);
    }

    [Fact]
    public async Task UpdateLastRunAsync_NonExistent_DoesNotThrow()
    {
        await Should.NotThrowAsync(() => _store.UpdateLastRunAsync(_home, "non_existent_id", DateTime.UtcNow, DateTime.UtcNow.AddHours(1)));
    }

    [Fact]
    public async Task CreateAsync_WithNoNextRun_DoesNotAddToDueSet()
    {
        var schedule = CreateTestSchedule() with { NextRunAt = null };
        _createdIds.Add((_home, schedule.Id));

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
        _createdIds.Add((_home, schedule.Id));

        await _store.CreateAsync(schedule);

        var stored = await _store.GetAsync(_home, schedule.Id);
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
        _createdIds.Add((_home, schedule.Id));

        await _store.CreateAsync(schedule);

        (await _store.GetAsync(_home, schedule.Id))!.CreatedBy.ShouldBe(creator);
    }

    // A schedule written before the principal carried an account is still the channel's user.
    [Fact]
    public async Task GetAsync_ACreatorRecordedWithoutAnAccount_ReadsAsTheChannelsUser()
    {
        var id = $"test_{Guid.NewGuid():N}";
        _createdIds.Add((_home, id));
        await fixture.Connection.GetDatabase().StringSetAsync($"schedule@{_home}:{id}",
            $$"""
            {"Id":"{{id}}","AgentId":"jonas","Prompt":"p","CronExpression":"0 9 * * *",
             "CreatedBy":{"ChannelId":"telegram","UserId":"FranTG"},"CreatedAt":"2026-10-01T00:00:00Z"}
            """);

        (await _store.GetAsync(_home, id))!.CreatedBy.ShouldBe(new Principal("telegram", "FranTG"));
    }

    private Schedule CreateTestSchedule()
    {
        return new Schedule
        {
            Id = $"test_{Guid.NewGuid():N}",
            TenantId = _home,
            AgentId = "test",
            Prompt = "Test prompt",
            CronExpression = "0 9 * * *",
            UserId = "testuser",
            CreatedAt = DateTime.UtcNow,
            NextRunAt = DateTime.UtcNow.AddHours(1)
        };
    }
}