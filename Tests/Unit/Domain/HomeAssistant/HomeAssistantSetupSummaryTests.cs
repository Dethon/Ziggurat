using System.Text.Json.Nodes;
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