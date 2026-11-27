using Domain.DTOs.WebChat;
using Shouldly;
using WebChat.Client.Components.Chat;
using WebChat.Client.Models;

namespace Tests.Unit.WebChat.Client.Components;

public sealed class TurnLayoutTests
{
    private static ChatMessageModel User(string text) => new() { Role = "user", Content = text };

    private static ChatMessageModel Says(string text, string? id = null) =>
        new() { Role = "assistant", Content = text, MessageId = id };

    private static ToolCallStep Call(string id, string name = "mcp__x__glob") =>
        new() { CallId = id, Name = name, Arguments = """{"pattern":"*"}""" };

    private static ToolCallStep Answer(string id, ToolCallOutcome outcome = ToolCallOutcome.Done) =>
        new() { CallId = id, Result = "a.txt", Outcome = outcome };

    private static ChatMessageModel Calls(params ToolCallStep[] tools) => new() { Role = "assistant", Tools = tools };

    private static IReadOnlyList<string> Describe(ChatMessageModel row) =>
        row.Activity?.Select(s => s.Kind switch
        {
            ActivityKind.Reasoning => $"think:{s.Text}",
            ActivityKind.Words => $"said:{s.Text}",
            _ => $"{s.Tool!.CallId}:{s.Tool.Outcome?.ToString() ?? "running"}"
        }).ToList() ?? [];

    [Fact]
    public void AReplyThatDidNothingElse_IsItsAnswerAlone()
    {
        var rows = TurnLayout.Rows([User("hi"), Says("hello", "m1")]);

        rows.Count.ShouldBe(2);
        rows[1].Content.ShouldBe("hello");
        rows[1].Activity.ShouldBeEmpty();
    }

    [Fact]
    public void ToolsThenAnAnswer_AreOneGroupAboveTheAnswer_EachCallFoldedWithItsAnswer()
    {
        var rows = TurnLayout.Rows(
        [
            User("go"),
            new ChatMessageModel { Role = "assistant", Reasoning = "look first", Tools = [Call("c1")], MessageId = "m1" },
            Calls(Answer("c1")),
            Says("found a.txt", "m2") with { EntryId = "e2" }
        ]);

        rows.Count.ShouldBe(2);
        Describe(rows[1]).ShouldBe(["think:look first", "c1:Done"]);
        rows[1].Activity![1].Tool!.Name.ShouldBe("mcp__x__glob");
        (rows[1].Content, rows[1].EntryId, rows[1].RunKey).ShouldBe(("found a.txt", "e2", "m1"));
    }

    // What streamed and what a reload reads are laid out the same.
    [Fact]
    public void AReplyReadFromTheHistory_IsLaidOutAsItStreamed()
    {
        var streamed = TurnLayout.Rows(
        [
            User("go"),
            new ChatMessageModel { Role = "assistant", Reasoning = "look first", Tools = [Call("c1")] },
            Calls(Answer("c1")),
            Says("found a.txt")
        ]);
        var reloaded = TurnLayout.Rows(
        [
            User("go"),
            Says("found a.txt") with { Steps = [new TurnStep(Reasoning: "look first"), new TurnStep(Tool: Call("c1").Merge(Answer("c1")))] }
        ]);

        reloaded[1].Activity.ShouldBe(streamed[1].Activity);
        reloaded[1].Content.ShouldBe(streamed[1].Content);
    }

    [Fact]
    public void WordsACallCameAfter_AreInterim_InTheGroup()
    {
        var rows = TurnLayout.Rows(
        [
            User("go"), Says("let me look") with { Tools = [Call("c1")] }, Calls(Answer("c1")), Says("done")
        ]);

        Describe(rows[1]).ShouldBe(["said:let me look", "c1:Done"]);
        rows[1].Content.ShouldBe("done");
    }

    // The steer splits the reply's activity in two: the cut reply is a group with no answer, the steer
    // is drawn between, and the reply to it is a group of its own.
    [Fact]
    public void ASteer_SplitsTheReplyInTwo()
    {
        var rows = TurnLayout.Rows(
        [
            User("go"),
            Says("on it", "m1") with { Tools = [Call("c1")] },
            Calls(Answer("c1")),
            User("actually, the other folder"),
            Calls(Call("c2")) with { MessageId = "m3" },
            Calls(Answer("c2")),
            Says("the other folder has b.txt")
        ]);

        rows.Select(r => r.Role).ShouldBe(["user", "assistant", "user", "assistant"]);
        Describe(rows[1]).ShouldBe(["said:on it", "c1:Done"]);
        rows[1].Content.ShouldBeEmpty();
        Describe(rows[3]).ShouldBe(["c2:Done"]);
        rows[3].Content.ShouldBe("the other folder has b.txt");
        rows[1].RunKey.ShouldNotBe(rows[3].RunKey);
    }

