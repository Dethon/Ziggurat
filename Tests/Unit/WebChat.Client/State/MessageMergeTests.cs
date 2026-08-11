using Shouldly;
using WebChat.Client.Components.Chat;
using WebChat.Client.Models;

namespace Tests.Unit.WebChat.Client.State;

public sealed class MessageMergeTests
{
    [Fact]
    public void ReasoningOnly_FollowedByContent_MergesIntoOne()
    {
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "assistant", Content = "", Reasoning = "Thinking" },
            new() { Role = "assistant", Content = "Answer" }
        };

        var result = MessageList.MergeConsecutiveAssistantMessages(messages);

        result.Count.ShouldBe(1);
        result[0].Content.ShouldBe("Answer");
        result[0].Reasoning.ShouldBe("Thinking");
    }

    [Fact]
    public void MultipleReasoningBlocks_JoinedWithSeparator()
    {
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "assistant", Content = "", Reasoning = "Step 1" },
            new() { Role = "assistant", Content = "Answer", Reasoning = "Step 2" }
        };

        var result = MessageList.MergeConsecutiveAssistantMessages(messages);

        result.Count.ShouldBe(1);
        result[0].Reasoning.ShouldBe("Step 1\n-----\nStep 2");
    }

    [Fact]
    public void ToolCallsOnly_FollowedByContent_MergesToolCalls()
    {
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "assistant", Content = "", ToolCalls = "tool_1" },
            new() { Role = "assistant", Content = "Result", ToolCalls = "tool_2" }
        };

        var result = MessageList.MergeConsecutiveAssistantMessages(messages);

        result.Count.ShouldBe(1);
        result[0].Content.ShouldBe("Result");
        result[0].ToolCalls.ShouldBe("tool_1\ntool_2");
    }

    [Fact]
    public void TrailingContentLess_PreservedWhenAlone()
    {
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "Hello" },
            new() { Role = "assistant", Content = "", Reasoning = "Thinking" }
        };

        var result = MessageList.MergeConsecutiveAssistantMessages(messages);

        result.Count.ShouldBe(2);
        result[1].Reasoning.ShouldBe("Thinking");
        result[1].Content.ShouldBeEmpty();
    }

    [Fact]
    public void NonAssistantBreaksRun()
    {
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "assistant", Content = "First" },
            new() { Role = "user", Content = "Question" },
            new() { Role = "assistant", Content = "Second" }
        };

        var result = MessageList.MergeConsecutiveAssistantMessages(messages);

        result.Count.ShouldBe(3);
    }

    [Fact]
    public void ReasoningFragment_ThenMultipleWithContent_MergesOnlyFragment()
    {
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "assistant", Content = "", Reasoning = "Thinking" },
            new() { Role = "assistant", MessageId = "gen-1", Content = "First answer" },
            new() { Role = "assistant", MessageId = "gen-2", Content = "Second answer" }
        };

        var result = MessageList.MergeConsecutiveAssistantMessages(messages);

        result.Count.ShouldBe(2);
        result[0].Reasoning.ShouldBe("Thinking");
        result[0].Content.ShouldBe("First answer");
        result[1].Content.ShouldBe("Second answer");
    }
}