using System.Text.Json.Nodes;
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

    // An entity hidden in Home Assistant's registry is off its own dashboards, and the states
    // endpoint still lists it. The wake button the TV's turn_on automation presses was one: listed
    // under the TV's room, the model pressed it by hand and then went looking for what else it
    // needed. Hidden means hidden from the agent too — it never enters the catalog, so no tree,
    // no index line and no path resolves to it.
    [Fact]
    public async Task GetAsync_AnEntityTheRegistryHides_IsLeftOutOfTheCatalog()
    {
        var client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off"), Entity("button.tv_wake", "unknown") },
            AreaTemplateJson =
                """{"areas":[{"id":"salon","name":"Salón","entities":["light.kitchen","button.tv_wake"]}],"hidden":["button.tv_wake"]}"""
        };
        var provider = new HaCatalogProvider(() => client, new FakeTimeProvider());

        var catalog = await provider.GetAsync(CancellationToken.None);

        catalog.Entities.Select(e => e.EntityId).ShouldBe(["light.kitchen"]);
        catalog.EntityIdsInArea("salon").ShouldBe(["light.kitchen"]);
    }

    // What holds for the TV holds for anything that chooses: a receiver's `source_list` and a
    // select's `options` leave the states endpoint with the entity going unavailable. Home
    // Assistant's entity registry persists both as the entity's capabilities, so an unavailable
    // entity serving no choice list gets the registry's — and only the choice lists, not the rest
    // of what the registry holds, because the index line is what the read is for.
    [Fact]
    public async Task GetAsync_AnUnavailableEntityServingNoChoices_ListsTheRegistrysCapabilities()
    {
        var client = new FakeHaClient
        {
            States =
            {
                Entity("media_player.receiver", "unavailable"),
                Entity("select.mop_mode", "unavailable", ("restored", JsonValue.Create(true)))
            },
            Registry =
            {
                ["media_player.receiver"] = Capabilities("media_player.receiver", new JsonObject { ["source_list"] = new JsonArray("TV", "Bluetooth") }),
                ["select.mop_mode"] = Capabilities("select.mop_mode", new JsonObject { ["options"] = new JsonArray("standard", "deep"), ["min"] = 1 })
            }
        };
        var provider = new HaCatalogProvider(() => client, new FakeTimeProvider());

        var catalog = await provider.GetAsync(CancellationToken.None);

        catalog.EntityById("media_player.receiver")!.Attributes["source_list"]!.AsArray()
            .Select(v => v!.GetValue<string>()).ShouldBe(["TV", "Bluetooth"]);
        var select = catalog.EntityById("select.mop_mode")!;
        select.Attributes["options"]!.AsArray().Select(v => v!.GetValue<string>()).ShouldBe(["standard", "deep"]);
        select.Attributes.ShouldNotContainKey("min");
        select.Attributes.ShouldContainKey("restored");
        client.RegistryReads.ShouldHaveSingleItem().ShouldBe(["media_player.receiver", "select.mop_mode"], ignoreOrder: true);
    }

    // The registry is asked only for what the states endpoint left blank: an available entity is
    // taken at its word (a list it serves, or none), and a home with everything on asks nothing.
    [Fact]
    public async Task GetAsync_AnAvailableEntity_IsNotLookedUpInTheRegistry()
    {
        var client = new FakeHaClient
        {
            States =
            {
                Entity("media_player.receiver", "on", ("source_list", new JsonArray("TV"))),
                Entity("light.kitchen", "off"),
                Entity("select.mop_mode", "unavailable", ("options", new JsonArray("standard")))
            },
            Registry = { ["media_player.receiver"] = Capabilities("media_player.receiver", new JsonObject { ["source_list"] = new JsonArray("TV", "Bluetooth") }) }
        };
        var provider = new HaCatalogProvider(() => client, new FakeTimeProvider());

        var catalog = await provider.GetAsync(CancellationToken.None);

        catalog.EntityById("media_player.receiver")!.Attributes["source_list"]!.AsArray().Count.ShouldBe(1);
        client.RegistryReads.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAsync_ARegistryReadThatFails_LeavesTheEntitiesAsServed_KeepsTheCatalog_AndWarns()
    {
        var client = new FakeHaClient
        {
            States = { Entity("media_player.receiver", "unavailable"), Entity("light.kitchen", "off") },
            RegistryFailure = new HttpRequestException("websocket unreachable"),
            TimeZone = "Europe/Madrid"
        };
        var log = new CapturingLoggerProvider(LogLevel.Warning);
        var provider = new HaCatalogProvider(
            () => client, new FakeTimeProvider(), logger: new Logger<HaCatalogProvider>(new LoggerFactory([log])));

        var catalog = await provider.GetAsync(CancellationToken.None);

        catalog.Entities.Count.ShouldBe(2);
        catalog.EntityById("media_player.receiver")!.Attributes.ShouldNotContainKey("source_list");
        log.Messages.ShouldHaveSingleItem().ShouldContain("registry");
    }

    // The living-room TV goes `unavailable` seconds into standby, and the states endpoint then
    // serves its remote with a friendly name and its features, nothing else — no `activity_list`,
    // at the one moment the apps are asked for ("turn on the TV and put Plex"). Home Assistant keeps
    // the apps in the Android TV Remote entry's options, which the catalog reads back for exactly
    // the remotes that serve no list: nothing remembered from a warmer moment, nothing the set has
    // to be on for. The entry is the one the registry names for the remote, because the
    // integration's own entities are nowhere while the set is off at the home's start — a real
    // index built four minutes after a restart listed the TV with no apps for that reason.
    [Fact]
    public async Task GetAsync_AnAndroidTvRemoteServingNoApps_ListsTheOnesItsEntryIsConfiguredWith()
    {
        var client = new FakeHaClient
        {
            States = { Entity("remote.tv", "unavailable", ("supported_features", JsonValue.Create(4))) },
            Registry = { ["remote.tv"] = AndroidTv("remote.tv", "entry-1") },
            AndroidTvApps = { ["entry-1"] = ["Plex", "Netflix"] }
        };
        var provider = new HaCatalogProvider(() => client, new FakeTimeProvider());

        var catalog = await provider.GetAsync(CancellationToken.None);

        var remote = catalog.EntityById("remote.tv").ShouldNotBeNull();
        remote.State.ShouldBe("unavailable");
        remote.Attributes["activity_list"]!.AsArray().Select(v => v!.GetValue<string>()).ShouldBe(["Plex", "Netflix"]);
        remote.Attributes["supported_features"]!.GetValue<int>().ShouldBe(4);
    }

    // A remote that serves its list is taken at its word: the options are not read, so a home with
    // the TV on costs no flow per catalog build. And only that integration's remotes are asked —
    // another platform's options flow is a different form, and its media player is no remote.
    [Fact]
    public async Task GetAsync_ARemoteServingItsApps_OrOfAnotherPlatform_OpensNoFlow()
    {
        var client = new FakeHaClient
        {
            States =
            {
                Entity("remote.tv", "on", ("activity_list", new JsonArray("Plex"))),
                Entity("remote.hub", "unavailable"),
                Entity("media_player.tv", "unavailable")
            },
            Registry =
            {
                ["remote.tv"] = AndroidTv("remote.tv", "entry-1"),
                ["remote.hub"] = new HaRegistryEntry { EntityId = "remote.hub", Platform = "harmony", ConfigEntryId = "entry-2" },
                ["media_player.tv"] = AndroidTv("media_player.tv", "entry-1")
            },
            AndroidTvApps = { ["entry-1"] = ["Plex", "DAZN"], ["entry-2"] = ["Watch TV"] }
        };
        var provider = new HaCatalogProvider(() => client, new FakeTimeProvider());

        var catalog = await provider.GetAsync(CancellationToken.None);

        catalog.EntityById("remote.tv")!.Attributes["activity_list"]!.AsArray().Count.ShouldBe(1);
        catalog.EntityById("remote.hub")!.Attributes.ShouldNotContainKey("activity_list");
        catalog.EntityById("media_player.tv")!.Attributes.ShouldNotContainKey("activity_list");
        client.AndroidTvAppsReads.ShouldBeEmpty();
    }

    // The options are a convenience for one index line, not the catalog's substance: a read that
    // fails leaves the remote as the states endpoint served it and the catalog whole, said once in
    // the log rather than blanking the mount for the failure TTL.
    [Fact]
    public async Task GetAsync_AnAppsReadThatFails_LeavesTheRemoteAsServed_KeepsTheCatalog_AndWarns()
    {
        var client = new FakeHaClient
        {
            States = { Entity("remote.tv", "unavailable"), Entity("light.kitchen", "off") },
            Registry = { ["remote.tv"] = AndroidTv("remote.tv", "entry-1") },
            AndroidTvAppsFailure = new HttpRequestException("options unreachable"),
            TimeZone = "Europe/Madrid"
        };
        var log = new CapturingLoggerProvider(LogLevel.Warning);
        var provider = new HaCatalogProvider(
            () => client, new FakeTimeProvider(), logger: new Logger<HaCatalogProvider>(new LoggerFactory([log])));

        var catalog = await provider.GetAsync(CancellationToken.None);

        catalog.Entities.Count.ShouldBe(2);
        catalog.EntityById("remote.tv")!.Attributes.ShouldNotContainKey("activity_list");
        log.Messages.ShouldHaveSingleItem().ShouldContain("remote.tv");
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