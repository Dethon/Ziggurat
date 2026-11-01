using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Domain.Prompts;
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
        _definitionProvider = CreateProvider(_customAgentRegistry, _builtInAgent);
        _sut = CreateFactory(_definitionProvider);
    }

    private static AgentDefinitionProvider CreateProvider(
        CustomAgentRegistry registry, params AgentDefinition[] agents)
    {
        return FixedHouseholdAgents.Provider(registry, agents);
    }

    private static MultiAgentFactory CreateFactory(IAgentDefinitionProvider definitionProvider)
    {
        var openRouterConfig = new OpenRouterConfig { ApiUrl = "http://test", ApiKey = "test-key" };

        var domainToolRegistry = new Mock<IDomainToolRegistry>();
        domainToolRegistry
            .Setup(r => r.GetToolsForFeatures(It.IsAny<IEnumerable<string>>(), It.IsAny<FeatureConfig>()))
            .Returns(Enumerable.Empty<AIFunction>());
        domainToolRegistry
            .Setup(r => r.GetPromptsForFeatures(It.IsAny<IEnumerable<string>>()))
            .Returns(Enumerable.Empty<PromptSection>());

        var stateStore = new Mock<IThreadStateStore>();

        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider
            .Setup(sp => sp.GetService(typeof(IThreadStateStore)))
            .Returns(stateStore.Object);

        return new MultiAgentFactory(
            serviceProvider.Object,
            definitionProvider,
            openRouterConfig,
            domainToolRegistry.Object,
            null);
    }

    private static Account AccountOf(string userId) => new("home", userId);

    private async Task<AgentDefinition> AddCustomAgent(string userId, string name = "TestBot", string model = "test-model")
    {
        var definition = new AgentDefinition
        {
            Id = $"custom-{Guid.NewGuid()}",
            Name = name,
            Model = model,
            McpServerEndpoints = []
        };
        await _customAgentRegistry.AddAsync(AccountOf(userId), definition);
        return definition;
    }

    public static IEnumerable<object[]> SuccessCases =>
    [
        ["built-in agent id", "user1", (Func<MultiAgentFactoryTests, Task<string?>>)(_ => Task.FromResult<string?>(_builtInAgent.Id))],
        ["custom agent id for owning user", "user1", (Func<MultiAgentFactoryTests, Task<string?>>)(async t => (await t.AddCustomAgent("user1")).Id)]
    ];

    [Theory]
    [MemberData(nameof(SuccessCases))]
    public async Task Create_SupportedAgentIdentifier_ReturnsAgent(string _, string userId, Func<MultiAgentFactoryTests, Task<string?>> agentIdFactory)
    {
        var agentId = await agentIdFactory(this);
        var agentKey = new AgentKey(ConversationId: "1:1", AgentId: "test");

        var agent = await _sut.CreateAsync(agentKey, userId, agentId, _approvalHandler.Object, AccountOf(userId));

        agent.ShouldNotBeNull();
    }

    public static IEnumerable<object?[]> ErrorCases =>
    [
        ["unknown agent id", "user1", (Func<MultiAgentFactoryTests, Task<string?>>)(_ => Task.FromResult<string?>("unknown-id")), "unknown-id"],
        // Never the first configured agent: the array is ordered for display, and picking from it
        // would answer a general request with whichever assistant happens to be listed first.
        ["no agent id at all", "user1", (Func<MultiAgentFactoryTests, Task<string?>>)(_ => Task.FromResult<string?>(null)), "No agent id"],
        ["custom agent unregistered before create", "user1", (Func<MultiAgentFactoryTests, Task<string?>>)(async t =>
        {
            var def = await t.AddCustomAgent("user1");
            await t._customAgentRegistry.RemoveAsync(AccountOf("user1"), def.Id);
            return def.Id;
        }), null],
        ["custom agent owned by a different user", "user2", (Func<MultiAgentFactoryTests, Task<string?>>)(async t => (await t.AddCustomAgent("user1")).Id), null]
    ];

    [Theory]
    [MemberData(nameof(ErrorCases))]
    public async Task Create_RejectsInvalidAgentId_Throws(string _, string userId, Func<MultiAgentFactoryTests, Task<string?>> agentIdFactory, string? expectedMessageFragment)
    {
        var agentId = await agentIdFactory(this);
        var agentKey = new AgentKey(ConversationId: "1:1", AgentId: "test");

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _sut.CreateAsync(agentKey, userId, agentId, _approvalHandler.Object, AccountOf(userId)));

        ex.Message.ShouldContain(expectedMessageFragment ?? agentId!);
    }

    // A person's custom agents are filed under their account, and a chat-client turn's sender is the
    // name the account goes by: the agent is found by the account the turn acts for, never by the name.
    [Fact]
    public async Task Create_ACustomAgentOfTheTurnsAccount_IsFoundWhateverTheSenderIsCalled()
    {
        var agentId = (await AddCustomAgent("acct-1")).Id;

        var agent = await _sut.CreateAsync(
            new AgentKey("1:1", agentId), "Fran", agentId, _approvalHandler.Object, new Account("home", "acct-1"));

        agent.ShouldNotBeNull();
    }

    [Fact]
    public async Task Create_ACustomAgentOfAnotherAccount_IsNotFound()
    {
        var agentId = (await AddCustomAgent("acct-1")).Id;

        await Should.ThrowAsync<InvalidOperationException>(() => _sut.CreateAsync(
            new AgentKey("1:1", agentId), "acct-1", agentId, _approvalHandler.Object, new Account("home", "acct-2")));
    }

    // Routing reads the id, never the position. Reordering the catalogue is a display decision and
    // has to leave every routing answer exactly where it was.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_AgentsReorderedInConfiguration_BuildsTheSameAgent(bool reversed)
    {
        AgentDefinition[] agents =
        [
            new() { Id = "jack", Name = "Jack", Model = "test-model", McpServerEndpoints = [] },
            new() { Id = "jonas", Name = "Jonas", Model = "test-model", McpServerEndpoints = [] }
        ];
        var provider = CreateProvider(
            new CustomAgentRegistry(), reversed ? [.. agents.Reverse()] : agents);
        var sut = CreateFactory(provider);

        var agent = await sut.CreateAsync(
            new AgentKey("1:1", "jonas"), "user1", "jonas", _approvalHandler.Object);

        // The display name is the definition's name and the conversation it was built for.
        agent.Name.ShouldBe("Jonas-1:1");
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

    // The prompt tells a household's agent its day in the household's zone; the time stamped on each
    // turn must be that zone's too, or the model is handed two clocks and answers by the turn's — a
    // household in New York was told the deployment's evening.
    [Fact]
    public async Task CreateChatClient_AHouseholdZone_StampsTheTurnsTimeInIt()
    {
        var handler = new CapturingSseHandler();
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        using var client = _sut.CreateChatClient("test-model", transportHandler: handler, turnZone: newYork);
        var message = new ChatMessage(ChatRole.User, "hi");
        message.SetTimestamp(new DateTimeOffset(2026, 11, 1, 22, 5, 0, TimeSpan.Zero));

        await client.GetResponseAsync([message]);

        handler.CapturedBody.ShouldContain("[Current time: 2026-11-01 17:05:00 -05:00]");
    }
}