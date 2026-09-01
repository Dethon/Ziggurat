using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

// An agent the factory builds honours a patch naming a model the Lemonade chat host has right
// now, even one discovered after the agent was built: the turn goes to the host. The host here
// is a name that does not resolve, so honouring it is observed as the host's own named failure —
// a refused patch would have gone to OpenRouter instead.
public sealed class MultiAgentFactoryLemonadeWhitelistTests
{
    private sealed class LiveSource : ILemonadeModelSource
    {
        public IReadOnlyList<LemonadeModel> Current { get; set; } = [];
    }

    [Fact]
    public async Task APatchNamingAModelTheHostGainedAfterTheAgentWasBuilt_IsSentToTheHost()
    {
        var source = new LiveSource();
        var definition = new AgentDefinition
        {
            Id = "jonas", Name = "Jonas", Model = "configured/model", McpServerEndpoints = []
        };
        var provider = new Mock<IAgentDefinitionProvider>();
        provider.Setup(p => p.GetAll(It.IsAny<string?>())).Returns([definition]);
        var services = new Mock<IServiceProvider>();
        services.Setup(sp => sp.GetService(typeof(ILemonadeModelSource))).Returns(source);
        services.Setup(sp => sp.GetService(typeof(LemonadeChatHostOptions)))
            .Returns(new LemonadeChatHostOptions { ApiUrl = "http://lemonade-chat-host.invalid:13305/api/v1" });
        services.Setup(sp => sp.GetService(typeof(IThreadStateStore))).Returns(Mock.Of<IThreadStateStore>());
        var factory = new MultiAgentFactory(
            services.Object, provider.Object,
            new OpenRouterConfig { ApiUrl = "http://openrouter.invalid/api/v1", ApiKey = "key" },
            Mock.Of<IDomainToolRegistry>());

        await using var agent = factory.Create(
            new AgentKey("conv-1", "jonas"), "fran", "jonas", Mock.Of<IToolApprovalHandler>());
        source.Current = [new LemonadeModel("local", [], null)];
        var message = new ChatMessage(ChatRole.User, "hi");
        message.SetConfigPatch(new AgentConfigPatch { Model = LemonadeModelId.Namespaced("local") });

        var error = await Should.ThrowAsync<LemonadeChatHostException>(
            () => agent.RunStreamingAsync([message]).ToListAsync().AsTask());

        error.Message.ShouldContain("http://lemonade-chat-host.invalid:13305/api/v1");
    }
}