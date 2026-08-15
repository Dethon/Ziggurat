using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics.Enums;
using Domain.Outposts;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Outposts;

// The whole lifecycle of an outpost registration, driven over a store and a clock the test owns.
// Nothing here needs a machine: an outpost is a name and an address with a lifetime, and the hub's
// side of it is that lifetime and nothing else.
public class OutpostRegistryTests
{
    private static readonly OutpostRegistration _laptop = new()
    {
        Name = "laptop",
        Endpoint = "http://192.168.1.20:8099/mcp"
    };

    [Fact]
    public async Task ARegistrationThatLanded_IsLive()
    {
        var (registry, _, _) = Registry();

        await registry.RegisterAsync(_laptop);

        (await registry.ListAsync()).ShouldBe([_laptop]);
    }

    // The whole expiry mechanism: an outpost that stops asking stops existing, and nothing had to
    // notice that the machine died.
    [Fact]
    public async Task AnOutpostThatStopsAsking_IsGoneOnceItsExpiryHasPassed()
    {
        var (registry, _, clock) = Registry();
        await registry.RegisterAsync(_laptop);

        clock.Advance(OutpostLifetime.Expiry + TimeSpan.FromSeconds(1));

        (await registry.ListAsync()).ShouldBeEmpty();
    }

    // Three keepalive intervals fit inside one expiry, so a machine that keeps asking outlives any
    // number of them.
    [Fact]
    public async Task AKeepAlive_PushesTheExpiryOut()
    {
        var (registry, _, clock) = Registry();
        await registry.RegisterAsync(_laptop);

        clock.Advance(OutpostLifetime.KeepAliveInterval);
        (await registry.KeepAliveAsync(_laptop.Name)).ShouldNotBeNull();
        clock.Advance(OutpostLifetime.Expiry - TimeSpan.FromSeconds(1));

        (await registry.ListAsync()).ShouldBe([_laptop]);
    }

    // A machine that went quiet long enough to lapse and is only now asking again is not keeping
    // anything alive; it is told so, and announces itself afresh.
    [Fact]
    public async Task AKeepAliveForALapsedRegistration_IsRefused()
    {
        var (registry, _, clock) = Registry();
        await registry.RegisterAsync(_laptop);
        clock.Advance(OutpostLifetime.Expiry + TimeSpan.FromSeconds(1));

        (await registry.KeepAliveAsync(_laptop.Name)).ShouldBeNull();
    }

    // The name is the identity and the last write wins, so a machine that restarts — or moves to a
    // different address — re-registers over its own entry with no special handling anywhere.
    [Fact]
    public async Task RegisteringOverALiveEntryOfTheSameName_ReplacesIt()
    {
        var (registry, _, _) = Registry();
        await registry.RegisterAsync(_laptop);

        var moved = _laptop with { Endpoint = "http://10.0.0.5:8099/mcp" };
        await registry.RegisterAsync(moved);

        (await registry.ListAsync()).ShouldBe([moved]);
    }

