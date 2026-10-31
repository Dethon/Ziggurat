using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Prompts;
using Domain.Security;
using Infrastructure.Agents;
using Mcp.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Integration.McpServers;

namespace Tests.Integration.Agents;

// A household's own server named in an agent's definition, driven from the factory a deployment
// builds agents with: the name is resolved against the household the turn is for, when the session
// is built, through whatever the host says reaches that household's servers — the relay in a
// deployment, the server a stack hosts in process here. A worker reaches its parent's household's.
public sealed class OwnServerDialTests : IAsyncLifetime
{
    private const string Household = "4f1c9a7e2b6d4e0a9c3b8d5e7f1a2c4b";

    private RunningServer _vault = null!;
    private InProcessOwnServers _servers = null!;

    private sealed record GatedSettings : IMcpHostSettings
    {
        public McpGateSettings Mcp => McpTestSecret.Gate;
    }

    public async Task InitializeAsync()
    {
        _vault = await InMemoryMcpServer.StartAsync(services => services
            .AddToolServer(new GatedSettings())
            .WithTools<TestEchoTool>());
        _servers = new InProcessOwnServers(new Dictionary<string, string> { ["vault"] = _vault.Endpoint });
    }

    public async Task DisposeAsync() => await _vault.DisposeAsync();

    [Fact]
    public async Task AnAgentNamingAHouseholdServer_ReachesTheOneItsTurnsHouseholdResolvesTo()
    {
        var (factory, _) = Factory();
        await using var agent = factory.Create(
            new AgentKey("conv-1", "jonas"), "fran", "jonas", new Mock<IToolApprovalHandler>().Object, tenantId: Household);

        var session = await agent.CreateSessionAsync();
        await agent.WarmupSessionAsync(session);

        _servers.Asked.ShouldBe([(Household, "vault")]);
    }

    [Fact]
    public async Task AWorkerNamingAHouseholdServer_ReachesItsParentsHouseholds()
    {
        var (factory, captured) = Factory();
        await using var agent = factory.Create(
            new AgentKey("conv-1", "jonas"), "fran", "jonas", new Mock<IToolApprovalHandler>().Object, tenantId: Household);
        var spawn = captured.ShouldHaveSingleItem().SubAgentFactory.ShouldNotBeNull();

        await using var worker = spawn(new SubAgentDefinition
        {
            Id = "worker",
            Name = "Worker",
            McpServerEndpoints = ["vault"]
        });
        var session = await worker.CreateSessionAsync();
        await worker.WarmupSessionAsync(session);

        _servers.Asked.ShouldBe([(Household, "vault")]);
    }

    private (MultiAgentFactory Factory, List<FeatureConfig> Captured) Factory()
    {
        var captured = new List<FeatureConfig>();
        var toolRegistry = new Mock<IDomainToolRegistry>();
        toolRegistry
            .Setup(r => r.GetToolsForFeatures(It.IsAny<IEnumerable<string>>(), It.IsAny<FeatureConfig>()))
            .Callback<IEnumerable<string>, FeatureConfig>((_, config) => captured.Add(config))
            .Returns(Enumerable.Empty<AIFunction>());
        toolRegistry
            .Setup(r => r.GetPromptsForFeatures(It.IsAny<IEnumerable<string>>()))
            .Returns(Enumerable.Empty<PromptSection>());

        var options = new Mock<IOptionsMonitor<AgentRegistryOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new AgentRegistryOptions
        {
            Agents =
            [
                new AgentDefinition
                {
                    Id = "jonas",
                    Name = "Jonas",
                    Model = "test-model",
                    McpServerEndpoints = ["vault"]
                }
            ]
        });

        var services = new ServiceCollection()
            .AddSingleton(McpTestSecret.Credentials)
            .AddSingleton(McpTestSecret.CallTokens)
            .AddSingleton<IOwnServers>(_servers)
            .AddSingleton(new Mock<IThreadStateStore>().Object)
            .BuildServiceProvider();

        return (new MultiAgentFactory(
            services,
            new AgentDefinitionProvider(options.Object, new CustomAgentRegistry()),
            new OpenRouterConfig { ApiUrl = "http://test", ApiKey = "test-key" },
            toolRegistry.Object), captured);
    }
}