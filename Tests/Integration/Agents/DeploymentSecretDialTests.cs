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

// The agent's half of the deployment secret, driven from the factory a deployment builds agents
// with to a server gated the way every deployment server is. A configured endpoint that did not
// present the secret fails the session (ADR-0027), so a session that builds is the observable —
// for an agent, and for a worker it spawns, whose endpoints are configured too and reach the same
// servers.
public sealed class DeploymentSecretDialTests : IAsyncLifetime
{
    private RunningServer _server = null!;

    private sealed record GatedSettings : IMcpHostSettings
    {
        public McpGateSettings Mcp => McpTestSecret.Gate;
    }

    public async Task InitializeAsync() =>
        _server = await InMemoryMcpServer.StartAsync(services => services
            .AddToolServer(new GatedSettings())
            .WithTools<TestEchoTool>());

    public async Task DisposeAsync() => await _server.DisposeAsync();

    [Fact]
    public async Task AnAgentsConfiguredEndpoints_PresentTheDeploymentSecret()
    {
        var (factory, _) = Factory();
        await using var agent = factory.Create(
            new AgentKey("conv-1", "jack"), "fran", "jack", new Mock<IToolApprovalHandler>().Object);

        var session = await agent.CreateSessionAsync();
        await agent.WarmupSessionAsync(session);
    }

    [Fact]
    public async Task AWorkersConfiguredEndpoints_PresentTheDeploymentSecret()
    {
        var (factory, captured) = Factory();
        await using var agent = factory.Create(
            new AgentKey("conv-1", "jack"), "fran", "jack", new Mock<IToolApprovalHandler>().Object);
        var spawn = captured.ShouldHaveSingleItem().SubAgentFactory.ShouldNotBeNull();

        await using var worker = spawn(new SubAgentDefinition
        {
            Id = "worker",
            Name = "Worker",
            Model = "test-model",
            McpServerEndpoints = [_server.Endpoint]
        });
        var session = await worker.CreateSessionAsync();
        await worker.WarmupSessionAsync(session);
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
                    Id = "jack",
                    Name = "Jack",
                    Model = "test-model",
                    McpServerEndpoints = [_server.Endpoint]
                }
            ]
        });

        var services = new ServiceCollection()
            .AddSingleton(McpTestSecret.Gate)
            .AddSingleton(new Mock<IThreadStateStore>().Object)
            .BuildServiceProvider();

        return (new MultiAgentFactory(
            services,
            new AgentDefinitionProvider(options.Object, new CustomAgentRegistry()),
            new OpenRouterConfig { ApiUrl = "http://test", ApiKey = "test-key" },
            toolRegistry.Object), captured);
    }
}