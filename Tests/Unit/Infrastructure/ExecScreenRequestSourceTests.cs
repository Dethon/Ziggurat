using System.Text.Json;
using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Judgments;
using Domain.Monitor;
using Domain.Tools.FileSystem;
using Domain.Tools.SubAgents;
using Infrastructure.Agents.ChatClients;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;
using Tests.Unit.Domain;
using Tests.Unit.Domain.Skills;
using Tests.Unit.Infrastructure.Helpers;
using static Tests.Unit.Infrastructure.Helpers.ToolApprovalResponseFactory;

namespace Tests.Unit.Infrastructure;

// Where the screen's `request` comes from on the two turns no person typed. The screen reads the
// user messages of the conversation its chat client is handed, so what it judges against is
// whatever the path that starts the run puts there: a worker's delegation prompt, a schedule's
// prompt. Each test drives the real path that builds the run's messages, then screens an exec
// on them through the real screen, and reads what the judge was shown.
public class ExecScreenRequestSourceTests
{
    private const string Exec = "domain__filesystem__exec";

    // A worker runs on a fresh context: the parent's words are not in it, only the task the parent
    // wrote for it. That task is the request — and the turn it was delegated from still decides the
    // model, so a delegation out of a local-box turn sends the judge nothing about itself.
    [Fact]
    public async Task ForASubagent_TheRequestIsItsDelegationPrompt()
    {
        var worker = new RecordingAgent();
        var parent = new ConversationContext(
            "jonas", "conv-7", "fran", new ReplyTarget("signalr", "conv-7"), ConfigPatchModel: "z-ai/glm-5");
        var tool = new SubAgentRunTool(
            new SubAgentRegistryOptions
            {
                SubAgents = [new SubAgentDefinition { Id = "worker", Name = "Worker", Model = "m", McpServerEndpoints = [] }]
            },
            new FeatureConfig(SubAgentFactory: _ => worker, UserId: "fran", ConversationContextProvider: () => parent));

        await tool.RunAsync("worker", "Descarga https://example.org/data.csv y cuenta sus filas.");

        var asked = await ScreenedOn(worker.Received.ShouldNotBeNull());

        Requests(asked).ShouldBe(["Descarga https://example.org/data.csv y cuenta sus filas."]);
        asked.Caller.TurnModel.ShouldBe("z-ai/glm-5");
        asked.Caller.Sender.ShouldBe("fran");
    }

    // A scheduled fire mints a conversation of its own each time, so nothing a person said earlier
    // is in it: the schedule's prompt is the whole request.
    [Fact]
    public async Task ForAScheduledFire_TheRequestIsTheSchedulesPrompt()
    {
        var agent = new FakeAiAgent();
        var fire = new ChannelMessage
        {
            ConversationId = "fire-1",
            Content = "Haz copia de seguridad de mis notas en el sandbox.",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "jonas",
            Origin = new MessageOrigin(MessageOriginKind.Schedule, "nightly-backup")
        };
        var monitor = new ChatMonitor(
            [MonitorTestMocks.CreateChannel("scheduling", fire)],
            MonitorTestMocks.CreateAgentFactory(agent),
            MonitorTestMocks.CreateThreadResolver(),
            new RecordingMetricsPublisher(),
            null,
            new Mock<ILogger<ChatMonitor>>().Object);

        await monitor.Monitor(CancellationToken.None);

        agent.ReceivedMessages.TryDequeue(out var received).ShouldBeTrue();
        var asked = await ScreenedOn(received!);

        Requests(asked).ShouldBe(["Haz copia de seguridad de mis notas en el sandbox."]);
    }

    // The run's messages, as the agent handed them to its chat client, with one whitelisted exec
    // on the person's machine called on them — screened by the real screen over a judge that
    // records what it was asked.
    private static async Task<JudgmentRequest> ScreenedOn(IReadOnlyList<ChatMessage> messages)
    {
        var judge = new ScriptedJudge(_ => new JudgmentOutcome.Absent(AbsenceReason.Error));
        var inner = new FakeChatClient();
        inner.SetNextResponse(CreateToolCallResponse(
            Exec, "c1", new Dictionary<string, object?> { ["path"] = "/laptop", ["command"] = "ls" }));
        var client = new ToolApprovalChatClient(
            inner, new TestApprovalHandler(ToolApprovalResult.Approved), "conv-7", ["domain__filesystem__*"],
            execScreen: new ExecScreen(judge, new ExecScreenSettings(), new FakeTimeProvider()));
        var exec = ExecReach.Carried(
            AIFunctionFactory.Create((string path, string command) => "ok", Exec),
            new ExecReach((_, _) => ShellReach.Host));

        await client.GetResponseAsync(messages, new ChatOptions { Tools = [exec] });

        return judge.Asked.ShouldHaveSingleItem();
    }

    // What the judge was shown of the person, oldest first: the context, then the request itself.
    private static IReadOnlyList<string> Requests(JudgmentRequest asked) =>
    [
        .. asked.State["earlier_messages"]!.AsArray().Select(node => node!.GetValue<string>()),
        asked.State["request"]!.GetValue<string>()
    ];
}

file sealed class RecordingSession : AgentSession;

// A worker that records the messages it was run with and answers at once.
file sealed class RecordingAgent : DisposableAgent
{
    public IReadOnlyList<ChatMessage>? Received { get; private set; }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public override ValueTask DisposeThreadSessionAsync(AgentSession thread) => ValueTask.CompletedTask;

    protected override Task<AgentResponse> RunCoreAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, CancellationToken cancellationToken = default)
    {
        Received = [.. messages];
        return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, "hecho")));
    }

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages, AgentSession? session = null,
        AgentRunOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<AgentSession>(new RecordingSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
        AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(JsonDocument.Parse("{}").RootElement);

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
        JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<AgentSession>(new RecordingSession());
}