using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public sealed class MultiAgentFactoryTests
{
    private static readonly AgentDefinition _builtInAgent = new()
    {
        Id = "built-in-id",
        Name = "Built-In",
        Model = "test-model",
        McpServerEndpoints = []
    };

    private readonly CustomAgentRegistry _customAgentRegistry = new();
    private readonly AgentDefinitionProvider _definitionProvider;
    private readonly MultiAgentFactory _sut;
    private readonly Mock<IToolApprovalHandler> _approvalHandler = new();

    public MultiAgentFactoryTests()
    {
        var registryOptions = new AgentRegistryOptions { Agents = [_builtInAgent] };

        var optionsMonitor = new Mock<IOptionsMonitor<AgentRegistryOptions>>();
        optionsMonitor.Setup(o => o.CurrentValue).Returns(registryOptions);

        var openRouterConfig = new OpenRouterConfig { ApiUrl = "http://test", ApiKey = "test-key" };

        var domainToolRegistry = new Mock<IDomainToolRegistry>();
        domainToolRegistry
            .Setup(r => r.GetToolsForFeatures(It.IsAny<IEnumerable<string>>(), It.IsAny<FeatureConfig>()))
            .Returns(Enumerable.Empty<AIFunction>());
        domainToolRegistry
            .Setup(r => r.GetPromptsForFeatures(It.IsAny<IEnumerable<string>>()))
            .Returns(Enumerable.Empty<string>());

        var stateStore = new Mock<IThreadStateStore>();

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider
            .Setup(sp => sp.GetService(typeof(IThreadStateStore)))
            .Returns(stateStore.Object);

        _definitionProvider = new AgentDefinitionProvider(optionsMonitor.Object, _customAgentRegistry);

        _sut = new MultiAgentFactory(
            serviceProvider.Object,
            _definitionProvider,
            openRouterConfig,
            domainToolRegistry.Object,
            null);
    }

    private AgentDefinition AddCustomAgent(string userId, string name = "TestBot", string model = "test-model")
    {
        var definition = new AgentDefinition
        {
            Id = $"custom-{Guid.NewGuid()}",
            Name = name,
            Model = model,
            McpServerEndpoints = []
        };
        _customAgentRegistry.Add(userId, definition);
        return definition;
    }

    public static IEnumerable<object[]> SuccessCases =>
    [
        ["null agent id", "user1", (Func<MultiAgentFactoryTests, string?>)(_ => null)],
        ["built-in agent id", "user1", (Func<MultiAgentFactoryTests, string?>)(_ => _builtInAgent.Id)],
        ["custom agent id for owning user", "user1", (Func<MultiAgentFactoryTests, string?>)(t => t.AddCustomAgent("user1").Id)]
    ];

    [Theory]
    [MemberData(nameof(SuccessCases))]
    public void Create_SupportedAgentIdentifier_ReturnsAgent(string _, string userId, Func<MultiAgentFactoryTests, string?> agentIdFactory)
    {
        var agentId = agentIdFactory(this);
        var agentKey = new AgentKey(ConversationId: "1:1", AgentId: "test");

        var agent = _sut.Create(agentKey, userId, agentId, _approvalHandler.Object);

        agent.ShouldNotBeNull();
    }

    public static IEnumerable<object?[]> ErrorCases =>
    [
        ["unknown agent id", "user1", (Func<MultiAgentFactoryTests, string>)(_ => "unknown-id"), "unknown-id"],
        ["custom agent unregistered before create", "user1", (Func<MultiAgentFactoryTests, string>)(t =>
        {
            var def = t.AddCustomAgent("user1");
            t._customAgentRegistry.Remove("user1", def.Id);
            return def.Id;
        }), null],
        ["custom agent owned by a different user", "user2", (Func<MultiAgentFactoryTests, string>)(t => t.AddCustomAgent("user1").Id), null]
    ];

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public void Create_RejectsInvalidAgentId_Throws(string _, string userId, Func<MultiAgentFactoryTests, string> agentIdFactory, string? expectedMessageFragment)
    {
        var agentId = agentIdFactory(this);
        var agentKey = new AgentKey(ConversationId: "1:1", AgentId: "test");

        var ex = Should.Throw<InvalidOperationException>(
            () => _sut.Create(agentKey, userId, agentId, _approvalHandler.Object));

        ex.Message.ShouldContain(expectedMessageFragment ?? agentId);
    }

    // ProviderRoutingResolverTests pins the resolution rules themselves, but not the argument
    // mapping at the real OpenRouterChatClient ctor call -- the one hop a null slip would sever
    // in production only, invisibly to a test that stops at the resolver. This drives the real
    // construction end-to-end onto a captured request.
    [Fact]
    public async Task CreateChatClient_ResolvedRouting_StampsRoutingAndSessionOntoTheRequest()
    {
        var handler = new CapturingSseHandler();

        using var client = _sut.CreateChatClient(
            "test-model",
            sessionId: "session-1",
            providerRouting: new ProviderRouting { Sort = ProviderSort.Latency },
            transportHandler: handler);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")]);

        var body = System.Text.Json.Nodes.JsonNode.Parse(handler.CapturedBody!)!.AsObject();
        body["provider"]!["sort"]!.GetValue<string>().ShouldBe("latency");
        body["session_id"]!.GetValue<string>().ShouldBe("session-1");
    }
}