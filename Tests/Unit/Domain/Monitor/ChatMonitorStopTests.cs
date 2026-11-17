using Domain.DTOs;
using Domain.DTOs.Channel;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

// A stop ends the conversation's group; what is sent after it is a new turn of a new group. The chat hub sends
// a person's next message right behind the stop's cancel (a send that opened a new stream reaches the agent
// after the cancel), so the group may have that message routed to it before it hears the cancel: it is
// answered all the same, by the group after it, never dropped with the group, which would leave the hub
// waiting on a reply that never comes.
public class ChatMonitorStopTests
{
    [Fact]
    public async Task AMessageRoutedToTheGroupBehindItsStop_IsAnsweredByTheNextGroup()
    {
        var lookUp = LocalTool.Held("look_up", "42");
        var model = new ScriptedModel([lookUp.Call("call-1")], [new TextContent("Something else, then")]);
        var pumpHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePump = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var run = new ToolLoopRun(model, [lookUp], holdReply: async reply =>
        {
            if (reply is { TurnKey: "held", IsComplete: true })
            {
                pumpHeld.TrySetResult();
                await releasePump.Task.WaitAsync(TimeSpan.FromSeconds(60));
            }
        });

        run.Send("look it up");
        await lookUp.Started;
        // Refused as it arrives, and the refusal's end held: the group takes nothing more until it is let go.
        run.Channel.WriteUnsigned(Unsigned(ToolLoopRun.ConversationId, "held"));
        await pumpHeld.Task.WaitAsync(TimeSpan.FromSeconds(60));
        run.Stop(returned: []);
        run.Send("something else");
        // Routed in the order they came, so once another conversation's message behind them was refused, the
        // stop and the send behind it are in this group, which has not heard the stop yet.
        run.Channel.WriteUnsigned(Unsigned("conv-2", "marker"));
        await Eventually.Until(
            () => run.Channel.SentReplies.Any(r => r is { TurnKey: "marker", IsComplete: true }), "the marker to be routed");
        releasePump.SetResult();

        await Eventually.Until(
            () => run.StoredLine().Contains("assistant: Something else, then"), "the send behind the stop to be answered");
        run.StoredLine().TakeLast(2).ShouldBe(["user: something else", "assistant: Something else, then"]);
        model.Requests.Count.ShouldBe(2);
    }

    // The stopped turn is kept as it ends, which may be after the person's next message has reached the agent:
    // the next message's group waits for the stopped one to end before it reads the conversation, so the line
    // keeps the stopped turn where the page drew it, before the next one, and never refuses it as a line moved
    // under it (a conversation with history keeps a stopped turn only on the line it read).
    [Fact]
    public async Task TheNextMessagesTurn_WaitsForTheStoppedTurnToBeKept()
    {
        var lookUp = LocalTool.HeldPastAStop("look_up", "42");
        var model = new ScriptedModel(
            [new TextContent("Hello")], [lookUp.Call("call-1")], [new TextContent("Something else, then")]);
        await using var run = new ToolLoopRun(model, [lookUp]);
        run.Send("hi");
        await Eventually.Until(() => run.StoredLine().Count == 2, "the first turn to be kept");

        run.Send("look it up");
        await lookUp.Started;
        run.Stop(returned: []);
        await run.CancelHeardAsync();
        run.Send("something else");
        await Eventually.Settle();
        model.Requests.Count.ShouldBe(2);

        lookUp.Finish();
        await Eventually.Until(
            () => run.StoredLine().Contains("assistant: Something else, then"), "the next message to be answered");
        run.StoredLine().ShouldBe([
            "user: hi", "assistant: Hello",
            "user: look it up", "assistant: call look_up#call-1", "tool: result call-1 = 42",
            "user: something else", "assistant: Something else, then"
        ]);
    }

    // Stopped again while the next message's group still waits for the first stop's: that group keeps the next
    // message as stopped too, but only once the first has kept its cut turn, so the line has both in the order
    // the page drew them, and the first is never refused for a line moved under it.
    [Fact]
    public async Task AStopWhileTheNextGroupWaitsForTheLast_KeepsBothStoppedTurns_InOrder()
    {
        var lookUp = LocalTool.HeldPastAStop("look_up", "42");
        var model = new ScriptedModel([new TextContent("Hello")], [lookUp.Call("call-1")], [new TextContent("never")]);
        await using var run = new ToolLoopRun(model, [lookUp]);
        run.Send("hi");
        await Eventually.Until(() => run.StoredLine().Count == 2, "the first turn to be kept");

        run.Send("look it up");
        await lookUp.Started;
        run.Stop(returned: []);
        await run.CancelHeardAsync();
        run.Send("something else");
        await run.GroupOpenAsync();
        run.Stop(returned: []);
        await run.CancelHeardAsync();
        await Eventually.Settle();
        lookUp.Finish();
        await run.FinishAsync();

        run.StoredLine().ShouldBe([
            "user: hi", "assistant: Hello",
            "user: look it up", "assistant: call look_up#call-1", "tool: result call-1 = 42",
            "user: something else"
        ]);
        model.Requests.Count.ShouldBe(2);
    }

    private static ChannelMessage Unsigned(string conversationId, string turnKey) => new()
    {
        ConversationId = conversationId,
        Content = "unsigned",
        Sender = "nobody",
        ChannelId = "signalr",
        AgentId = ToolLoopRun.AgentId,
        TurnKey = turnKey
    };
}