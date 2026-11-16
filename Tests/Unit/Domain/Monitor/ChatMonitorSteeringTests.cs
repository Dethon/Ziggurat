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

    // A reply that never reaches a tool call has no boundary to stop at: it is said whole, and the steer
    // is simply the next turn.
    [Fact]
    public async Task ASteerDuringATextOnlyReply_LeavesItWhole_AndRunsNext()
    {
        var model = new ScriptedModel([new TextContent("The whole answer")], [new TextContent("And the steer's")]);
        var release = model.Hold(0);
        await using var run = new ToolLoopRun(model, []);

        run.Send("tell me");
        await Eventually.Until(() => model.Requests.Count == 1, "the reply to be under way");
        run.Send("shorter", steer: true);
        await run.AdmittedSoFarAsync();
        release();
        await run.FinishAsync();

        run.StoredLine().ShouldBe([
            "user: tell me",
            "assistant: The whole answer",
            "user: shorter",
            "assistant: And the steer's"
        ]);
    }

    // Only the running turn's own person steers it: another member's steer, or a steer of the person
    // whose schedule is firing, waits for the turn to end and is then answered as the next turn.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASteerFromAnotherAccount_OrDuringAFire_RunsNextWithoutCutting(bool duringAFire)
    {
        var lookUp = LocalTool.Held("look_up", "42");
        var model = new ScriptedModel(
            [lookUp.Call("call-1")], [new TextContent("It is 42")], [new TextContent("Steered")]);
        await using var run = new ToolLoopRun(model, [lookUp]);

        if (duringAFire)
        {
            run.Fire("look it up");
        }
        else
        {
            run.Send("look it up");
        }

        await lookUp.Started;
        run.Send("the other one", steer: true, from: duringAFire ? ToolLoopRun.Person : ToolLoopRun.OtherPerson);
        await run.AdmittedSoFarAsync();
        lookUp.Finish();
        await run.FinishAsync();

        run.StoredLine().Skip(1).ShouldBe([
            "assistant: call look_up#call-1",
            "tool: result call-1 = 42",
            "assistant: It is 42",
            "user: the other one",
            "assistant: Steered"
        ]);
    }

    // A worker runs inside its parent's tool call on options of its own, so the parent's steer never
    // cuts the worker's loop: the worker finishes its task, and the parent's turn ends once the call
    // that ran it returns.
    [Fact]
    public async Task AParentsSteer_LetsItsWorkerFinish_AndCutsTheParentAfterTheWorkersCall()
    {
        var dig = LocalTool.Held("dig", "dug");
        var workerModel = new ScriptedModel([dig.Call("dig-1")], [new TextContent("The hole is dug")]);
        var worker = new ToolLoopRun.Worker(workerModel, [dig]);
        var model = new ScriptedModel([worker.Call("call-1", "dig a hole")], [new TextContent("Steered")]);
        await using var run = new ToolLoopRun(model, [], worker: worker);

        run.Send("get it dug");
        await dig.Started;
        run.Send("never mind", steer: true);
        await run.AdmittedSoFarAsync();
        dig.Finish();
        await run.FinishAsync();

        workerModel.Requests.Count.ShouldBe(2);
        var line = run.StoredLine();
        line.Take(2).ShouldBe(["user: get it dug", $"assistant: call {worker.Call("call-1", "").Name}#call-1"]);
        line[2].ShouldStartWith("tool: result call-1 = ");
        line[2].ShouldContain("The hole is dug");
        line.Skip(3).ShouldBe(["user: never mind", "assistant: Steered"]);
        model.Requests.Count.ShouldBe(2);
    }
}