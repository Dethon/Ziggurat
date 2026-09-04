using Domain.Contracts;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant.Vfs;

public class HaCatalogProviderTests
{
    [Fact]
    public async Task GetAsync_BuildsCatalogFromClient()
    {
        var client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off") },
            Services = { Service("light", "turn_on", AnyEntityTarget()) },
            AreaTemplateJson = """{"areas":[{"id":"salon","name":"Salón","entities":["light.kitchen"]}]}"""
        };
        var provider = new HaCatalogProvider(() => client, new FakeTimeProvider());

        var catalog = await provider.GetAsync(CancellationToken.None);

        catalog.Entities.Count.ShouldBe(1);
        catalog.Services.Count.ShouldBe(1);
        catalog.Areas.ShouldContain(a => a.Id == "salon" && a.EntityIds.Contains("light.kitchen"));
    }

    // A served action with the same name as one Home Assistant publishes replaces it rather than
    // sitting beside it: the calendar's create_event is served here (Home Assistant's own cannot
    // take a recurrence rule), and two definitions of one action file would resolve to whichever
    // came first.
    [Fact]
    public async Task GetAsync_ExtraServiceWithTheSameName_ReplacesTheHomeAssistantOne()
    {
        var client = new FakeHaClient
        {
            Services =
            {
                Service("calendar", "create_event", DomainTarget("calendar"), ("summary", new HaServiceField())),
                Service("calendar", "get_events", DomainTarget("calendar"))
            }
        };
        var served = Service("calendar", "create_event", DomainTarget("calendar"),
            ("summary", new HaServiceField()), ("rrule", new HaServiceField()));
        var provider = new HaCatalogProvider(() => client, new FakeTimeProvider(), extraServices: [served]);

        var catalog = await provider.GetAsync(CancellationToken.None);

        catalog.Services.Count.ShouldBe(2);
        catalog.Services.Single(s => s.Service == "create_event").Fields.Keys.ShouldContain("rrule");
    }

    // The recorder stamps history in UTC, so the home's own clock has to come from its
    // configuration; the catalog carries it, resolved to a zone this runtime knows.
    [Fact]
    public async Task GetAsync_CarriesTheHomesTimeZone()
    {
        var client = new FakeHaClient { TimeZone = "Europe/Madrid" };
        var provider = new HaCatalogProvider(() => client, new FakeTimeProvider());

        var catalog = await provider.GetAsync(CancellationToken.None);

        catalog.HomeZone.ShouldNotBeNull().Id.ShouldBe("Europe/Madrid");
    }

    // A zone this runtime cannot resolve, or a config read that fails, leaves the zone unknown
    // rather than blanking the catalog: the mount stays usable and the summary buckets on UTC. It
    // is said once in the log, because otherwise the only trace is a `bucket_zone: UTC` in a payload.
    [Theory]
    [InlineData("Nowhere/Nowhere", false)]
    [InlineData(null, false)]
    [InlineData("Europe/Madrid", true)]
    public async Task GetAsync_AnUnknownOrUnreadableZone_LeavesItNull_KeepsTheCatalog_AndWarns(string? zone, bool fails)
    {
        var client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off") },
            TimeZone = zone,
            TimeZoneFailure = fails ? new HttpRequestException("config unreachable") : null
        };
        var log = new CapturingLoggerProvider(LogLevel.Warning);
        var provider = new HaCatalogProvider(
            () => client, new FakeTimeProvider(), logger: new Logger<HaCatalogProvider>(new LoggerFactory([log])));

        var catalog = await provider.GetAsync(CancellationToken.None);

        catalog.Entities.Count.ShouldBe(1);
        catalog.HomeZone.ShouldBeNull();
        log.Messages.ShouldHaveSingleItem().ShouldContain("UTC");
    }

    [Fact]
    public async Task GetAsync_AResolvedZone_WarnsOfNothing()
    {
        var client = new FakeHaClient { States = { Entity("light.kitchen", "off") }, TimeZone = "Europe/Madrid" };
        var log = new CapturingLoggerProvider(LogLevel.Warning);
        var provider = new HaCatalogProvider(
            () => client, new FakeTimeProvider(), logger: new Logger<HaCatalogProvider>(new LoggerFactory([log])));

        await provider.GetAsync(CancellationToken.None);

        log.Messages.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAsync_SuccessfulButEmpty_CachesForFullTtl()
    {
        var client = new CountingClient(); // no states, but the call succeeds (not a failure)
        var time = new FakeTimeProvider();
        var provider = new HaCatalogProvider(() => client, time);

        await provider.GetAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(60)); // past the 30s failure TTL
        await provider.GetAsync(CancellationToken.None);

        client.StateCalls.ShouldBe(1);
    }

    [Fact]
    public async Task GetAsync_AfterFailure_RepollsOnceFailureTtlElapses()
    {
        var client = new FlakyClient { States = { Entity("light.kitchen", "off") } };
        var time = new FakeTimeProvider();
        var provider = new HaCatalogProvider(() => client, time);

        (await provider.GetAsync(CancellationToken.None)).Entities.ShouldBeEmpty();
        client.StateCalls.ShouldBe(1);

        time.Advance(TimeSpan.FromSeconds(15)); // within the failure TTL — still cached
        await provider.GetAsync(CancellationToken.None);
        client.StateCalls.ShouldBe(1);

        time.Advance(TimeSpan.FromSeconds(30)); // past the failure TTL — re-polls, now recovered
        client.Throw = false;
        (await provider.GetAsync(CancellationToken.None)).Entities.Count.ShouldBe(1);
        client.StateCalls.ShouldBe(2);
    }

    [Fact]
    public async Task GetAsync_Cancelled_PropagatesAndDoesNotPoisonCache()
    {
        var client = new CancellingClient { States = { Entity("light.kitchen", "off") } };
        var provider = new HaCatalogProvider(() => client, new FakeTimeProvider());

        // Cancellation must propagate, not be swallowed into an empty catalog cached for the failure TTL.
        await Should.ThrowAsync<OperationCanceledException>(() => provider.GetAsync(CancellationToken.None));

        // Cache wasn't poisoned: the next call rebuilds and yields the real catalog (no blind window).
        (await provider.GetAsync(CancellationToken.None)).Entities.Count.ShouldBe(1);
        client.StateCalls.ShouldBe(2);
    }

    private sealed class CountingClient : FakeHaClient
    {
        public int StateCalls { get; private set; }
        public override Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
        {
            StateCalls++;
            return base.ListStatesAsync(ct);
        }
    }

    private sealed class FlakyClient : FakeHaClient
    {
        public int StateCalls { get; private set; }
        public bool Throw { get; set; } = true;

        public override Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
        {
            StateCalls++;
            return Throw ? throw new InvalidOperationException("HA down") : base.ListStatesAsync(ct);
        }
    }

    private sealed class CancellingClient : FakeHaClient
    {
        public int StateCalls { get; private set; }
        private bool _cancel = true;

        public override Task<IReadOnlyList<HaEntityState>> ListStatesAsync(CancellationToken ct = default)
        {
            StateCalls++;
            if (!_cancel)
            {
                return base.ListStatesAsync(ct);
            }
            _cancel = false;
            throw new OperationCanceledException();
        }
    }
}