using Domain.Contracts;
using Domain.DTOs.Channel;
using Infrastructure.Agents;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

// The whitelist an agent checks a patch against: the configured ids, then whatever the Lemonade
// chat host has right now, under the host's namespace — read live, so a model that appeared after
// the agent was built is honoured and one that vanished is refused.
public sealed class PatchableModelWhitelistTests
{
    private sealed class LiveSource : ILemonadeModelSource
    {
        public IReadOnlyList<LemonadeModel> Current { get; set; } = [];
    }

    [Fact]
    public void Ids_AreTheConfiguredOnesThenTheHostsCurrentModelsNamespaced()
    {
        var source = new LiveSource { Current = [new LemonadeModel("Qwen3.8-27B-GGUF-UD-Q4_K_XL", [], null)] };
        var whitelist = new PatchableModelWhitelist(["z-ai/glm-5.2"], source);

        whitelist.Ids.ShouldBe(["z-ai/glm-5.2", "lemonade/Qwen3.8-27B-GGUF-UD-Q4_K_XL"]);
    }

    [Fact]
    public void Ids_FollowTheSourceAsItChanges()
    {
        var source = new LiveSource();
        var whitelist = new PatchableModelWhitelist([], source);
        whitelist.Ids.ShouldBeEmpty();

        source.Current = [new LemonadeModel("local", [], null)];
        whitelist.Ids.ShouldBe(["lemonade/local"]);

        source.Current = [];
        whitelist.Ids.ShouldBeEmpty();
    }
}