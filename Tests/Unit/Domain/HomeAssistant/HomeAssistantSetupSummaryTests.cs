using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Voice;
using Domain.Exceptions;
using Domain.Prompts;
using Domain.Tools.HomeAssistant.Vfs;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Unit.Domain.HomeAssistant.Vfs;
using static Tests.Unit.Domain.HomeAssistant.Vfs.FakeHaClient;

namespace Tests.Unit.Domain.HomeAssistant;

public class HomeAssistantSetupSummaryTests
{
    private static HomeAssistantSetupSummary Build(FakeHaClient client) =>
        new(new HaCatalogProvider(() => client, new FakeTimeProvider()));

    // Every entity used to be printed twice, once per tree, which on the live house was 167
    // duplicated lines — 12.7k characters of the ~28k-token prefix saying what the other half
    // already said. One tree plus the rule for deriving the other costs nothing in reach: both
    // path forms still resolve, only the index stops spelling both out.
    [Fact]
    public async Task GetAsync_ListsEachEntityOnceUnderItsRoom()
    {
        var client = new FakeHaClient
        {
            States =
            {
                Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen"))),
                Entity("sensor.salon_temp", "21", ("friendly_name", JsonValue.Create("Salon Temp"))),
            },
            AreaTemplateJson = """{"areas":[{"id":"salon","name":"Salón","entities":["sensor.salon_temp"]}]}"""
        };

        var text = await Build(client).GetAsync(CancellationToken.None);

        text.ShouldContain("## Current Home Assistant setup");
        text.ShouldContain("### salon\nsensor.salon_temp_(salon-temp)\n");
        text.ShouldContain("### unassigned\nlight.kitchen_(kitchen)\n");
        // The entities tree is still reachable and the header still explains it; what is gone is
        // the second copy of every entity, so assert on the listed paths, not on the mention.
        text.ShouldNotContain("/ha/entities/light/kitchen");
        text.ShouldNotContain("/ha/entities/sensor/salon_temp");
        text.ShouldNotContain("/ha/areas/salon/");
    }

    [Fact]
    public async Task GetAsync_RoomsAndEntriesAreLexicallySorted()
    {
        var client = new FakeHaClient
        {
            States =
            {
                Entity("light.b_lamp", "off"),
                Entity("light.a_lamp", "off"),
                Entity("light.hall", "off"),
            },
            AreaTemplateJson = """{"areas":[{"id":"attic","name":"Attic","entities":["light.hall"]}]}"""
        };

        var text = await Build(client).GetAsync(CancellationToken.None);

        var idxAttic = text.IndexOf("### attic", StringComparison.Ordinal);
        var idxUnassigned = text.IndexOf("### unassigned", StringComparison.Ordinal);
        var idxA = text.IndexOf("light.a_lamp", StringComparison.Ordinal);
        var idxB = text.IndexOf("light.b_lamp", StringComparison.Ordinal);
        idxAttic.ShouldBeGreaterThanOrEqualTo(0);
        idxUnassigned.ShouldBeGreaterThan(idxAttic);
        idxA.ShouldBeGreaterThan(idxUnassigned);
        idxB.ShouldBeGreaterThan(idxA);
    }

    [Fact]
    public async Task GetAsync_EntityWithoutFriendlyName_OmitsSlugSuffix()
    {
        var client = new FakeHaClient
        {
            States = { Entity("switch.bare", "off") },
            AreaTemplateJson = """{"areas":[]}"""
        };

        var text = await Build(client).GetAsync(CancellationToken.None);

        text.ShouldContain("### unassigned\nswitch.bare\n");
        text.ShouldNotContain("switch.bare_(");
    }

