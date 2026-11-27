using Domain.DTOs.WebChat;
using Shouldly;
using WebChat.Client.Components.Chat;

namespace Tests.Unit.WebChat.Client.Components;

public sealed class ActivitySummaryTests
{
    private static ActivityStep Tool(string id, string name, ToolCallOutcome? outcome = null, string args = "{}") =>
        new(ActivityKind.Tool, Tool: new ToolCallStep { CallId = id, Name = name, Arguments = args, Outcome = outcome });

    [Fact]
    public void Live_SaysWhatTheReplyIsDoingNow_AndTheLatestOfIt()
    {
        var running = ActivitySummary.Of([Tool("c1", "domain__filesystem__exec", args: """{"command":"ls ~"}""")], live: true, answered: false);
        (running.Title, running.Preview).ShouldBe(("Running exec", "ls ~"));

        var thinking = ActivitySummary.Of([new ActivityStep(ActivityKind.Reasoning, "first\nthen this")], live: true, answered: false);
        (thinking.Title, thinking.Preview).ShouldBe(("Thinking", "then this"));

        ActivitySummary.Of([], live: true, answered: false).Title.ShouldBe("Working");
    }

    [Fact]
    public void Done_SaysWhatTheReplyDid_WhichTools_AndHowManyFailed()
    {
        var summary = ActivitySummary.Of(
        [
            new ActivityStep(ActivityKind.Reasoning, "hmm"),
            Tool("c1", "mcp__x__glob", ToolCallOutcome.Done),
            Tool("c2", "mcp__x__glob", ToolCallOutcome.Failed),
            Tool("c3", "mcp__y__read", ToolCallOutcome.Done)
        ], live: false, answered: true);

        (summary.Title, summary.Tools, summary.Failed, summary.Ended).ShouldBe(("Used 3 tools", 3, 1, null));
        summary.ToolNames.ShouldBe(["glob", "read"]);
        summary.Preview.ShouldBeNull();
    }

    [Fact]
    public void AReplyCutBeforeItAnswered_SaysWhatCutIt()
    {
        ActivitySummary.Of([Tool("c1", "glob", ToolCallOutcome.Done)], live: false, answered: false)
            .ShouldSatisfyAllConditions(s => s.Title.ShouldBe("Used 1 tool"), s => s.Ended.ShouldBe("Interrupted"));
        ActivitySummary.Of([Tool("c1", "delete", ToolCallOutcome.Declined)], live: false, answered: false)
            .Ended.ShouldBe("Turned down");
        ActivitySummary.Of([Tool("c1", "glob")], live: true, answered: false).Ended.ShouldBeNull();
    }

    [Fact]
    public void ManyToolNames_ShowTheFirstFewAndHowManyMore()
    {
        ActivitySummary.Of([.. Enumerable.Range(0, 5).Select(i => Tool($"c{i}", $"t{i}", ToolCallOutcome.Done))], false, true)
            .ToolNames.ShouldBe(["t0", "t1", "t2", "+2"]);
    }

    [Fact]
    public void ReasoningAlone_IsReasoning() =>
        ActivitySummary.Of([new ActivityStep(ActivityKind.Reasoning, "hmm")], false, true).Title.ShouldBe("Reasoning");
}