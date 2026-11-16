using Domain.DTOs;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

// Steer now, as the agent takes it: a person's steer is a turn of its own, and it also ends their running
// turn at its next tool boundary — once the calls the model made have their results, before the model
// is asked again — so the steer is answered with everything done so far in view.
public class ChatMonitorSteeringTests
{
    [Fact]
    public async Task ASteerDuringAToolCall_EndsTheTurnAfterThatCallsResult_AndIsAnsweredNext()
    {
        var lookUp = LocalTool.Held("look_up", "42");
        var model = new ScriptedModel([lookUp.Call("call-1")], [new TextContent("The other one is 7")]);
        await using var run = new ToolLoopRun(model, [lookUp]);

        run.Send("look it up");
        await lookUp.Started;
        run.Send("no, the other one", steer: true);
        await run.AdmittedSoFarAsync();
        lookUp.Finish();
        await run.FinishAsync();

        run.StoredLine().ShouldBe([
            "user: look it up",
            "assistant: call look_up#call-1",
            "tool: result call-1 = 42",
            "user: no, the other one",
            "assistant: The other one is 7"
        ]);
        model.Requests.Count.ShouldBe(2);
        ToolLoopRun.Render(model.Requests[1]).TakeLast(2)
            .ShouldBe(["tool: result call-1 = 42", "user: no, the other one"]);
    }

    // The calls of one message run together, so the steer lands while some are done and some are not:
    // the turn still ends only once every one of them has its result, or the next request is refused.
    [Fact]
    public async Task ASteerDuringConcurrentCalls_LeavesEveryCallItsResult()
    {
        var slow = LocalTool.Held("slow", "slow done");
        var quick = LocalTool.Answering("quick", "quick done");
        var model = new ScriptedModel(
            [quick.Call("call-quick"), slow.Call("call-slow")], [new TextContent("Steered")]);
        await using var run = new ToolLoopRun(model, [slow, quick]);

        run.Send("both");
        await slow.Started;
        run.Send("stop there", steer: true);
        await run.AdmittedSoFarAsync();
        slow.Finish();
        await run.FinishAsync();

        run.StoredLine().ShouldBe([
            "user: both",
            "assistant: call quick#call-quick",
            "assistant: call slow#call-slow",
            "tool: result call-quick = quick done",
            "tool: result call-slow = slow done",
            "user: stop there",
            "assistant: Steered"
        ]);
        model.Requests.Count.ShouldBe(2);
    }
}