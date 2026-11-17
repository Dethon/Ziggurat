using System.Collections.Concurrent;
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Microsoft.Agents.AI;
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

    // Two messages steered into the same reply go in together: the hub drew both where the cut turn ended
    // and says so in its answer to that end, and the agent runs them as one turn, in the order they were
    // steered, answered once under the first one's key.
    [Fact]
    public async Task TwoSteersWaitingWhenTheCutTurnEnds_GoInTogether_AndAreAnsweredOnce()
    {
        var lookUp = LocalTool.Held("look_up", "42");
        var model = new ScriptedModel([lookUp.Call("call-1")], [new TextContent("Both answered")]);
        await using var run = new ToolLoopRun(model, [lookUp], receipts: SteeredTogetherAtTheCut("steer-1", "steer-2"));

        run.Send("look it up");
        await lookUp.Started;
        run.Send("no, the other one", steer: true, turnKey: "steer-1");
        run.Send("and in red", steer: true, turnKey: "steer-2");
        await run.AdmittedSoFarAsync();
        lookUp.Finish();
        await run.FinishAsync();

        run.StoredLine().ShouldBe([
            "user: look it up",
            "assistant: call look_up#call-1",
            "tool: result call-1 = 42",
            "user: no, the other one",
            "user: and in red",
            "assistant: Both answered"
        ]);
        model.Requests.Count.ShouldBe(2);
        ToolLoopRun.Render(model.Requests[1]).TakeLast(2).ShouldBe(["user: no, the other one", "user: and in red"]);

        // Read back as a reload draws it: the steered messages one after another, each its own entry, then
        // the one answer.
        var shown = Transcript.Shown(run.Line());
        ToolLoopRun.Render(shown).Skip(2).ShouldBe(["user: no, the other one", "user: and in red", "assistant: Both answered"]);
        shown.Skip(2).Select(m => m.GetEntryId()).Distinct().Count().ShouldBe(3);
        // Answered under the first steer's key; the second's key only ends, once, after that answer, since
        // the hub counts every steer it sent as a turn until its key ends.
        var steered = run.Replies.Where(r => r.TurnKey is "steer-1" or "steer-2").ToList();
        steered.Count(r => r is { IsComplete: true, TurnKey: "steer-1" }).ShouldBe(1);
        steered.Where(r => r.TurnKey == "steer-2")
            .Select(r => (r.ContentType, r.IsComplete)).ShouldBe([(ReplyContentType.StreamComplete, true)]);
        steered.Last().TurnKey.ShouldBe("steer-2");
    }

    // The hub drew both steers as the cut turn ended, but the second was still on its way through the pump:
    // the turn waits for it rather than answer the first alone and the second after.
    [Fact]
    public async Task ASteerDrawnTogetherButNotYetAdmitted_IsWaitedFor_AndGoesInWithTheFirst()
    {
        var lookUp = LocalTool.Held("look_up", "42");
        var model = new ScriptedModel([lookUp.Call("call-1")], [new TextContent("Both answered")]);
        await using var run = new ToolLoopRun(model, [lookUp], receipts: SteeredTogetherAtTheCut("steer-1", "steer-2"));

        run.Send("look it up");
        await lookUp.Started;
        run.Send("no, the other one", steer: true, turnKey: "steer-1");
        await run.AdmittedSoFarAsync();
        lookUp.Finish();
        await Eventually.Until(
            () => run.Replies.Any(r => r is { IsComplete: true, TurnKey: not ("steer-1" or "steer-2") }), "the cut turn to end");
        await Eventually.Settle();
        model.Requests.Count.ShouldBe(1);

        run.Send("and in red", steer: true, turnKey: "steer-2");
        await run.FinishAsync();

        run.StoredLine().Skip(3).ShouldBe(["user: no, the other one", "user: and in red", "assistant: Both answered"]);
        model.Requests.Count.ShouldBe(2);
    }

    // A steer drawn together that the agent refused as it arrived (its member disabled meanwhile) is not
    // waited for: the others go in without it.
    [Fact]
    public async Task ASteerDrawnTogetherThatWasRefusedAsItArrived_IsNotWaitedFor()
    {
        var lookUp = LocalTool.Held("look_up", "42");
        var model = new ScriptedModel([lookUp.Call("call-1")], [new TextContent("First answered")]);
        await using var run = new ToolLoopRun(model, [lookUp], receipts: SteeredTogetherAtTheCut("steer-1", "steer-2"));

        run.Send("look it up");
        await lookUp.Started;
        run.Send("no, the other one", steer: true, turnKey: "steer-1");
        run.Channel.WriteUnsigned(new ChannelMessage
        {
            ConversationId = ToolLoopRun.ConversationId,
            Content = "and in red",
            Sender = "Fran",
            ChannelId = "signalr",
            AgentId = ToolLoopRun.AgentId,
            Steer = true,
            TurnKey = "steer-2"
        });
        await run.AdmittedSoFarAsync();
        lookUp.Finish();
        // Before the channel's stream ends, which would end any wait too.
        await Eventually.Until(
            () => run.Replies.Any(r => r is { IsComplete: true, TurnKey: "steer-1" }), "the first steer to be answered");
        await run.FinishAsync();

        run.StoredLine().Skip(3).ShouldBe(["user: no, the other one", "assistant: First answered"]);
        run.Replies.Count(r => r is { IsComplete: true, TurnKey: "steer-1" }).ShouldBe(1);
    }

    // The steers went in together and the turn they make is running: a steer pressed now cuts it at its
    // next tool boundary, as a steer cuts any turn of its person's, and is answered next.
    [Fact]
    public async Task ASteerAfterTheSteersWentInTogether_CutsTheirTurn()
    {
        var lookUp = LocalTool.Held("look_up", "42");
        var again = LocalTool.Held("again", "43");
        var model = new ScriptedModel(
            [lookUp.Call("call-1")], [again.Call("call-2")], [new TextContent("The third answered")]);
        await using var run = new ToolLoopRun(
            model, [lookUp, again], receipts: SteeredTogetherAtTheCut("steer-1", "steer-2"));

        run.Send("look it up");
        await lookUp.Started;
        run.Send("no, the other one", steer: true, turnKey: "steer-1");
        run.Send("and in red", steer: true, turnKey: "steer-2");
        await run.AdmittedSoFarAsync();
        lookUp.Finish();
        await again.Started;
        run.Send("in blue after all", steer: true, turnKey: "steer-3");
        await run.AdmittedSoFarAsync();
        again.Finish();
        await run.FinishAsync();

        run.StoredLine().Skip(3).ShouldBe([
            "user: no, the other one",
            "user: and in red",
            "assistant: call again#call-2",
            "tool: result call-2 = 43",
            "user: in blue after all",
            "assistant: The third answered"
        ]);
        model.Requests.Count.ShouldBe(3);
    }

    // Stopped once the steers went in together: the hub drew them, so it handed none back, and the turn is
    // kept as far as it got with both messages; had it handed them back, nothing of it is kept.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AStopAfterTheSteersWentInTogether_KeepsTheirTurnOrNothing_AsTheHubHandedThemBack(bool handedBack)
    {
        var lookUp = LocalTool.Held("look_up", "42");
        var model = new ScriptedModel([lookUp.Call("call-1")], [new TextContent("Both answered")]);
        model.Hold(1);
        await using var run = new ToolLoopRun(model, [lookUp], receipts: SteeredTogetherAtTheCut("steer-1", "steer-2"));

        run.Send("look it up");
        await lookUp.Started;
        run.Send("no, the other one", steer: true, turnKey: "steer-1");
        run.Send("and in red", steer: true, turnKey: "steer-2");
        await run.AdmittedSoFarAsync();
        lookUp.Finish();
        await Eventually.Until(() => model.Requests.Count == 2, "the steers' turn to be under way");
        run.Stop(returned: handedBack ? ["steer-1", "steer-2"] : ["queued-3"]);
        await run.FinishAsync();

        var cut = new[] { "user: look it up", "assistant: call look_up#call-1", "tool: result call-1 = 42" };
        run.StoredLine().ShouldBe(handedBack ? cut : [.. cut, "user: no, the other one", "user: and in red"]);
    }

    // Stopped before the cut turn's end was heard: the hub handed both steers back and says so in its answer
    // to that end, drawing nothing together, so neither runs.
    [Fact]
    public async Task SteersTheHubHandedBackAtTheCut_NeitherRuns()
    {
        var lookUp = LocalTool.Held("look_up", "42");
        var model = new ScriptedModel([lookUp.Call("call-1")], [new TextContent("Steered")]);
        await using var run = new ToolLoopRun(model, [lookUp], receipts: reply =>
            reply is { IsComplete: true, TurnKey: not ("steer-1" or "steer-2") }
                ? new ReplyReceipt(["steer-1", "steer-2"])
                : ReplyReceipt.None);

        run.Send("look it up");
        await lookUp.Started;
        run.Send("no, the other one", steer: true, turnKey: "steer-1");
        run.Send("and in red", steer: true, turnKey: "steer-2");
        await run.AdmittedSoFarAsync();
        lookUp.Finish();
        await run.FinishAsync();

        model.Requests.Count.ShouldBe(1);
        run.StoredLine().ShouldBe(["user: look it up", "assistant: call look_up#call-1", "tool: result call-1 = 42"]);
    }

    // Every steer the hub drew where the cut turn ended ends exactly once, and every message it drew is in the
    // stored line, whichever way the agent leaves the step between that end and the steers' turn (ticket 14).
    // A stop ends their keys at the hub, which hands none back once it drew them: the messages are kept as a
    // stopped turn with no answer, and nothing runs after the stop.
    public enum GatheringExit
    {
        // Two steers drawn together, the stop landing while their turn is being set up.
        StopWhileTheyGather,

        // One steer, the stop landing while its turn is being set up.
        StopBeforeALoneSteersTurn,

        // Two steers, the stop landing once their turn was handed to the agent but before its run reached the
        // history, which then decides nothing about it.
        StopBeforeTheirRunReachesTheHistory,

        // The steer reached the agent before the stop, which the group heard before it took the steer.
        ReachesTheTurnAfterTheStop
    }

    [Theory]
    [InlineData(GatheringExit.StopWhileTheyGather)]
    [InlineData(GatheringExit.StopBeforeALoneSteersTurn)]
    [InlineData(GatheringExit.StopBeforeTheirRunReachesTheHistory)]
    [InlineData(GatheringExit.ReachesTheTurnAfterTheStop)]
    public async Task EveryDrawnSteer_EndsOnce_AndIsInTheStoredLine_HoweverTheGatheringIsLeft(GatheringExit exit)
    {
        var lookUp = LocalTool.Held("look_up", "42");
        var model = new ScriptedModel([lookUp.Call("call-1")], [new TextContent("Steered")]);
        var recall = new HeldRecall();
        var runs = new HeldRecall();
        var cutEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cutEndHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var two = exit is GatheringExit.StopWhileTheyGather or GatheringExit.StopBeforeTheirRunReachesTheHistory;
        string[] drawn = two ? ["steer-1", "steer-2"] : ["steer-1"];
        await using var run = new ToolLoopRun(model, [lookUp], recall: recall, beforeRun: runs.BeforeRunAsync,
            receipts: SteeredTogetherAtTheCut(drawn),
            holdReply: async reply =>
            {
                if (exit == GatheringExit.ReachesTheTurnAfterTheStop
                    && reply is { ContentType: ReplyContentType.StreamComplete, IsComplete: true }
                    && ToolLoopRun.IsTheFirstTurns(reply))
                {
                    cutEndHeld.TrySetResult();
                    await cutEnd.Task.WaitAsync(TimeSpan.FromSeconds(60));
                }
            });
        var setUp = exit == GatheringExit.StopBeforeTheirRunReachesTheHistory
            ? runs.Hold("and in red")
            : recall.Hold(two ? "and in red" : "no, the other one");

        run.Send("look it up");
        await lookUp.Started;
        run.Send("no, the other one", steer: true, turnKey: "steer-1");
        if (two)
        {
            run.Send("and in red", steer: true, turnKey: "steer-2");
        }

        await run.AdmittedSoFarAsync();
        lookUp.Finish();
        if (exit == GatheringExit.ReachesTheTurnAfterTheStop)
        {
            await cutEndHeld.Task.WaitAsync(TimeSpan.FromSeconds(60));
            run.Stop(returned: []);
            await run.CancelHeardAsync();
            cutEnd.SetResult();
        }
        else
        {
            await setUp;
            run.Stop(returned: []);
        }

        await run.FinishAsync();

        string[] said = two ? ["user: no, the other one", "user: and in red"] : ["user: no, the other one"];
        run.StoredLine().Skip(3).ShouldBe(said);
        run.Line().Skip(3).ShouldAllBe(m => !m.IsFailedTurn() && !m.IsInterrupted());
        model.Requests.Count.ShouldBe(1);
        // The stop ended every drawn key at the hub, which takes nothing after it; whatever the cancelled
        // turn still sends ends each key once at most.
        drawn.ShouldAllBe(key => run.Replies.Count(r => r.IsComplete && r.TurnKey == key) <= 1);
    }

    // A stop's cancel tells the agent which steers it drew and which it handed back: a hub that names what it
    // returned (none, here) returns every steer it has not drawn, so one it does not name was drawn and is
    // kept; one it names is in its sender's composer and is not; and a hub from before, which names nothing,
    // leaves the agent dropping whatever waited, as it always did, rather than keep what it may have returned.
    [Theory]
    [InlineData("a hub from before", false)]
    [InlineData("nothing returned", true)]
    [InlineData("the steer returned", false)]
    public async Task AStopBeforeALoneSteersTurnStarts_KeepsIt_OnlyWhenTheCancelSaysItWasNotHandedBack(
        string cancel, bool kept)
    {
        var lookUp = LocalTool.Held("look_up", "42");
        var model = new ScriptedModel([lookUp.Call("call-1")], [new TextContent("Steered")]);
        var recall = new HeldRecall();
        await using var run = new ToolLoopRun(model, [lookUp], recall: recall);
        var setUp = recall.Hold("no, the other one");

        run.Send("look it up");
        await lookUp.Started;
        run.Send("no, the other one", steer: true, turnKey: "steer-1");
        await run.AdmittedSoFarAsync();
        lookUp.Finish();
        await setUp;
        run.Stop(returned: cancel switch
        {
            "a hub from before" => null,
            "nothing returned" => [],
            _ => ["steer-1"]
        });
        await run.FinishAsync();

        run.StoredLine().Skip(3).ShouldBe(kept ? ["user: no, the other one"] : []);
        model.Requests.Count.ShouldBe(1);
    }

    // Asked of every message as its turn is set up, as memory recall is, and of every run before it reaches the
    // agent: a message the test names holds its turn there until the turn is cancelled.
    private sealed class HeldRecall : IMemoryRecallHook
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource> _held = new();

        public Task Hold(string text) =>
            _held.GetOrAdd(text, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
                .Task.WaitAsync(TimeSpan.FromSeconds(60));

        public async Task<MemoryExtraction?> EnrichAsync(
            ChatMessage message, Account? account, string? conversationId, string? agentId, AgentSession thread,
            CancellationToken ct)
        {
            if (_held.TryGetValue(message.Text, out var held))
            {
                held.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            return null;
        }

        public async Task BeforeRunAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
        {
            if (messages.Select(m => _held.GetValueOrDefault(m.Text)).FirstOrDefault(h => h is not null) is { } held)
            {
                held.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
        }
    }

    // What the hub answers the cut turn's end with, as it draws the steers waiting there.
    private static Func<SendReplyParams, ReplyReceipt> SteeredTogetherAtTheCut(params string[] steers) =>
        reply => reply.IsComplete && !steers.Contains(reply.TurnKey)
            ? ReplyReceipt.None with { SteeredTogether = steers }
            : ReplyReceipt.None;

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

    // A turn cut by a steer is a whole turn: kept as any other, never marked stopped or failed, and read
    // back as a reply showing the tools it ran though it said nothing.
    [Fact]
    public async Task ACutTurn_IsNeitherInterruptedNorFailed_AndIsShownAsAReplyThatSaidNothing()
    {
        var lookUp = LocalTool.Held("look_up", "42");
        var model = new ScriptedModel([lookUp.Call("call-1")], [new TextContent("Steered")]);
        await using var run = new ToolLoopRun(model, [lookUp]);

        run.Send("look it up");
        await lookUp.Started;
        run.Send("the other one", steer: true);
        await run.AdmittedSoFarAsync();
        lookUp.Finish();
        await run.FinishAsync();

        var line = run.Line();
        line.ShouldAllBe(m => !m.IsInterrupted() && !m.IsFailedTurn());
        ToolLoopRun.Render(Transcript.Shown(line)).ShouldBe([
            "user: look it up",
            "tool: result call-1 = 42",
            "user: the other one",
            "assistant: Steered"
        ]);
    }

    // The chat hub hands a steer still waiting back to its sender when they stop the reply, so the
    // agent must not run it: it is dropped with the rest of what waited behind the cancelled turn.
    [Fact]
    public async Task AStopWithASteerPending_DropsTheSteer()
    {
        var lookUp = LocalTool.Held("look_up", "42");
        var model = new ScriptedModel([lookUp.Call("call-1")], [new TextContent("Steered")]);
        await using var run = new ToolLoopRun(model, [lookUp]);

        run.Send("look it up");
        await lookUp.Started;
        run.Send("the other one", steer: true);
        await run.AdmittedSoFarAsync();
        run.Stop();
        await run.FinishAsync();

        model.Requests.Count.ShouldBe(1);
        run.StoredLine().ShouldNotContain("user: the other one");
        run.StoredLine().First().ShouldBe("user: look it up");
    }

    // A stop landing just as the steered turn ended: the agent has started the steer's turn, but the hub
    // had not heard the steered one end, so it handed the steer back to its sender's composer and says so
    // on the cancel. The agent keeps nothing of that turn, or the person would have the steer twice, once
    // in their composer and once in the conversation; a stopped turn the hub did not hand back is kept
    // as far as it got, as any stopped turn is.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStopAfterTheSteersTurnStarted_KeepsNothingOfIt_WhenTheHubHandedItBack(bool handedBack)
    {
        var lookUp = LocalTool.Held("look_up", "42");
        var model = new ScriptedModel([lookUp.Call("call-1")], [new TextContent("Steered")]);
        model.Hold(1);
        await using var run = new ToolLoopRun(model, [lookUp]);

        run.Send("look it up");
        await lookUp.Started;
        run.Send("the other one", steer: true, turnKey: "steer-1");
        await run.AdmittedSoFarAsync();
        lookUp.Finish();
        await Eventually.Until(() => model.Requests.Count == 2, "the steer's turn to be under way");
        run.Stop(returned: handedBack ? ["steer-1", "queued-2"] : ["queued-2"]);
        await run.FinishAsync();

        var cut = new[] { "user: look it up", "assistant: call look_up#call-1", "tool: result call-1 = 42" };
        run.StoredLine().ShouldBe(handedBack ? cut : [.. cut, "user: the other one"]);
    }

    // The stop won the hub's race against the steered turn's end: the hub handed the steer back before it
    // heard that end, and says so in its answer to the end's send_reply, which the agent waits for before
    // it starts the next turn. The steer is never run, so nothing of it is kept, however fast it would
    // have finished and whenever the cancel arrives.
    [Fact]
    public async Task ASteerTheHubNamesInItsAnswerToTheSteeredTurnsEnd_IsNeverRun()
    {
        var lookUp = LocalTool.Held("look_up", "42");
        var model = new ScriptedModel([lookUp.Call("call-1")], [new TextContent("Steered")]);
        await using var run = new ToolLoopRun(model, [lookUp], receipts: reply =>
            reply is { IsComplete: true, TurnKey: not "steer-1" } ? new ReplyReceipt(["steer-1"]) : ReplyReceipt.None);

        run.Send("look it up");
        await lookUp.Started;
        run.Send("the other one", steer: true, turnKey: "steer-1");
        await run.AdmittedSoFarAsync();
        lookUp.Finish();
        await run.FinishAsync();

        model.Requests.Count.ShouldBe(1);
        run.StoredLine().ShouldBe(["user: look it up", "assistant: call look_up#call-1", "tool: result call-1 = 42"]);
    }
}