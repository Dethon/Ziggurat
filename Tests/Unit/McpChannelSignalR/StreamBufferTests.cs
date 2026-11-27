using Domain.DTOs.WebChat;
using McpChannelSignalR.Internal;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

public class StreamBufferTests
{
    [Fact]
    public void Add_TextChunks_AggregatesBySameMessageId()
    {
        var sut = new StreamBuffer();
        sut.Add(new ChatStreamMessage { Content = "Hello ", MessageId = "m1" });
        sut.Add(new ChatStreamMessage { Content = "World", MessageId = "m1" });

        var messages = sut.GetAll();
        messages.Count.ShouldBe(1);
        messages[0].Content.ShouldBe("Hello World");
    }

    [Fact]
    public void Add_DifferentMessageIds_CreatesSeparateEntries()
    {
        var sut = new StreamBuffer();
        sut.Add(new ChatStreamMessage { Content = "A", MessageId = "m1" });
        sut.Add(new ChatStreamMessage { Content = "B", MessageId = "m2" });

        sut.GetAll().Count.ShouldBe(2);
    }

    [Fact]
    public void Add_UserMessage_AlwaysNewEntry()
    {
        var sut = new StreamBuffer();
        sut.Add(new ChatStreamMessage { UserMessage = new UserMessageInfo("u1", DateTimeOffset.UtcNow) });
        sut.Add(new ChatStreamMessage { UserMessage = new UserMessageInfo("u1", DateTimeOffset.UtcNow) });

        sut.GetAll().Count.ShouldBe(2);
    }

    [Fact]
    public void Add_ExceedsMaxBuffer_RemovesOldest()
    {
        var sut = new StreamBuffer();
        for (var i = 0; i < 101; i++)
        {
            sut.Add(new ChatStreamMessage { Content = $"msg-{i}", MessageId = $"id-{i}" });
        }

        var messages = sut.GetAll();
        messages.Count.ShouldBe(100);
        messages[0].Content.ShouldBe("msg-1");
    }

    [Fact]
    public void Add_ReasoningChunks_AggregatesByMessageId()
    {
        var sut = new StreamBuffer();
        sut.Add(new ChatStreamMessage { Reasoning = "Think ", MessageId = "m1" });
        sut.Add(new ChatStreamMessage { Reasoning = "more", MessageId = "m1" });

        var messages = sut.GetAll();
        messages.Count.ShouldBe(1);
        messages[0].Reasoning.ShouldBe("Think more");
    }

    // A call's answer comes under the call's own message id and folds into the call, so a page joining
    // later is handed each call once, answered.
    [Fact]
    public void Add_ToolSteps_FoldEachAnswerIntoItsCall()
    {
        var sut = new StreamBuffer();
        sut.Add(new ChatStreamMessage { Tools = [new ToolCallStep { CallId = "c1", Name = "glob" }], MessageId = "m1" });
        sut.Add(new ChatStreamMessage { Tools = [new ToolCallStep { CallId = "c2", Name = "read" }], MessageId = "m1" });
        sut.Add(new ChatStreamMessage
        {
            Tools = [new ToolCallStep { CallId = "c1", Result = "a.txt", Outcome = ToolCallOutcome.Done }], MessageId = "m1"
        });

        var tools = sut.GetAll().ShouldHaveSingleItem().Tools!;
        tools.Select(t => (t.CallId, t.Name, t.Result)).ShouldBe([("c1", "glob", "a.txt"), ("c2", "read", null)]);
    }
}