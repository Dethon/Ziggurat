using Agent.Settings;
using Domain.DTOs;
using Shouldly;

namespace Tests.Eval.Fixtures;

// The model a pass drives, taken from the environment rather than from the shipped file: a
// parity run compares two models against one definition, and editing Agent/appsettings.json to
// switch is a working-tree change that has already been swept into a commit once.
public class EvalModelTests
{
    [Fact]
    public void Apply_NothingConfigured_LeavesTheShippedSettingsAlone()
    {
        var shipped = Shipped();

        EvalModel.Apply(shipped, model: null, provider: null).ShouldBeSameAs(shipped);
        EvalModel.Apply(shipped, model: " ", provider: "").ShouldBeSameAs(shipped);
    }

    [Fact]
    public void Apply_AModel_ReachesEveryAgentAndSubagent()
    {
        var applied = EvalModel.Apply(Shipped(), model: "openai/gpt-5.6-luna", provider: null);

        applied.Agents.Select(a => a.Model).ShouldAllBe(m => m == "openai/gpt-5.6-luna");
        applied.SubAgents.Select(s => s.Model).ShouldAllBe(m => m == "openai/gpt-5.6-luna");
    }

    [Fact]
    public void Apply_AModel_LiftsTheProviderPinAndKeepsTheRestOfTheRouting()
    {
        // The shipped `only` names the vendor of the shipped model; kept, it would refuse every
        // endpoint the override can be served from. The sort and its threshold are the
        // deployment's preference about latency, not about who serves, so they stay.
        var applied = EvalModel.Apply(Shipped(), model: "openai/gpt-5.6-luna", provider: null);

        var nabu = applied.Agents.Single(a => a.Id == "nabu").ProviderRouting.ShouldNotBeNull();
        nabu.Only.ShouldBeNull();
        nabu.Order.ShouldBeNull();
        nabu.Sort.ShouldBe(ProviderSort.Latency);
        nabu.PreferredMinThroughput.ShouldNotBeNull().P50.ShouldBe(70);
        nabu.Ignore.ShouldBe(["azure"]);

        applied.Agents.Single(a => a.Id == "jack").ProviderRouting.ShouldBeNull();
        applied.SubAgents.Single().ProviderRouting.ShouldNotBeNull().Only.ShouldBeNull();
    }

    [Fact]
    public void Apply_AProvider_PinsEveryAgentAndSubagentToIt()
    {
        var applied = EvalModel.Apply(Shipped(), model: "openai/gpt-5.6-luna", provider: "openai");

        applied.Agents.Select(a => a.ProviderRouting?.Only)
            .ShouldAllBe(only => only != null && only.SequenceEqual(new[] { "openai" }));
        applied.SubAgents.Single().ProviderRouting.ShouldNotBeNull().Only.ShouldBe(["openai"]);
        applied.Agents.Single(a => a.Id == "nabu").ProviderRouting!.Sort.ShouldBe(ProviderSort.Latency);
    }

    [Fact]
    public void Apply_AProviderAlone_RepinsWithoutTouchingTheModel()
    {
        var applied = EvalModel.Apply(Shipped(), model: null, provider: "z-ai");

        applied.Agents.Select(a => a.Model).ShouldAllBe(m => m == "z-ai/glm-5.3-flash");
        applied.Agents.Select(a => a.ProviderRouting?.Only)
            .ShouldAllBe(only => only != null && only.SequenceEqual(new[] { "z-ai" }));
    }

    private static AgentSettings Shipped() => new()
    {
        OpenRouter = new OpenRouterConfiguration { ApiUrl = "https://openrouter.ai/api/v1/", ApiKey = "" },
        Redis = new RedisConfiguration { ConnectionString = "localhost" },
        Agents =
        [
            new AgentDefinition
            {
                Id = "jack", Name = "Jack", Model = "z-ai/glm-5.3-flash", McpServerEndpoints = []
            },
            new AgentDefinition
            {
                Id = "nabu", Name = "Nabu", Model = "z-ai/glm-5.3-flash", McpServerEndpoints = [],
                ProviderRouting = new ProviderRouting
                {
                    Only = ["z-ai"],
                    Order = ["z-ai"],
                    Ignore = ["azure"],
                    Sort = ProviderSort.Latency,
                    PreferredMinThroughput = new ProviderThreshold { P50 = 70 }
                }
            }
        ],
        SubAgents =
        [
            new SubAgentDefinition
            {
                Id = "jonas-worker", Name = "Jonas Worker", Model = "z-ai/glm-5.3-flash",
                McpServerEndpoints = [],
                ProviderRouting = new ProviderRouting { Only = ["z-ai"], Sort = ProviderSort.Throughput }
            }
        ]
    };
}