    [Fact]
    public async Task GetAsync_EmptyCatalog_ReturnsEmpty()
    {
        var client = new FakeHaClient();
        (await Build(client).GetAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAsync_ListsAvailableActionsPerEntityClass()
    {
        // The prompt used to tell the agent to `glob <entity-dir>/*.sh` to discover actions, which
        // cost a round trip per turn — and its first guess, the CLASS directory, always returns
        // nothing because action files live one level deeper. Naming the actions up front removes
        // both turns for ~350 tokens, a trade worth roughly 100:1 against a ~1.15s round trip.
        var client = new FakeHaClient
        {
            States =
            {
                Entity("light.kitchen", "off", ("friendly_name", JsonValue.Create("Kitchen"))),
                Entity("light.desk", "on", ("friendly_name", JsonValue.Create("Desk"))),
                Entity("sensor.salon_temp", "21", ("friendly_name", JsonValue.Create("Salon Temp"))),
            },
            Services =
            {
                Service("light", "turn_on", DomainTarget("light")),
                Service("light", "turn_off", DomainTarget("light")),
            }
        };

        var text = await Build(client).GetAsync(CancellationToken.None);

        text.ShouldContain("## Actions by entity class");
        text.ShouldContain("light: turn_off.sh, turn_on.sh");
        // A read-only class must not appear at all rather than as an empty entry.
        text.ShouldNotContain("sensor:");
    }

    [Fact]
    public async Task GetAsync_WithNoActionableEntities_OmitsTheActionTable()
    {
        var client = new FakeHaClient { States = { Entity("sensor.salon_temp", "21") } };

        var text = await Build(client).GetAsync(CancellationToken.None);

        text.ShouldNotContain("## Actions by entity class");
    }

}
public class HomeAssistantSetupSummaryWatchesTests
{
    // The agent discovers the feature from the index rather than from being told: the line names
    // the subtree, counts what exists and tells a paused or spent watch from a live one.
    [Fact]
    public async Task GetAsync_ListsTheWatchesLine_WithTheCountAndEachWatchsState()
    {
        var client = new FakeHaClient { States = { Entity("light.kitchen", "off") } };
        client.SeedAutomation("assistant_watch_laura-sugar-high", Watch("Laura's sugar", once: false));
        client.SeedAutomation("assistant_watch_washing-done", Watch("Washing done", once: true), on: false);
        client.SeedAutomation("assistant_watch_night-sugar", Watch("Night sugar", once: false), on: false);
        client.SeedAutomation("voice_alarm_bridge", new JsonObject
        {
            ["alias"] = "Voice alarm bridge", ["description"] = "hand made",
            ["triggers"] = new JsonArray(new JsonObject { ["trigger"] = "state" }), ["actions"] = new JsonArray()
        });
        var summary = new HomeAssistantSetupSummary(
            new HaCatalogProvider(() => client, new FakeTimeProvider()), new HaWatches(() => client));

        var text = await summary.GetAsync(CancellationToken.None);

        var line = text.Split('\n').Single(l => l.StartsWith("watches:", StringComparison.Ordinal));
        line.ShouldBe(
            "watches: `/ha/watches/<id>/watch.json` — 3 defined (`laura-sugar-high`, `night-sugar` (paused), `washing-done` (spent)); "
            + "see the guide's Watches section.");
    }

    [Fact]
    public async Task GetAsync_WithNoWatches_SaysNoneYet()
    {
        var client = new FakeHaClient { States = { Entity("light.kitchen", "off") } };
        var summary = new HomeAssistantSetupSummary(
            new HaCatalogProvider(() => client, new FakeTimeProvider()), new HaWatches(() => client));

        (await summary.GetAsync(CancellationToken.None)).ShouldContain("— 0 defined (none yet)");
    }

    // The rooms an announcement can target are the voice hub's, not the home's areas; the index
    // names them so the model never reaches for an area slug, and says nothing when the hub is
    // down rather than listing nothing as fact.
    [Fact]
    public async Task GetAsync_ListsTheVoiceSatellites_AsTheAnnounceTargets()
    {
        var client = new FakeHaClient { States = { Entity("light.kitchen", "off") } };
        var summary = new HomeAssistantSetupSummary(
            new HaCatalogProvider(() => client, new FakeTimeProvider()), new HaWatches(() => client),
            new FakeSatellites([new("FRAN-OFFICE-01", "Fran's office"), new("kitchen-01", "Kitchen")]));

        var text = await summary.GetAsync(CancellationToken.None);

        text.Split('\n').Single(l => l.StartsWith("voice satellites:", StringComparison.Ordinal)).ShouldBe(
            "voice satellites: FRAN-OFFICE-01 (room \"Fran's office\"), kitchen-01 (room \"Kitchen\") — an announce "
            + "target is one of these rooms or ids, never a Home Assistant area.");
    }

    [Fact]
    public async Task GetAsync_WithTheHubDown_SaysNothingAboutSatellites()
    {
        var client = new FakeHaClient { States = { Entity("light.kitchen", "off") } };
        var summary = new HomeAssistantSetupSummary(
            new HaCatalogProvider(() => client, new FakeTimeProvider()), new HaWatches(() => client), new FakeSatellites(null));

        (await summary.GetAsync(CancellationToken.None)).ShouldNotContain("voice satellites");
    }

    private sealed class FakeSatellites(IReadOnlyList<SatelliteDescriptor>? roster) : ISatelliteCatalog
    {
        public Task<IReadOnlyList<SatelliteDescriptor>> GetAllAsync(CancellationToken ct) =>
            roster is null ? throw new VoiceHubUnavailableException("connection refused") : Task.FromResult(roster);

        public Task<IReadOnlyList<string>> ResolveAsync(AnnounceTarget target, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([]);
    }

    private static JsonObject Watch(string name, bool once) => new()
    {
        ["alias"] = name,
        ["description"] = new HaWatchMetadata("jonas", [new HaPromptEffect("look into it")], once, null, null,
            new DateTimeOffset(2026, 9, 5, 9, 0, 0, TimeSpan.Zero)).ToJson(),
        ["triggers"] = new JsonArray(new JsonObject { ["trigger"] = "state", ["entity_id"] = "sensor.x" }),
        ["actions"] = new JsonArray()
    };
}

public class HomeAssistantSetupSummaryEveryEntityTests
{
    // Listing `history.sh` on every class line would put every read-only class into the table and
    // say the same word once per class; the index says it once instead.
    [Fact]
    public async Task GetAsync_AnEveryEntityAction_IsAnnouncedOnce_NotPerClass()
    {
        var client = new FakeHaClient
        {
            States =
            {
                Entity("light.kitchen", "off"),
                Entity("sensor.salon_temp", "21", ("state_class", JsonValue.Create("measurement")))
            },
            Services = { Service("light", "turn_on", DomainTarget("light")) }
        };
        var summary = new HomeAssistantSetupSummary(new HaCatalogProvider(
            () => client, new FakeTimeProvider(),
            extraServices: [HaHistoryActions.History, HaStatisticsActions.Statistics]));

        var text = await summary.GetAsync(CancellationToken.None);

        text.ShouldContain("every entity: history.sh\nevery entity with state_class: statistics.sh\n");
        text.ShouldContain("light: turn_on.sh");
        text.ShouldNotContain("light: history.sh");
        text.ShouldNotContain("sensor: history.sh");
        text.ShouldNotContain("sensor: statistics.sh");
    }

    // A narrowed action is announced only when some entity admits it: a home with no state_class
    // sensor has no statistics.sh anywhere, and a line naming it would send the model looking.
    [Fact]
    public async Task GetAsync_ANarrowedActionNoEntityAdmits_IsNotAnnounced()
    {
        var client = new FakeHaClient
        {
            States = { Entity("light.kitchen", "off"), Entity("sensor.glucose", "94") },
            Services = { Service("light", "turn_on", DomainTarget("light")) }
        };
        var summary = new HomeAssistantSetupSummary(new HaCatalogProvider(
            () => client, new FakeTimeProvider(),
            extraServices: [HaHistoryActions.History, HaStatisticsActions.Statistics]));

        var text = await summary.GetAsync(CancellationToken.None);

        text.ShouldContain("every entity: history.sh\n");
        text.ShouldNotContain("statistics.sh");
    }
}