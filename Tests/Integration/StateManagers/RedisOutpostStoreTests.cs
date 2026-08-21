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

    // The expiry is read off the key rather than inferred from an entry outliving a sibling. That
    // older shape wrote both entries with the two-second expiry and then refreshed one, which made
    // the short expiry a deadline the test itself had to beat: the round trip and a thread shared
    // with the rest of the suite were spent against it, and a stall longer than it left RefreshAsync
    // nothing to find, reporting an empty Live as a behaviour failure. Asking Redis what the TTL is
    // now says the same thing more directly — and more strictly, since an entry that outlived its
    // sibling proves only that its expiry is longer, not that it is the one the refresh asked for.
    [Fact]
    public async Task RefreshingAnEntry_PushesItsExpiryOut()
    {
        var name = Unique();
        await _store.SetAsync(Registration(name), _shortExpiry);

        (await _store.RefreshAsync(name, _longExpiry)).ShouldNotBeNull().Name.ShouldBe(name);

        // A lower bound only. Redis reports the remaining TTL, which is the expiry asked for minus
        // however long the round trip took and rounded to its own resolution, so pinning the top of
        // the range asserts the clock rather than the code — it was briefly pinned at exactly the
        // long expiry, and a report a shade above it failed the test for no defect at all. What the
        // refresh has to have done is move the TTL far past the short one it was written with, and
        // a bound most of the way to the long expiry cannot be met by anything else here.
        var ttl = await fixture.Connection.GetDatabase().KeyTimeToLiveAsync($"outpost:{name}");
        ttl.ShouldNotBeNull();
        ttl.Value.ShouldBeGreaterThan(_longExpiry - TimeSpan.FromSeconds(30));

        await _store.RemoveAsync(name);
    }

    // The other half of the same rule, kept as its own test: an entry nobody refreshes really does
    // go. It waits for that state rather than for a span, so it costs the expiry and no more.
    [Fact]
    public async Task AnEntryRefreshedThenLeft_StillLapsesOnTheExpiryItWasGiven()
    {
        var name = Unique();
        await _store.SetAsync(Registration(name), _longExpiry);

        (await _store.RefreshAsync(name, _shortExpiry)).ShouldNotBeNull().Name.ShouldBe(name);

        await Eventually.Until(
            async ValueTask<bool> () => !(await _store.ReadAsync()).Live.Any(r => r.Name == name),
            "the entry refreshed onto a short expiry lapses");
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