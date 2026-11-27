using Domain.Agents;
using Domain.Conversations;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.Conversations;

public class TranscriptStepsTests
{
    private static ChatMessage User(string text) => new(ChatRole.User, text);

    private static ChatMessage Assistant(params AIContent[] contents) => new(ChatRole.Assistant, contents);

    private static ChatMessage Tool(string callId, object? result) =>
        new(ChatRole.Tool, [new FunctionResultContent(callId, result)]);

    private static FunctionCallContent Call(string id, string name = "mcp__x__glob") =>
        new(id, name, new Dictionary<string, object?> { ["pattern"] = "*" });

    private static IReadOnlyList<string> Describe(IReadOnlyList<TurnStep>? steps) =>
        steps?.Select(s => s.Reasoning is { } r ? $"think:{r}" : $"{s.Tool!.CallId}:{s.Tool.Outcome}").ToList() ?? [];

    [Fact]
    public void ToolsThenAnAnswer_AreTheAnswersStepsBeforeItsWords()
    {
        var answer = Assistant(new TextContent("done"));
        List<ChatMessage> line =
        [
            User("go"),
            Assistant(new TextReasoningContent("look "), new TextReasoningContent("first"), Call("c1")),
            Tool("c1", "a.txt"),
            answer
        ];

        var steps = TranscriptSteps.Of(line);

        Describe(steps[answer].Before).ShouldBe(["think:look first", "c1:Done"]);
        steps[answer].After.ShouldBeNull();
        steps[answer].Before![1].Tool!.Result.ShouldBe("a.txt");
    }

    [Fact]
    public void ARepliesOwnReasoning_IsBeforeItsWords()
    {
        var answer = Assistant(new TextReasoningContent("hmm"), new TextContent("hi"));

        Describe(TranscriptSteps.Of([User("hey"), answer])[answer].Before).ShouldBe(["think:hmm"]);
    }

    [Fact]
    public void WordsThatLedToACall_PassTheCallToTheNextWords()
    {
        var interim = Assistant(new TextContent("let me look"), Call("c1"));
        var answer = Assistant(new TextContent("found it"));

        var steps = TranscriptSteps.Of([User("go"), interim, Tool("c1", "x"), answer]);

        steps.ContainsKey(interim).ShouldBeFalse();
        Describe(steps[answer].Before).ShouldBe(["c1:Done"]);
    }

    [Fact]
    public void CallsAfterTheTurnsLastWords_AreThoseWordsStepsAfter()
    {
        var interim = Assistant(new TextContent("on it"), Call("c1"));
        List<ChatMessage> line =
        [
            User("go"), interim, Tool("c1", "x"), Assistant(Call("c2")), Tool("c2", "y"), User("actually stop")
        ];

        Describe(TranscriptSteps.Of(line)[interim].After).ShouldBe(["c1:Done", "c2:Done"]);
    }

    [Fact]
    public void AReplyCutBeforeItSaidAWord_HangsItsCallsOnTheResultThatStandsForIt()
    {
        var standIn = Tool("c2", "y");
        List<ChatMessage> line =
        [
            User("go"), Assistant(Call("c1")), Tool("c1", "x"), Assistant(Call("c2")), standIn, User("steer")
        ];

        Describe(TranscriptSteps.Of(line)[standIn].Before).ShouldBe(["c1:Done", "c2:Done"]);
    }

    [Fact]
    public void ACallTurnedDown_IsDeclined_OnTheLineSaidInItsPlace()
    {
        var turnedDown = Tool("c1", ApprovalRejection.ResultFor("mcp__x__delete"));

        var steps = TranscriptSteps.Of([User("delete it"), Assistant(Call("c1", "mcp__x__delete")), turnedDown]);

        Describe(steps[turnedDown].Before).ShouldBe(["c1:Declined"]);
    }

    [Fact]
    public void EachTurnKeepsItsOwnSteps()
    {
        var first = Assistant(new TextContent("one"));
        var second = Assistant(new TextContent("two"));

        var steps = TranscriptSteps.Of(
            [User("a"), Assistant(Call("c1")), Tool("c1", "x"), first, User("b"), Assistant(Call("c2")), Tool("c2", "y"), second]);

        Describe(steps[first].Before).ShouldBe(["c1:Done"]);
        Describe(steps[second].Before).ShouldBe(["c2:Done"]);
    }

    [Fact]
    public void ACallWithNoAnswer_IsStillShown_NotAnswered()
    {
        var answer = Assistant(new TextContent("stopped"));

        var steps = TranscriptSteps.Of([User("go"), Assistant(Call("c1")), answer]);

        steps[answer].Before!.Single().Tool!.HasAnswered.ShouldBeFalse();
    }

    [Fact]
    public void AMessageThatDidNothing_HasNoSteps()
    {
        var answer = Assistant(new TextContent("hi"));

        TranscriptSteps.Of([User("hey"), answer]).ContainsKey(answer).ShouldBeFalse();
    }
}