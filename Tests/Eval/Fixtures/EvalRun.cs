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
            userId: "eval-user",
            agentId: scenario.AgentId,
            approvalHandler: new AutoApproveHandler());

        // Taken after the stack is up and before the turn goes out, so what the diff reports is
        // what this turn did rather than what arming the scenario did.
        recording.StateBefore = stack.Home.Snapshot();
        recording.FilesBefore = EvalVault.Read(stack.VaultPath);

        using var cancellation = new CancellationTokenSource(_budget);
        var thread = await agent.CreateSessionAsync(cancellation.Token);

        var response = await agent.RunAsync(
            [Turn(scenario, conversationId)], thread, cancellationToken: cancellation.Token);

        recording.Reply = response.Text;
        recording.StateAfter = stack.Home.Snapshot();
        recording.FilesAfter = EvalVault.Read(stack.VaultPath);
        recording.Delegations = stack.Workers.Delegations;
        return recording;
    }

    // Everything a channel puts on a turn, set the way a channel sets it. The decoration itself
    // happens where it happens in production — on the way out of the chat client — so what this
    // builds is the message, never the prefix.
    public static ChatMessage Turn(Scenario scenario, string conversationId)
    {
        var message = new ChatMessage(ChatRole.User, scenario.Turn.Text);
        message.SetSenderId(scenario.Turn.Sender);
        message.SetLocation(scenario.Turn.Room);
        message.SetSatelliteId(scenario.Turn.SatelliteId);
        message.SetDismissedAlert(scenario.Turn.DismissedAlert);
        message.SetTimestamp(scenario.Instant);
        message.SetConversationContext(new ConversationContext(
            scenario.AgentId, conversationId, "eval-user",
            new ReplyTarget(scenario.Turn.SatelliteId is null ? "signalr" : "voice", conversationId)));

        return message;
    }

    // What the dump shows: the same function the client applies, run against the same message and
    // the same zone, so what a diagnosis reads is what the model read.
    public static string Decorated(Scenario scenario) =>
        TurnDecoration
            .Apply(Turn(scenario, "eval:dump"), TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid"))
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