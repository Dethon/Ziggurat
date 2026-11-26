using Domain.Apps;
using Domain.DTOs.Channel;
using McpServerApps.Asks;
using McpServerApps.Settings;
using McpServerApps.Store;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpServerApps;

// An app's asks are counted on its own directory of the volume, one ask at a time, so the last slot is
// taken once however many ask together, and an app deleted takes its counts with it.
public sealed class AskCountsTests : IDisposable
{
    private static readonly DateTimeOffset _start = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(_start);
    private readonly BuiltAppStore _store;
    private readonly AskCounts _counts;
    private readonly string _storage = Path.Combine(Path.GetTempPath(), "ask-counts-tests", Guid.NewGuid().ToString("N"));

    public AskCountsTests()
    {
        _store = new BuiltAppStore(new AppsSettings { AppsDomain = "apps.test", StoragePath = _storage }, _clock);
        _counts = new AskCounts(_store, _clock);
    }

    public void Dispose()
    {
        if (Directory.Exists(_storage))
        {
            Directory.Delete(_storage, recursive: true);
        }
    }

    [Fact]
    public async Task AsksTogether_TakeTheLastSlotsOnce()
    {
        var app = await AppAsync();

        var counted = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => _counts.TryCountAsync(app, 5, CancellationToken.None))));

        counted.OfType<AskCount.Counted>().Count().ShouldBe(5);
        counted.OfType<AskCount.Capped>().Count().ShouldBe(15);
    }

    // An ask that was never delivered gives its slot back, from the minute it took, whenever that is.
    [Fact]
    public async Task AnAskReleased_GivesItsSlotBack()
    {
        var app = await AppAsync();
        var counted = (await _counts.TryCountAsync(app, 1, CancellationToken.None)).ShouldBeOfType<AskCount.Counted>();
        _clock.Advance(TimeSpan.FromMinutes(5));

        await _counts.ReleaseAsync(app, counted);

        (await _counts.TryCountAsync(app, 1, CancellationToken.None)).ShouldBeOfType<AskCount.Counted>();
    }

    // When an ask is counted again is when enough minutes have rolled out of the day to come under the limit:
    // after a lowered limit, more than the oldest.
    [Fact]
    public async Task ACappedAsk_SaysWhenEnoughHaveStoppedCounting()
    {
        var app = await AppAsync();
        await CountAsync(app, 3, 10);
        _clock.Advance(TimeSpan.FromHours(1));
        await CountAsync(app, 2, 10);
        _clock.Advance(TimeSpan.FromHours(1));

        var capped = (await _counts.TryCountAsync(app, 2, CancellationToken.None)).ShouldBeOfType<AskCount.Capped>();

        capped.Limit.ShouldBe(2);
        capped.FreesAt.ShouldBe(_start + TimeSpan.FromHours(1) + TimeSpan.FromDays(1));
    }

    // A cap of none frees nothing however long the app waits.
    [Fact]
    public async Task ACapOfNone_FreesNever()
    {
        var app = await AppAsync();

        var capped = (await _counts.TryCountAsync(app, 0, CancellationToken.None)).ShouldBeOfType<AskCount.Capped>();

        capped.FreesAt.ShouldBeNull();
    }

    // The counts are the server's: beside the repository, never spending the app's quota.
    [Fact]
    public async Task TheCounts_SpendNothingOfTheAppsQuota()
    {
        var app = await AppAsync();
        var before = _store.SizeOf(app);

        await _counts.TryCountAsync(app, 5, CancellationToken.None);

        File.Exists(_store.AskCountsFile(app)).ShouldBeTrue();
        _store.SizeOf(app).ShouldBe(before);
    }

    [Fact]
    public async Task ADeletedApp_TakesItsCountsWithIt_AndIsNeverMadeAgainByAnAsk()
    {
        var app = await AppAsync();
        var counted = (await _counts.TryCountAsync(app, 5, CancellationToken.None)).ShouldBeOfType<AskCount.Counted>();

        await _store.DeleteAsync(app);

        (await _counts.TryCountAsync(app, 5, CancellationToken.None)).ShouldBeOfType<AskCount.Gone>();
        await _counts.ReleaseAsync(app, counted);
        Directory.Exists(Path.GetDirectoryName(_store.AskCountsFile(app))).ShouldBeFalse();
    }

    private async Task CountAsync(BuiltApp app, int asks, int limit)
    {
        foreach (var _ in Enumerable.Range(0, asks))
        {
            (await _counts.TryCountAsync(app, limit, CancellationToken.None)).ShouldBeOfType<AskCount.Counted>();
        }
    }

    // The metadata alone: counting reads nothing of the repository.
    private async Task<BuiltApp> AppAsync()
    {
        var app = new BuiltApp
        {
            Id = BuiltAppId.Mint(),
            Slug = "glucose",
            HouseholdId = "household-home",
            Author = new Account("household-home", "user-her"),
            BuildingAgent = "jonas",
            CreatedAt = _start
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_store.AskCountsFile(app))!);
        await _store.SaveAsync(app, CancellationToken.None);
        return app;
    }
}