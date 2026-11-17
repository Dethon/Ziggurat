using Domain.Agents;
using Domain.DTOs;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

// The conversation group over the agent's real tool loop: a person's message reaches the approval
// client the deployment builds, a scripted model calls a local tool through it, and what the person
// is sent and what the conversation keeps are read where a channel and the store would read them.
public class ChatMonitorToolLoopTests
{
    [Fact]
    public async Task APersonsMessage_RunsTheToolTheModelCalls_AndKeepsTheWholeTurn()
    {
        var lookUp = LocalTool.Answering("look_up", "42");
        var model = new ScriptedModel([lookUp.Call("call-1")], [new TextContent("It is 42")]);
        await using var run = new ToolLoopRun(model, [lookUp]);

        run.Send("what is it?");
        await run.FinishAsync();

        run.Replies.Select(r => (r.ContentType, r.Content))
            .ShouldBe([(ReplyContentType.Text, "It is 42"), (ReplyContentType.StreamComplete, "")]);
        run.StoredLine().ShouldBe([
            "user: what is it?",
            "assistant: call look_up#call-1",
            "tool: result call-1 = 42",
            "assistant: It is 42"
        ]);
        model.Requests.Count.ShouldBe(2);
        ToolLoopRun.Render(model.Requests[1]).ShouldContain("tool: result call-1 = 42");
        run.Channel.NotifyAutoApprovedCalls.SelectMany(n => n.Requests).Select(r => r.ToolName).ShouldBe(["look_up"]);
    }

    // Steering ends a turn at a tool boundary through FunctionInvocationContext.Terminate, the flag a
    // turned-down approval sets. With concurrency on — the approval client's setting — the library runs
    // every call of the batch to its end before it looks at the flag, wherever in the batch the call that
    // set it was written, so each call keeps its result and the model is not asked again.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ACallEndingTheLoop_InAConcurrentBatch_LeavesEveryCallItsResult_AndAsksTheModelNoMore(int endingAt)
    {
        var slow = LocalTool.Held("slow", "slow done");
        var quick = LocalTool.Answering("quick", "quick done");
        var refused = LocalTool.Answering("refused", "never run");
        List<FunctionCallContent> batch = [slow.Call("call-slow"), quick.Call("call-quick")];
        batch.Insert(endingAt, refused.Call("call-refused"));
        var model = new ScriptedModel([.. batch], [new TextContent("never said")]);
        await using var run = new ToolLoopRun(model, [slow, quick], refused: [refused]);

        run.Send("go");
        await slow.Started;
        await Eventually.Until(() => run.Channel.AskedApprovals.Count == 1, "the refused call to be asked about");
        await Eventually.Settle();
        run.Replies.ShouldBeEmpty("the turn waits for the call still in flight");
        slow.Finish();
        await run.FinishAsync();

        run.StoredLine().ShouldBe([
            "user: go",
            .. batch.Select(c => $"assistant: call {c.Name}#{c.CallId}"),
            .. batch.Select(c => $"tool: result {c.CallId} = {Result(c.CallId)}")
        ]);
        model.Requests.Count.ShouldBe(1);
        run.Replies.Select(r => r.ContentType).ShouldBe([ReplyContentType.StreamComplete]);
    }

    // With concurrency off the library stops at the call that set the flag: the calls written after it
    // never run and get no result, though the model's message that wrote them is kept whole. A line like
    // that is one no provider takes back (a call without its result is a 400), so anything ending a loop
    // must not rely on this path.
    [Fact]
    public async Task ACallEndingTheLoop_InASequentialBatch_LeavesTheCallsAfterItWithoutAResult()
    {
        var quick = LocalTool.Answering("quick", "quick done");
        var refused = LocalTool.Answering("refused", "never run");
        var slow = LocalTool.Answering("slow", "slow done");
        var model = new ScriptedModel(
            [quick.Call("call-quick"), refused.Call("call-refused"), slow.Call("call-slow")],
            [new TextContent("never said")]);
        await using var run = new ToolLoopRun(model, [quick, slow], refused: [refused], sequential: true);

        run.Send("go");
        await run.FinishAsync();

        run.StoredLine().ShouldBe([
            "user: go",
            "assistant: call quick#call-quick",
            "assistant: call refused#call-refused",
            "assistant: call slow#call-slow",
            "tool: result call-quick = quick done",
            $"tool: result call-refused = {ApprovalRejection.ResultFor("refused")}"
        ]);
        slow.HasStarted.ShouldBeFalse();
        model.Requests.Count.ShouldBe(1);
        run.Replies.Select(r => r.ContentType).ShouldBe([ReplyContentType.StreamComplete]);
    }

    private static string Result(string callId) => callId switch
    {
        "call-slow" => "slow done",
        "call-quick" => "quick done",
        _ => ApprovalRejection.ResultFor("refused")
    };
}