    // A machine somebody switched off deliberately disappears at once, rather than lingering as a
    // mount the agent offers for another ninety seconds.
    [Fact]
    public async Task TakingARegistrationBack_RemovesItAtOnce()
    {
        var (registry, _, _) = Registry();
        await registry.RegisterAsync(_laptop);

        (await registry.DeregisterAsync(_laptop.Name)).ShouldBeTrue();

        (await registry.ListAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task TakingBackARegistrationThatIsNotThere_SaysSo()
    {
        var (registry, _, _) = Registry();

        (await registry.DeregisterAsync("never-registered")).ShouldBeFalse();
    }

    [Fact]
    public async Task LandingRefreshingAndTakingBack_EachPublishAMetric()
    {
        var (registry, metrics, _) = Registry();

        await registry.RegisterAsync(_laptop);
        await registry.KeepAliveAsync(_laptop.Name);
        await registry.DeregisterAsync(_laptop.Name);

        metrics.Outposts().Select(e => e.Lifecycle).ShouldBe(
        [
            OutpostLifecycle.Registered,
            OutpostLifecycle.Refreshed,
            OutpostLifecycle.Deregistered
        ]);
        metrics.Outposts().ShouldAllBe(e => e.Outpost == "laptop");
    }

    // Nothing watches for an expiry, so it is noticed the next time somebody asks what is out
    // there. That is the only moment the hub could ever learn it, and the answer is stamped with
    // when it was learned rather than pretending to know when the machine actually went.
    [Fact]
    public async Task AnExpiry_IsPublishedOnceWhenItIsNoticed()
    {
        var (registry, metrics, clock) = Registry();
        await registry.RegisterAsync(_laptop);
        clock.Advance(OutpostLifetime.Expiry + TimeSpan.FromSeconds(1));

        await registry.ListAsync();
        await registry.ListAsync();

        metrics.Outposts().Count(e => e.Lifecycle == OutpostLifecycle.Expired).ShouldBe(1);
        metrics.Outposts().Single(e => e.Lifecycle == OutpostLifecycle.Expired)
            .Timestamp.ShouldBe(clock.GetUtcNow());
    }

    // A registration taken back has not expired: the machine said so. Publishing both would make
    // an orderly shutdown indistinguishable from a machine that vanished.
    [Fact]
    public async Task ARegistrationTakenBack_NeverAlsoReportsAnExpiry()
    {
        var (registry, metrics, clock) = Registry();
        await registry.RegisterAsync(_laptop);
        await registry.DeregisterAsync(_laptop.Name);

        clock.Advance(OutpostLifetime.Expiry * 2);
        await registry.ListAsync();

        metrics.Outposts().ShouldNotContain(e => e.Lifecycle == OutpostLifecycle.Expired);
    }

    // The only channel back to a machine. A shadowed outpost registered perfectly and simply is
    // not there, and nothing at the machine can detect that — the collision is discovered on the
    // hub when a session is built, long after the registration succeeded.
    [Fact]
    public async Task AVerdictWrittenByASessionBuild_ComesBackWithTheNextKeepAlive()
    {
        var (registry, _, _) = Registry();
        await registry.RegisterAsync(_laptop);

        await registry.RecordVerdictAsync(_laptop.Name, OutpostVerdict.Shadowed);

        (await registry.KeepAliveAsync(_laptop.Name)).ShouldBe(OutpostVerdict.Shadowed);
    }

    // Not yet known is distinguishable from both, and is what every registration reads as until an
    // opted-in agent has built a session. It is not a problem and must not read as one.
    [Fact]
    public async Task BeforeAnySessionHasBeenBuilt_TheVerdictIsNotYetKnown()
    {
        var (registry, _, _) = Registry();
        await registry.RegisterAsync(_laptop);

        (await registry.KeepAliveAsync(_laptop.Name)).ShouldBe(OutpostVerdict.Unknown);
    }

    // A machine that restarts has not been through a session build yet, so a verdict from the
    // previous run would be a claim about a mount that no longer exists.
    [Fact]
    public async Task ReRegistering_ForgetsTheOldVerdict()
    {
        var (registry, _, _) = Registry();
        await registry.RegisterAsync(_laptop);
        await registry.RecordVerdictAsync(_laptop.Name, OutpostVerdict.Mounted);

        await registry.RegisterAsync(_laptop);

        (await registry.KeepAliveAsync(_laptop.Name)).ShouldBe(OutpostVerdict.Unknown);
    }

    private static (OutpostRegistry Registry, RecordingMetricsPublisher Metrics, FakeTimeProvider Clock) Registry()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-15T14:00:00Z"));
        var metrics = new RecordingMetricsPublisher();
        return (new OutpostRegistry(new InMemoryOutpostStore(clock), metrics, clock), metrics, clock);
    }

    // Redis's own expiry, modelled: an entry holds the moment it stops answering, and the index of
    // names outlives the entries so a lapse has something to be noticed against.
    private sealed class InMemoryOutpostStore(TimeProvider clock) : IOutpostStore
    {
        private readonly Dictionary<string, (OutpostRegistration Registration, DateTimeOffset ExpiresAt)> _entries =
            new(StringComparer.Ordinal);

        private readonly HashSet<string> _known = new(StringComparer.Ordinal);

        public Task SetAsync(OutpostRegistration registration, TimeSpan expiry, CancellationToken ct = default)
        {
            _entries[registration.Name] = (registration, clock.GetUtcNow() + expiry);
            _known.Add(registration.Name);
            return Task.CompletedTask;
        }

        public Task<OutpostRegistration?> RefreshAsync(string name, TimeSpan expiry, CancellationToken ct = default)
        {
            if (!Live(name, out var entry))
            {
                return Task.FromResult<OutpostRegistration?>(null);
            }

            _entries[name] = (entry.Registration, clock.GetUtcNow() + expiry);
            return Task.FromResult<OutpostRegistration?>(entry.Registration);
        }

        // Never touches the entry's expiry, exactly as the real store's keepTtl write does not.
        public Task RecordVerdictAsync(string name, OutpostVerdict verdict, CancellationToken ct = default)
        {
            if (Live(name, out var entry))
            {
                _entries[name] = (entry.Registration with { Verdict = verdict }, entry.ExpiresAt);
            }

            return Task.CompletedTask;
        }

        public Task<bool> RemoveAsync(string name, CancellationToken ct = default)
        {
            var wasLive = Live(name, out _);
            _entries.Remove(name);
            _known.Remove(name);
            return Task.FromResult(wasLive);
        }

        public Task<OutpostSnapshot> ReadAsync(CancellationToken ct = default)
        {
            var live = _known.Where(name => Live(name, out _)).ToList();
            var lapsed = _known.Except(live).ToList();
            lapsed.ForEach(name =>
            {
                _entries.Remove(name);
                _known.Remove(name);
            });

            return Task.FromResult(new OutpostSnapshot(
                [.. live.Select(name => _entries[name].Registration)], lapsed));
        }

        private bool Live(
            string name, out (OutpostRegistration Registration, DateTimeOffset ExpiresAt) entry) =>
            _entries.TryGetValue(name, out entry) && entry.ExpiresAt > clock.GetUtcNow();
    }
}