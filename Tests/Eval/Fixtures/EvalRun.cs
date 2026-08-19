using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using Tests.Eval.Harness;

namespace Tests.Eval.Fixtures;

// One run of one scenario: a fresh stack, one turn, one recording. Built per run rather than
// shared, because a scenario's subject is what the agent does with an empty conversation and a
// pinned clock, and a stack carried over from the previous run has neither.
public static class EvalRun
{
    // The user every scenario runs as. Memory is scoped to it, and the forget tool refuses a run
    // that carries no identity — so it is one constant rather than a string repeated per fixture.
    public const string UserId = "eval-user";

    // A stalled provider costs the run rather than the suite. The budget is generous: a voice turn
    // that reads a prompt, calls a tool and answers takes seconds, and a run cut off at its own
    // deadline would be reported as a behavioural failure it is not.
    private static readonly TimeSpan _budget = TimeSpan.FromSeconds(180);

    public static async Task<Recording> ExecuteAsync(Scenario scenario, string redisConnectionString)
    {
        var recording = new Recording();
        await using var stack = await EvalStack.StartAsync(
            scenario, redisConnectionString, recording);

        var conversationId = $"eval:{Guid.NewGuid():N}";
        await using var agent = stack.Factory.Create(
            new AgentKey(conversationId, scenario.AgentId),
            userId: UserId,
            agentId: scenario.AgentId,
            approvalHandler: new AutoApproveHandler());

        // Taken after the stack is up and before the turn goes out, so what the diff reports is
        // what this turn did rather than what arming the scenario did.
        recording.StateBefore = stack.Home.Snapshot();
        recording.FilesBefore = EvalVault.Read(stack.VaultPath);

        using var cancellation = new CancellationTokenSource(_budget);
        var thread = await agent.CreateSessionAsync(cancellation.Token);

        var response = await agent.RunAsync(
            Messages(scenario, conversationId, stack.Memory.Context), thread,
            cancellationToken: cancellation.Token);

        recording.Reply = response.Text;
        recording.StateAfter = stack.Home.Snapshot();
        recording.FilesAfter = EvalVault.Read(stack.VaultPath);
        recording.Delegations = stack.Workers.Delegations;
        recording.MemoriesAfter = stack.Memory.Remaining;
        return recording;
    }

    // The whole run's input: the scripted history, oldest first and five minutes apart, then the
    // turn itself. History rides as ordinary prior messages on the turn's own channel — a user
    // message decorated the way the channel decorates one, and the assistant's answer verbatim —
    // so what a multi-turn scenario tests is a conversation the model actually sees.
    public static IReadOnlyList<ChatMessage> Messages(
        Scenario scenario, string conversationId, MemoryContext? memories = null) =>
    [
        .. scenario.History.SelectMany<HistoryExchange, ChatMessage>((exchange, index) =>
        [
            Decorate(new ChatMessage(ChatRole.User, exchange.User), scenario,
                scenario.Instant.AddMinutes(-5 * (scenario.History.Count - index))),
            new ChatMessage(ChatRole.Assistant, exchange.Assistant)
        ]),
        Turn(scenario, conversationId, memories)
    ];

    // Everything a channel puts on a turn, set the way a channel sets it. The decoration itself
    // happens where it happens in production — on the way out of the chat client — so what this
    // builds is the message, never the prefix.
    public static ChatMessage Turn(
        Scenario scenario, string conversationId, MemoryContext? memories = null)
    {
        var message = Decorate(
            new ChatMessage(ChatRole.User, scenario.Turn.Text), scenario, scenario.Instant);
        message.SetDismissedAlert(scenario.Turn.DismissedAlert);
        // Set on the message the recall hook would have set it on, so the block reaches the model
        // through the decoration that renders it in production rather than through a second path.
        message.SetMemoryContext(memories);
        message.SetConversationContext(new ConversationContext(
            scenario.AgentId, conversationId, UserId,
            new ReplyTarget(scenario.Turn.SatelliteId is null ? "signalr" : "voice", conversationId)));

        return message;
    }

    private static ChatMessage Decorate(ChatMessage message, Scenario scenario, DateTimeOffset at)
    {
        message.SetSenderId(scenario.Turn.Sender);
        message.SetLocation(scenario.Turn.Room);
        message.SetSatelliteId(scenario.Turn.SatelliteId);
        message.SetTimestamp(at);
        return message;
    }

    // What the dump shows: the same function the client applies, run against the same message and
    // the same zone, so what a diagnosis reads is what the model read.
    public static string Decorated(Scenario scenario) =>
        TurnDecoration
            .Apply(
                Turn(scenario, "eval:dump", new EvalMemory(scenario.Remembered).Context),
                TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid"))
            .Text;
}

// Nothing in an eval is approved by a person, and the whitelist a scenario runs under is the
// shipped one — so what this stands in for is the channel that would have asked, never the rule
// that decides whether asking is needed.
public sealed class AutoApproveHandler : IToolApprovalHandler
{
    public Task<ToolApprovalResult> RequestApprovalAsync(
        string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct) =>
        Task.FromResult(ToolApprovalResult.Approved);

    public Task NotifyAutoApprovedAsync(
        string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct) =>
        Task.CompletedTask;
}