using System.Text.Json;
using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.Household;
using Domain.Judgments;
using Shouldly;

namespace Tests.Unit.Domain.DTOs;

// A household's key travels on records that are logged and asserted on whole — a worker's spawn, a
// judgment's caller — so it must never show in what they print or write.
public class ModelKeyTests
{
    private const string Secret = "sk-or-v1-household-secret";

    [Fact]
    public void ARecordCarryingAKey_PrintsWithoutIt()
    {
        var spawn = new SpawnContext("conv-1", "fran", [], UsesOutposts: false, "m", Key: new ModelKey(Secret));
        var caller = new JudgmentCaller("m", "fran") { Payer = new ModelPayer("household", new ModelKey(Secret)) };

        spawn.ToString().ShouldNotContain(Secret);
        caller.ToString().ShouldNotContain(Secret);
    }

    // The agent's view of a household carries the key in clear, as the wire must; printed, it does not.
    [Fact]
    public void TheAgentsViewOfAHousehold_PrintsWithoutItsKey()
    {
        var view = new AgentHouseholdConfig(
            "household", false, new HouseholdSettings { Name = "Casa", TimeZone = "UTC" }, new AgentsSection(),
            new FeaturesSection(), new SpacesSection(), Secret);
        var agents = new HouseholdAgents { HouseholdId = "household", Agents = [], Configuration = view };

        view.ToString().ShouldNotContain(Secret);
        agents.ToString().ShouldNotContain(Secret);
        view.ToString().ShouldContain("household");
    }

    [Fact]
    public void ARecordCarryingAKey_SerializesWithoutIt()
    {
        var payer = new ModelPayer("household", new ModelKey(Secret));

        JsonSerializer.Serialize(payer).ShouldNotContain(Secret);
    }

    [Fact]
    public void AKey_IsNeverReadBackFromJson() =>
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<ModelPayer>("""{"TenantId":"h","Key":"[a model key]"}"""));

    [Fact]
    public void TwoKeys_AreEqualByTheirValue()
    {
        new ModelPayer("h", new ModelKey(Secret)).ShouldBe(new ModelPayer("h", new ModelKey(Secret)));
        new ModelKey(Secret).ShouldNotBe(new ModelKey("another"));
    }
}