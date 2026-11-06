using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
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
        var provider = FixedHouseholdAgents.Provider(definition);
        var services = new Mock<IServiceProvider>();
        services.Setup(sp => sp.GetService(typeof(ILemonadeModelSource))).Returns(source);
        services.Setup(sp => sp.GetService(typeof(LemonadeChatHostOptions)))
            .Returns(new LemonadeChatHostOptions { ApiUrl = "http://lemonade-chat-host.invalid:13305/api/v1" });
        services.Setup(sp => sp.GetService(typeof(IThreadStateStore))).Returns(Mock.Of<IThreadStateStore>());
        var factory = new MultiAgentFactory(
            services.Object, provider,
            new OpenRouterConfig { ApiUrl = "http://openrouter.invalid/api/v1", ApiKey = "key" },
            Mock.Of<IDomainToolRegistry>());

        await using var agent = await factory.CreateAsync(
            new AgentKey("conv-1", "jonas"), "fran", "jonas", Mock.Of<IToolApprovalHandler>());
        source.Current = [new LemonadeModel("local", [], null)];
        var message = new ChatMessage(ChatRole.User, "hi");
        message.SetConfigPatch(new AgentConfigPatch { Model = LemonadeModelId.Namespaced("local") });

        var error = await Should.ThrowAsync<LemonadeChatHostException>(
            () => agent.RunStreamingAsync([message]).ToListAsync().AsTask());

        error.Message.ShouldContain("http://lemonade-chat-host.invalid:13305/api/v1");
    }

    // The box is the household's that provides it — the home's — so another household's turn naming
    // one of its models is never sent there, though the host has it: the turn is refused for want of
    // a host of its own rather than run on hardware and a key it does not pay for.
    [Fact]
    public async Task APatchNamingALemonadeModel_FromAHouseholdThatProvidesNoBox_IsNeverSentToTheHost()
    {
        var source = new LiveSource { Current = [new LemonadeModel("local", [], null)] };
        var definition = new AgentDefinition
        {
            Id = "jonas", Name = "Jonas", Model = "configured/model", McpServerEndpoints = []
        };
        var household = new AgentHouseholdConfig(
            "rivas", false, new HouseholdSettings(), new AgentsSection(), new FeaturesSection(), new SpacesSection(),
            "household-key");
        var provider = new AgentDefinitionProvider(new OtherHousehold(definition, household), new CustomAgentRegistry());
        var services = new Mock<IServiceProvider>();
        services.Setup(sp => sp.GetService(typeof(ILemonadeModelSource))).Returns(source);
        services.Setup(sp => sp.GetService(typeof(LemonadeChatHostOptions)))
            .Returns(new LemonadeChatHostOptions { ApiUrl = "http://lemonade-chat-host.invalid:13305/api/v1" });
        services.Setup(sp => sp.GetService(typeof(IThreadStateStore))).Returns(Mock.Of<IThreadStateStore>());
        var factory = new MultiAgentFactory(
            services.Object, provider,
            new OpenRouterConfig { ApiUrl = "http://openrouter.invalid/api/v1", ApiKey = "key" },
            Mock.Of<IDomainToolRegistry>());

        await using var agent = await factory.CreateAsync(
            new AgentKey("conv-1", "jonas"), "ines", "jonas", Mock.Of<IToolApprovalHandler>(), tenantId: "rivas");
        var message = new ChatMessage(ChatRole.User, "hi");
        message.SetConfigPatch(new AgentConfigPatch { Model = LemonadeModelId.Namespaced("local") });

        var error = await Should.ThrowAsync<LemonadeChatHostException>(
            () => agent.RunStreamingAsync([message]).ToListAsync().AsTask());

        error.Message.ShouldNotContain("lemonade-chat-host.invalid");
        // A device that kept its pick after the household's box went away is told what to do about it,
        // in words a person reads, not an address that is not there.
        error.Message.ShouldBe(
            "No Lemonade box serves this household now, so 'local' was not run: pick another model in the menu.");
    }

    // A household that saved a box of its own runs a turn patched to one of that box's models on that
    // box — never the deployment's, which is the home's.
    [Fact]
    public async Task APatchNamingAModelOfAHouseholdsOwnBox_IsSentToThatBox()
    {
        var definition = new AgentDefinition
        {
            Id = "jonas", Name = "Jonas", Model = "configured/model", McpServerEndpoints = []
        };
        var household = new AgentHouseholdConfig(
            "rivas", false, new HouseholdSettings(), new AgentsSection(), new FeaturesSection(), new SpacesSection(),
            "household-key");
        var provider = new AgentDefinitionProvider(new OtherHousehold(definition, household), new CustomAgentRegistry());
        var ownBox = new LemonadeHost(
            new LemonadeChatHostOptions { ApiUrl = "http://rivas-box.invalid:13305/api/v1" },
            new LiveSource { Current = [new LemonadeModel("theirs", [], null)] });
        var hosts = new Mock<ILemonadeHosts>();
        hosts.Setup(h => h.For("rivas", false)).Returns(ownBox);
        var services = new Mock<IServiceProvider>();
        services.Setup(sp => sp.GetService(typeof(ILemonadeHosts))).Returns(hosts.Object);
        services.Setup(sp => sp.GetService(typeof(LemonadeChatHostOptions)))
            .Returns(new LemonadeChatHostOptions { ApiUrl = "http://lemonade-chat-host.invalid:13305/api/v1" });
        services.Setup(sp => sp.GetService(typeof(IThreadStateStore))).Returns(Mock.Of<IThreadStateStore>());
        var factory = new MultiAgentFactory(
            services.Object, provider,
            new OpenRouterConfig { ApiUrl = "http://openrouter.invalid/api/v1", ApiKey = "key" },
            Mock.Of<IDomainToolRegistry>());

        await using var agent = await factory.CreateAsync(
            new AgentKey("conv-1", "jonas"), "ines", "jonas", Mock.Of<IToolApprovalHandler>(), tenantId: "rivas");
        var message = new ChatMessage(ChatRole.User, "hi");
        message.SetConfigPatch(new AgentConfigPatch { Model = LemonadeModelId.Namespaced("theirs") });

        var error = await Should.ThrowAsync<LemonadeChatHostException>(
            () => agent.RunStreamingAsync([message]).ToListAsync().AsTask());

        error.Message.ShouldContain("rivas-box.invalid");
        error.Message.ShouldNotContain("lemonade-chat-host.invalid");
    }

    private sealed class OtherHousehold(AgentDefinition agent, AgentHouseholdConfig configuration) : IHouseholdAgentSource
    {
        public Task<HouseholdAgents> ForAsync(string? householdId, CancellationToken ct) =>
            Task.FromResult(HouseholdAgents.Fixed([agent]) with { HouseholdId = householdId, Configuration = configuration });
    }
}