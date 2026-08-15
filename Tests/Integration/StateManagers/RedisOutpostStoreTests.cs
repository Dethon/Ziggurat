using Domain.DTOs;
using Infrastructure.StateManagers;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.StateManagers;

// The expiry is the entire mechanism — nothing on the hub notices a machine dying, the key just
// stops being there — so a wrong TTL argument would otherwise ship green against a fake store that
// models expiry the way the code hoped Redis did.
public sealed class RedisOutpostStoreTests(RedisFixture fixture) : IClassFixture<RedisFixture>
{
    // Short enough to wait out, long enough that a slow round trip does not expire the entry
    // before the assertion that it is still there.
    private static readonly TimeSpan _shortExpiry = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan _longExpiry = TimeSpan.FromMinutes(5);

    private readonly RedisOutpostStore _store = new(fixture.Connection);

    [Fact]
    public async Task AnEntryNobodyRefreshes_ReallyDisappears()
    {
        var name = Unique();
        await _store.SetAsync(Registration(name), _shortExpiry);

        (await _store.ReadAsync()).Live.Select(r => r.Name).ShouldContain(name);

        await Eventually.Until(
            async ValueTask<bool> () => !(await _store.ReadAsync()).Live.Any(r => r.Name == name),
            "the outpost entry expires");
    }

    // The index outlives the entry, so the lapse has something to be noticed against — and it is
    // reported exactly once, because reporting it is also what forgets it.
    [Fact]
    public async Task ALapse_IsReportedOnceAndThenForgotten()
    {
        var name = Unique();
        await _store.SetAsync(Registration(name), _shortExpiry);

        await Eventually.Until(
            async ValueTask<bool> () => (await _store.ReadAsync()).Lapsed.Contains(name),
            "the lapse is noticed");

        (await _store.ReadAsync()).Lapsed.ShouldNotContain(name);
    }

    // The refreshed entry outlives an entry written at the same moment with the same short expiry,
    // which is the state this waits for rather than a span it sleeps through.
    [Fact]
    public async Task RefreshingAnEntry_PushesItsExpiryOut()
    {
        var refreshed = Unique();
        var control = Unique();
        await _store.SetAsync(Registration(refreshed), _shortExpiry);
        await _store.SetAsync(Registration(control), _shortExpiry);

        (await _store.RefreshAsync(refreshed, _longExpiry)).ShouldNotBeNull().Name.ShouldBe(refreshed);

        await Eventually.Until(
            async ValueTask<bool> () => !(await _store.ReadAsync()).Live.Any(r => r.Name == control),
            "the un-refreshed entry expires");
        (await _store.ReadAsync()).Live.Select(r => r.Name).ShouldContain(refreshed);

        await _store.RemoveAsync(refreshed);
    }

    [Fact]
    public async Task RefreshingAnEntryThatHasGone_SaysSo()
    {
        (await _store.RefreshAsync(Unique(), _longExpiry)).ShouldBeNull();
    }

    [Fact]
    public async Task RemovingAnEntry_TakesItAndItsIndexEntryAtOnce()
    {
        var name = Unique();
        await _store.SetAsync(Registration(name), _longExpiry);

        (await _store.RemoveAsync(name)).ShouldBeTrue();

        var snapshot = await _store.ReadAsync();
        snapshot.Live.Select(r => r.Name).ShouldNotContain(name);
        snapshot.Lapsed.ShouldNotContain(name);
    }

    // The verdict rides the keepalive home, so it has to survive a refresh — and recording one
    // must not extend the lifetime the machine is managing on its own schedule.
    [Fact]
    public async Task ARecordedVerdict_ComesBackWithTheNextRefreshAndDoesNotExtendTheEntry()
    {
        var name = Unique();
        var control = Unique();
        await _store.SetAsync(Registration(name), _shortExpiry);
        await _store.SetAsync(Registration(control), _shortExpiry);

        await _store.RecordVerdictAsync(name, OutpostVerdict.Shadowed);

        (await _store.ReadAsync()).Live.Single(r => r.Name == name).Verdict
            .ShouldBe(OutpostVerdict.Shadowed);

        // Written with keepTtl, so this entry lapses alongside the one nobody touched.
        await Eventually.Until(
            async ValueTask<bool> () => !(await _store.ReadAsync()).Live.Any(r => r.Name == control),
            "the untouched entry expires");
        (await _store.ReadAsync()).Live.ShouldNotContain(r => r.Name == name);
    }

    // A machine that went away between the session build and the write is not resurrected: the
    // verdict is about a mount nobody holds any more.
    [Fact]
    public async Task RecordingAVerdictForARegistrationThatHasGone_WritesNothing()
    {
        var name = Unique();

        await _store.RecordVerdictAsync(name, OutpostVerdict.Mounted);

        (await _store.ReadAsync()).Live.ShouldNotContain(r => r.Name == name);
    }

    // The database is shared with whatever else this run is doing, so each test names its own
    // outpost rather than asserting on the whole listing.
    private static string Unique() => $"outpost-{Guid.NewGuid():N}";

    private static OutpostRegistration Registration(string name) =>
        new() { Name = name, Endpoint = $"http://192.168.1.20:8099/mcp#{name}" };
}