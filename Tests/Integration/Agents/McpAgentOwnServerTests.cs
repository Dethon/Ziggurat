using System.ComponentModel;
using Domain.Agents;
using Domain.Contracts;
using Domain.Security;
using Infrastructure.Agents;
using Infrastructure.Metrics;
using Mcp.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Moq;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Integration.Agents;

// A household's own server being offline is the household's business, not a fault of the
// deployment: a turn goes without its tools and is told which servers are missing, and the next turn
// of the same conversation picks the server up once it is back — reconciled where the turn starts.
// The servers are real and hosted in process; which of them are online is what the stack's own
// IOwnServers answers, edited between turns.
public sealed class McpAgentOwnServerTests : IAsyncLifetime
{
    private const string Household = "4f1c9a7e2b6d4e0a9c3b8d5e7f1a2c4b";

    private readonly Dictionary<string, string> _online = [];
    private readonly List<(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options)> _requests = [];
    private RunningServer _vault = null!;
    private RunningServer _shared = null!;

    private sealed record GatedSettings : IMcpHostSettings
    {
        public McpGateSettings Mcp => McpTestSecret.Gate;
    }

    [McpServerToolType]
    public sealed class VaultTools
    {
        [McpServerTool(Name = "recall")]
        [Description("Reads a note back.")]
        public static string Recall(string note) => note;
    }

    public async Task InitializeAsync()
    {
        _vault = await InMemoryMcpServer.StartAsync(services => services
            .AddToolServer(new GatedSettings())
            .WithTools<VaultTools>());
        _shared = await InMemoryMcpServer.StartAsync(services => services
            .AddToolServer(new GatedSettings())
            .WithTools<Fixtures.TestEchoTool>());
    }

    public async Task DisposeAsync()
    {
        await _vault.DisposeAsync();
        await _shared.DisposeAsync();
    }

    [Fact]
    public async Task ATurnBuiltWhileAHouseholdServerIsOffline_OffersNoneOfItsToolsAndNamesIt()
    {
        await using var agent = Agent();
        var session = await agent.CreateSessionAsync();

        await TurnAsync(agent, session);

        ToolsOf(_requests.Single()).ShouldContain(name => name.EndsWith("__Echo"));
        ToolsOf(_requests.Single()).ShouldNotContain(name => name.EndsWith("__recall"));
        ContextOf(_requests.Single()).ShouldContain(TurnDecoration.OfflineLine(["vault"]));
    }

    [Fact]
    public async Task AServerThatComesOnline_IsOfferedOnTheNextTurnOfTheSameConversation()
    {
        await using var agent = Agent();
        var session = await agent.CreateSessionAsync();
        await TurnAsync(agent, session);

        _online["vault"] = _vault.Endpoint;
        await TurnAsync(agent, session);

        ToolsOf(_requests[1]).ShouldContain(name => name.EndsWith("__recall"));
        ToolsOf(_requests[1]).ShouldContain(name => name.EndsWith("__Echo"));
        ContextOf(_requests[1]).ShouldNotContain("Offline household servers");
    }

    [Fact]
    public async Task AServerThatGoesOffline_LeavesTheNextTurnOfTheSameConversation()
    {
        _online["vault"] = _vault.Endpoint;
        await using var agent = Agent();
        var session = await agent.CreateSessionAsync();
        await TurnAsync(agent, session);

        _online.Remove("vault");
        await TurnAsync(agent, session);

        ToolsOf(_requests[0]).ShouldContain(name => name.EndsWith("__recall"));
        ToolsOf(_requests[1]).ShouldNotContain(name => name.EndsWith("__recall"));
        ContextOf(_requests[1]).ShouldContain(TurnDecoration.OfflineLine(["vault"]));
    }

    private McpAgent Agent() => new(
        TestAgentSpec.Default with
        {
            TenantId = Household,
            McpServerEndpoints =
            [
                McpServerEndpoint.OwnServer("vault"),
                McpServerEndpoint.Configured(_shared.Endpoint, McpTestSecret.Value)
            ]
        },
        ChatClient(),
        new Mock<IThreadStateStore>().Object,
        NoOpMetricsPublisher.Instance,
        TimeProvider.System,
        [],
        [],
        callTokens: McpTestSecret.CallTokens,
        ownServers: new InProcessOwnServers(_online));

    private IChatClient ChatClient()
    {
        var client = new Mock<IChatClient>();
        client
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>(
                (messages, options, _) => _requests.Add(([.. messages], options)))
            .Returns(() => new List<ChatResponseUpdate>
            {
                new() { Role = ChatRole.Assistant, Contents = [new TextContent("ok")] }
            }.ToAsyncEnumerable());
        return client.Object;
    }

    private static Task TurnAsync(McpAgent agent, Microsoft.Agents.AI.AgentSession session) =>
        agent.RunStreamingAsync([new ChatMessage(ChatRole.User, "what is in my vault?")], session).ToListAsync().AsTask();

    private static IReadOnlyList<string> ToolsOf((IReadOnlyList<ChatMessage> Messages, ChatOptions? Options) request) =>
        [.. request.Options.ShouldNotBeNull().Tools.ShouldNotBeNull().Select(t => t.Name)];

    // What the model reads of the turn's own message: decorated as the chat client decorates it.
    private static string ContextOf((IReadOnlyList<ChatMessage> Messages, ChatOptions? Options) request) =>
        TurnDecoration.Apply(request.Messages.Last(m => m.Role == ChatRole.User), TimeZoneInfo.Utc).Text;
}