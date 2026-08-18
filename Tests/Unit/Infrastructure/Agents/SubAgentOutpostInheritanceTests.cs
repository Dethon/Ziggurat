using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Prompts;
using Infrastructure.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

// The half of the rule that only the factory can get wrong. The projection decides the
// conjunction and its own suite proves every combination of the two flags, but what the parent
// contributes to the spawn is composed here — and an argument that stopped being passed would
// leave the projection deciding the same rule against a value nobody supplied.
//
// Driven the whole way: an agent is created, the spawn delegate it was given runs, and the
// subagent that comes back warms up a session. Asking the registry at all is what a session build
// does only when its spec is opted in, so a registry that was never asked is the observable.
public sealed class SubAgentOutpostInheritanceTests
{
    private static readonly SubAgentDefinition _worker = new()
    {
        Id = "worker",
        Name = "Worker",
        Model = "test-model",
        McpServerEndpoints = [],
        UsesOutposts = true
    };

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task ASpawnedSubAgent_AsksTheRegistryOnlyWhereItsParentIsOptedIn(
        bool parentUsesOutposts, int expectedLookups)
    {
        var registry = new CountingRegistry();

        await using var subAgent = await SpawnAsync(parentUsesOutposts, registry);
        var session = await subAgent.CreateSessionAsync();
        await subAgent.WarmupSessionAsync(session);

        registry.Asked.ShouldBe(expectedLookups);
    }

    // The parent's own build, run for its side effect: the feature config it is handed carries the
    // spawn delegate, and running that delegate is the only way the parent's contribution reaches
    // the projection.
    private static async Task<DisposableAgent> SpawnAsync(bool parentUsesOutposts, IOutpostRegistry registry)
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

        var parent = new AgentDefinition
        {
            Id = "jack",
            Name = "Jack",
            Model = "test-model",
            McpServerEndpoints = [],
            UsesOutposts = parentUsesOutposts
        };
        var options = new Mock<IOptionsMonitor<AgentRegistryOptions>>();
        options.Setup(o => o.CurrentValue).Returns(new AgentRegistryOptions { Agents = [parent] });

        var services = new ServiceCollection()
            .AddSingleton(new OutpostAccess(registry, "s3cret"))
            .AddSingleton(new Mock<IThreadStateStore>().Object)
            .BuildServiceProvider();

        var factory = new MultiAgentFactory(
            services,
            new AgentDefinitionProvider(options.Object, new CustomAgentRegistry()),
            new OpenRouterConfig { ApiUrl = "http://test", ApiKey = "test-key" },
            toolRegistry.Object);

        await using var agent = factory.Create(
            new AgentKey("conv-1", "jack"), "fran", "jack", new Mock<IToolApprovalHandler>().Object);

        var spawn = captured.ShouldHaveSingleItem().SubAgentFactory.ShouldNotBeNull();
        return await Task.FromResult(spawn(_worker));
    }

    private sealed class CountingRegistry : IOutpostRegistry
    {
        public int Asked { get; private set; }

        public Task<IReadOnlyList<OutpostRegistration>> ListAsync(CancellationToken ct = default)
        {
            Asked++;
            return Task.FromResult<IReadOnlyList<OutpostRegistration>>([]);
        }

        public Task RegisterAsync(OutpostRegistration registration, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<OutpostVerdict?> KeepAliveAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<OutpostVerdict?>(OutpostVerdict.Unknown);

        public Task RecordVerdictAsync(string name, OutpostVerdict verdict, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> DeregisterAsync(string name, CancellationToken ct = default) =>
            Task.FromResult(true);
    }
}