using Domain.DTOs.Metrics;
using Infrastructure.Agents.ChatClients;
using Shouldly;

namespace Tests.Eval.Harness;

// A run with real workers is heard by one recording: the worker is built by the same factory, so
// its calls and its turns arrive at the same observer as its parent's. What a scenario declares —
// the permitted set, the ceiling, the order — is about the agent the turn was addressed to, so
// the recording has to keep the two apart.
public class RecordingTests
{
    private static ToolInvocation Call(string tool, int sequence, string? agent) => new()
    {
        Sequence = sequence,
        ToolName = tool,
        Arguments = "{}",
        Outcome = ToolInvocationOutcome.Completed,
        AgentId = agent
    };

    [Fact]
    public void AWorkersCalls_AreKeptApartFromItsParents()
    {
        var recording = new Recording { Parent = ["Jonas", "Jonas-eval:1"] };

        recording.OnInvoked(Call("domain__subagents__run_subagent", 0, "Jonas"));
        recording.OnInvoked(Call("mcp__web__web_search", 0, "Jonas Worker"));
        recording.OnInvoked(Call("mcp__web__web_browse", 1, "Jonas Worker"));

        recording.Calls.Select(c => c.ToolName).ShouldBe(["domain__subagents__run_subagent"]);
        recording.WorkerCalls.Select(c => c.ToolName).ShouldBe(["mcp__web__web_search", "mcp__web__web_browse"]);
    }

    [Fact]
    public void AWorkersTurn_DoesNotReplaceTheParentsPromptOrRoute()
    {
        var recording = new Recording { Parent = ["Jonas", "Jonas-eval:1"] };
        var served = new ServedRoute("parent/model", "p", null);

        recording.OnTurn(new TurnObservation("the parent's prompt", served) { AgentId = "Jonas" });
        recording.OnTurn(new TurnObservation("the worker's prompt", null) { AgentId = "Jonas Worker" });

        recording.SystemPrompt.ShouldBe("the parent's prompt");
        recording.Route.ShouldBe(served);
    }

    // A preload is published by the judge's own client, which names the agent as its session
    // does — "Jonas-<conversation>", "subagent-<profile>" — where a call names it as its metrics
    // do. Both spellings of the parent have to be the parent, or every host preload of an armed
    // run is filed as a worker's and the load a scenario requires is reported as never made.
    [Fact]
    public void AHostPreload_IsTheTurns_UnderTheNameItsSessionCarries_AndAWorkersIsNot()
    {
        var recording = new Recording { Parent = ["Jonas", "Jonas-eval:1"] };

        recording.Publish(new SkillPreloadEvent
        {
            AgentId = "Jonas-eval:1", Outcome = "preloaded", Skills = ["web-browsing"], Reads = []
        });
        recording.Publish(new SkillPreloadEvent
        {
            AgentId = "subagent-jonas-worker", Outcome = "preloaded", Skills = ["sandbox"], Reads = []
        });

        recording.Preloads.ShouldHaveSingleItem().Arguments.ShouldContain("web-browsing");
        recording.Calls.ShouldHaveSingleItem();
    }

    // Every scenario with canned workers, and every harness test that builds a recording by hand:
    // nobody named a parent, so nothing is a worker's.
    [Fact]
    public void WithNoParentNamed_EveryCallIsTheTurns()
    {
        var recording = new Recording();

        recording.OnInvoked(Call("domain__filesystem__glob", 0, "Jonas"));
        recording.OnInvoked(Call("domain__filesystem__read", 1, null));

        recording.Calls.Count.ShouldBe(2);
        recording.WorkerCalls.ShouldBeEmpty();
    }
}