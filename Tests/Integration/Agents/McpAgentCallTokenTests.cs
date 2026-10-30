using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Domain.Security;
using Domain.Security.Signing;
using Domain.Tools.SubAgents;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Agents.Mcp;
using Infrastructure.Metrics;
using Mcp.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using Shouldly;
using Tests.Integration.McpServers;
using Tests.Unit.Infrastructure.Helpers;

namespace Tests.Integration.Agents;

// The agent's side of the call token, through a real agent turn against a real gated server: every
// call the turn makes carries a token the server admits, for the person the turn acts for — however
// long the turn runs, and from a worker the turn spawned as much as from the turn itself.
public class McpAgentCallTokenTests
{
    private const string Alice = "a11ce0000000000000000000000000a1";

    private sealed record GatedSettings(McpGateSettings Mcp) : IMcpHostSettings;

    private static readonly ConversationContext _alicesTurn = new(
        "jonas", "conv-alice", "Alice", new ReplyTarget(ChannelIds.SignalR, "conv-alice"),
        Principal: new Principal(ChannelIds.SignalR, Alice, new Account("home", Alice)));

    // A turn outlives its first token: the model thinks for minutes between calls. Each call still
    // presents a token the server admits, minted afresh once the last one neared its expiry, and the
    // calls between two refreshes share one token rather than paying a signature each.
    [Fact]
    public async Task ATurnRunningPastTheFirstTokensLifetime_KeepsPresentingValidTokens()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));
        await using var server = await StartAsync(clock);
        var record = RecordTool(server);
        var model = new ScriptedChatClient(
            Call(record, "1"),
            Call(record, "2"),
            Call(record, "3", before: () => clock.Advance(TimeSpan.FromSeconds(100))),
            Call(record, "4", before: () => clock.Advance(TimeSpan.FromMinutes(5))),
            Answer("Done"));
        await using var agent = Agent(model, server.Endpoint, new CallTokens(TestSigningKeys.Store(TestSigningKeys.Agent), clock));

        await agent.RunAsync([Message(_alicesTurn)]);

        var calls = Log(server).Calls;
        calls.Count.ShouldBe(4);
        calls.ShouldAllBe(c => c.Subject == Alice && c.ExpiresAt > c.At);
        calls[1].TokenId.ShouldBe(calls[0].TokenId, "two calls a moment apart present one token");
        calls[2].TokenId.ShouldNotBe(calls[1].TokenId, "a token within its margin of expiry is not presented again");
        calls[3].At.ShouldBeGreaterThan(calls[0].ExpiresAt, "the turn ran past the first token's lifetime");
        calls[3].TokenId.ShouldNotBe(calls[2].TokenId);
    }

    // A worker acts on its parent's behalf, so its calls are signed for the parent turn's person: the
    // server it calls acts for that person, in the parent's conversation.
    [Fact]
    public async Task AWorkersCalls_ActForItsParentTurnsPerson()
    {
        await using var server = await StartAsync(TimeProvider.System);
        var worker = Agent(
            new ScriptedChatClient(Call(RecordTool(server), "w1"), Answer("Worked")),
            server.Endpoint, McpTestSecret.CallTokens);
        var spawn = new SubAgentToolFeature(new SubAgentRegistryOptions
        {
            SubAgents = [new SubAgentDefinition { Id = "worker", Name = "Worker", McpServerEndpoints = [] }]
        }).GetTools(new FeatureConfig(
            SubAgentFactory: _ => worker,
            ConversationContextProvider: () => ConversationContextMeta.Current)).Single();
        await using var parent = Agent(
            new ScriptedChatClient(
                ToolApprovalResponseFactory.CreateToolCallResponse(spawn.Name, "p1",
                    new Dictionary<string, object?> { ["subAgentId"] = "worker", ["prompt"] = "go" }),
                Answer("Done")),
            endpoint: null, McpTestSecret.CallTokens, [spawn]);

        await parent.RunAsync([Message(_alicesTurn)]);

        var call = Log(server).Calls.ShouldHaveSingleItem();
        call.Subject.ShouldBe(Alice);
        call.Conversation.ShouldBe("conv-alice");
    }

    private static Task<RunningServer> StartAsync(TimeProvider clock) =>
        InMemoryMcpServer.StartAsync(services => services
            .AddSingleton(clock)
            .AddKeyedSingleton(CallTokenGate.ClockKey, clock)
            .AddSingleton<CallLog>()
            .AddToolServer(new GatedSettings(McpTestSecret.Gate))
            .WithTools<RecordingTools>());

    private static CallLog Log(RunningServer server) => server.App.Services.GetRequiredService<CallLog>();

    // As the agent names a server's tool: its host and port, then the tool.
    private static string RecordTool(RunningServer server) =>
        $"mcp__localhost-{new Uri(server.Endpoint).Port}__record";

    private static ChatMessage Message(ConversationContext turn)
    {
        var message = new ChatMessage(ChatRole.User, "go");
        message.SetConversationContext(turn);
        return message;
    }

    private static McpAgent Agent(
        IChatClient model, string? endpoint, CallTokens tokens, IReadOnlyList<AIFunction>? domainTools = null) =>
        new(
            TestAgentSpec.Default with
            {
                McpServerEndpoints = endpoint is null ? [] : [McpServerEndpoint.Configured(endpoint, McpTestSecret.Value)]
            },
            new ToolApprovalChatClient(model, new TestApprovalHandler(ToolApprovalResult.Approved), "conv-alice", ["*"]),
            new Mock<IThreadStateStore>().Object,
            NoOpMetricsPublisher.Instance,
            TimeProvider.System,
            domainTools ?? [],
            [],
            callTokens: tokens);

    private static Step Call(string tool, string id, Action? before = null) =>
        new(ToolApprovalResponseFactory.CreateToolCallResponse(tool, id, new Dictionary<string, object?>()), before);

    private static Step Answer(string text) =>
        new(new ChatResponse([new ChatMessage(ChatRole.Assistant, text)]) { FinishReason = ChatFinishReason.Stop });

    private sealed record Step(ChatResponse Response, Action? Before = null)
    {
        public static implicit operator Step(ChatResponse response) => new(response);
    }

    // The model, scripted: each round answers the next step, after doing whatever the step says
    // happens first — the minutes a model can spend thinking between two calls.
    private sealed class ScriptedChatClient(params Step[] steps) : IChatClient
    {
        private readonly Queue<Step> _steps = new(steps);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Next());

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Next().ToChatResponseUpdates().ToAsyncEnumerable();

        private ChatResponse Next()
        {
            var step = _steps.Dequeue();
            step.Before?.Invoke();
            return step.Response;
        }

        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }

    public sealed record LoggedCall(string Subject, string TokenId, DateTimeOffset ExpiresAt, DateTimeOffset At, string? Conversation);

    // What the server took each call to be, at the server's own time.
    public sealed class CallLog(TimeProvider clock)
    {
        private readonly Lock _lock = new();
        private readonly List<LoggedCall> _calls = [];

        public IReadOnlyList<LoggedCall> Calls
        {
            get
            {
                lock (_lock)
                {
                    return [.. _calls];
                }
            }
        }

        public void Record(IdentityClaims identity, ConversationContext? turn)
        {
            lock (_lock)
            {
                _calls.Add(new LoggedCall(identity.Subject, identity.Id, identity.ExpiresAt, clock.GetUtcNow(), turn?.ConversationId));
            }
        }
    }

    [McpServerToolType]
    public sealed class RecordingTools
    {
        [McpServerTool(Name = "record")]
        [Description("Records who the call acts for.")]
        public static string Record(RequestContext<CallToolRequestParams> context, CallLog log)
        {
            log.Record(context.Identity!, context.Turn);
            return "recorded";
        }
    }
}