    [Fact]
    public void AReplyStillCalling_HasNoAnswerYet()
    {
        var rows = TurnLayout.Rows([User("go"), Says("checking"), Calls(Call("c1"))]);

        Describe(rows[1]).ShouldBe(["said:checking", "c1:running"]);
        rows[1].Content.ShouldBeEmpty();
    }

    [Fact]
    public void AFailure_IsARowOfItsOwn_AfterWhatTheReplyDid()
    {
        var error = new ChatMessageModel { Role = "assistant", Content = "boom", IsError = true };

        var rows = TurnLayout.Rows([User("go"), Calls(Call("c1")), Calls(Answer("c1")), error]);

        rows.Count.ShouldBe(3);
        Describe(rows[1]).ShouldBe(["c1:Done"]);
        rows[2].ShouldBe(error);
    }

    [Fact]
    public void ACallTurnedDown_IsDeclined_AboveTheLineSaidInItsPlace()
    {
        var rows = TurnLayout.Rows(
        [
            User("delete it"),
            Says("x was not run: it was turned down.") with
            {
                Steps = [new TurnStep(Tool: Call("c1").Merge(Answer("c1", ToolCallOutcome.Declined)))]
            }
        ]);

        Describe(rows[1]).ShouldBe(["c1:Declined"]);
        rows[1].Content.ShouldBe("x was not run: it was turned down.");
    }

    // What followed a Continue is the rest of the reply before it: one answer, named as the reply it
    // continues, cut off only if the rest was, with whatever ran between the two in its group.
    [Fact]
    public void AContinuation_IsTheRestOfTheAnswerItContinues()
    {
        var rows = TurnLayout.Rows(
        [
            User("a story"),
            Says("Once upon ", "m1") with { EntryId = "a1", IsInterrupted = true },
            Calls(Call("c1")),
            Calls(Answer("c1")),
            Says("a time.", "m2") with { EntryId = "a2", ContinuesPrevious = true, IsInterrupted = true }
        ]);

        rows.Count.ShouldBe(2);
        (rows[1].Content, rows[1].EntryId, rows[1].IsInterrupted).ShouldBe(("Once upon a time.", "a1", true));
        Describe(rows[1]).ShouldBe(["c1:Done"]);
    }

    [Fact]
    public void AReplysVersions_AreOnItsRow_FromItsFirstMessage()
    {
        var versions = new MessageVersions("u1", 1, 2);

        var rows = TurnLayout.Rows(
        [
            new ChatMessageModel { Role = "assistant", Reasoning = "Thinking", Versions = versions }, Says("Answer")
        ]);

        rows.ShouldHaveSingleItem().Versions.ShouldBe(versions);
        Describe(rows[0]).ShouldBe(["think:Thinking"]);
    }

    // Words right after words, with nothing run between them, are two replies: a fire answered while the
    // person's turn ran, a refusal.
    [Fact]
    public void WordsAfterWords_WithNoCallBetween_AreTwoReplies()
    {
        var rows = TurnLayout.Rows(
        [
            new ChatMessageModel { Role = "assistant", Reasoning = "Thinking" }, Says("First answer", "g1"), Says("Second answer", "g2")
        ]);

        rows.Select(r => r.Content).ShouldBe(["First answer", "Second answer"]);
        Describe(rows[0]).ShouldBe(["think:Thinking"]);
    }

    [Fact]
    public void ANoReplyLine_AndAServiceNotice_PassThrough()
    {
        var noReply = new ChatMessageModel { Role = "assistant", NoReply = true, Versions = new MessageVersions("s", 0, 2) };
        var notice = new ChatMessageModel { Role = "user", Content = "fire", SenderId = "scheduler" };

        var rows = TurnLayout.Rows([notice, Says("done"), User("again"), noReply]);

        rows[0].ShouldBe(notice);
        rows[3].ShouldBe(noReply);
    }

    [Fact]
    public void ACallsAnswerSeenTwice_IsFoldedOnce()
    {
        var rows = TurnLayout.Rows([User("go"), Calls(Call("c1")), Calls(Answer("c1")), Calls(Answer("c1")), Says("ok")]);

        Describe(rows[1]).ShouldBe(["c1:Done"]);
    }
}