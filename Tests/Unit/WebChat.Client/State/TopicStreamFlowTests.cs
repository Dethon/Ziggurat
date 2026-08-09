using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

// One reply in flight per topic, asserted over the whole client with only the transport
// scripted. These are the invariants the module exists for, so they belong at the seam where
// a future caller could break them without touching the module.
public sealed class TopicStreamFlowTests
{
    [Fact]
    public async Task ASend_ToAnIdleTopic_OpensOneReply()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var reply = new GatedChatStream();
        transport.Answer("SendMessage", _ => reply.Chunks());

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "hello"));

        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));
        transport.Calls.Count(call => call.MethodName == "SendMessage").ShouldBe(1);
        reply.Release();
    }

    [Fact]
    public async Task ASecondSend_WhileTheReplyIsBeingWritten_JoinsItAndOpensNoSecondStream()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var reply = new GatedChatStream();
        transport.Answer("SendMessage", _ => reply.Chunks());
        transport.Answer("EnqueueMessage", true);
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "first"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "second"));

        await TestChat.Eventually(() => transport.Calls.Any(call => call.MethodName == "EnqueueMessage"));
        transport.Calls.Count(call => call.MethodName == "SendMessage").ShouldBe(1);
        reply.Release();
    }

    // A false from enqueue is the server saying there is no reply in progress, so the one this
    // client thought was running is over: it ends, keeping what it wrote, and a fresh one opens.
    [Fact]
    public async Task ASecondSend_WhenThereIsNothingToEnqueueOnto_OpensAFreshStream()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var older = new GatedChatStream();
        var newer = new GatedChatStream();
        var opened = 0;
        transport.Answer("SendMessage", _ => opened++ == 0 ? older.Chunks() : newer.Chunks());
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "first"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "second"));

        await TestChat.Eventually(() => transport.Calls.Count(call => call.MethodName == "SendMessage") == 2);
        client.Streaming.State.StreamingTopics.ShouldContain("topic-1");
        AssistantMessages(client).ShouldContain(message => message.Content == "thinking");
        older.Release();
        newer.Release();
    }

    // The late-cleanup case: the first reply's loop only finishes after the user has sent
    // again, and by then a newer stream holds the topic. Its ending must change nothing.
    [Fact]
    public async Task AnOldReplysEnding_AfterTheUserSentAgain_LeavesTheNewOneAlone()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var older = new GatedChatStream();
        var newer = new GatedChatStream();
        var opened = 0;
        transport.Answer("SendMessage", _ => opened++ == 0 ? older.Chunks() : newer.Chunks());
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "first"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));
        var olderReply = client.Service<TopicStreams>().Snapshot("topic-1");

        client.Dispatcher.Dispatch(new CancelStreaming("topic-1"));
        await olderReply.Completion!;
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "second"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));
        older.Release();
        await olderReply.Stream!;

        client.Service<TopicStreams>().Snapshot("topic-1").IsStreaming.ShouldBeTrue();
        client.Streaming.State.StreamingTopics.ShouldContain("topic-1");
        newer.Release();
    }

    [Fact]
    public async Task TheStopButton_KeepsTheTextThatHadAlreadyArrived()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var reply = new GatedChatStream();
        transport.Answer("SendMessage", _ => reply.Chunks());
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "hello"));
        await TestChat.Eventually(() =>
            client.Streaming.State.StreamingByTopic.GetValueOrDefault("topic-1")?.Content == "thinking");

        var replyEnding = client.Service<TopicStreams>().Snapshot("topic-1").Completion!;
        client.Dispatcher.Dispatch(new CancelStreaming("topic-1"));

        await replyEnding;
        AssistantMessages(client).ShouldContain(message => message.Content == "thinking");
        reply.Release();
    }

    [Fact]
    public async Task ADeleteMidReply_EndsTheReplyAndKeepsNothingStreaming()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var reply = new GatedChatStream();
        transport.Answer("SendMessage", _ => reply.Chunks());
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "hello"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));

        var replyEnding = client.Service<TopicStreams>().Snapshot("topic-1").Completion!;
        client.Dispatcher.Dispatch(new RemoveTopic("topic-1", "agent-1", 10, 20));

        await replyEnding;
        transport.Calls.ShouldContain(call => call.MethodName == "CancelTopic");
        client.Service<TopicStreams>().Snapshot("topic-1").HasStream.ShouldBeFalse();
        reply.Release();
    }

    // A resolved approval takes the prompt off screen and says nothing about the reply. What it
    // let through arrives on the topic's stream, so this push must leave an idle conversation
    // idle rather than sprout a streaming bubble out of it.
    [Fact]
    public async Task AResolvedApproval_ForAnIdleTopic_LeavesNothingBehind()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);

        transport.Raise("OnApprovalResolved", new ApprovalResolvedNotification("topic-1", "approval-1"));

        await Task.Delay(20);
        client.Streaming.State.StreamingByTopic.ShouldNotContainKey("topic-1");
        client.Streaming.State.StreamingTopics.ShouldNotContain("topic-1");
    }

    // Another person writing into the conversation closes off what the agent had written so
    // far, so the two do not merge into one bubble.
    [Fact]
    public async Task AnotherPersonsMessage_ArrivingMidReply_ClosesOffWhatTheAgentHadWritten()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        client.Dispatcher.Dispatch(new SelectTopic("topic-1"));
        var reply = new GatedChatStream();
        transport.Answer("SendMessage", _ => reply.Chunks());
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "hello"));
        await TestChat.Eventually(() =>
            client.Streaming.State.StreamingByTopic.GetValueOrDefault("topic-1")?.Content == "thinking");

        transport.Raise("OnUserMessage", new UserMessageNotification(
            "topic-1", "and one more thing", "someone-else", DateTimeOffset.UnixEpoch));

        await TestChat.Eventually(() => AssistantMessages(client).Any(m => m.Content == "thinking"));
        client.Streaming.State.StreamingByTopic["topic-1"].HasContent.ShouldBeFalse();
        reply.Release();
    }

    // Closing off the half-written bubble does not end the message: the reply carries on under
    // the same id, and what arrives after has to extend the bubble rather than replace it.
    [Fact]
    public async Task AnotherPersonsMessage_MidReply_LeavesTheRestOfTheReplyExtendingTheBubble()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        client.Dispatcher.Dispatch(new SelectTopic("topic-1"));
        var reply = new GatedChatStream();
        transport.Answer("SendMessage", _ => reply.Chunks());
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "hello"));
        await TestChat.Eventually(() =>
            client.Streaming.State.StreamingByTopic.GetValueOrDefault("topic-1")?.Content == "thinking");
        var replyEnding = client.Service<TopicStreams>().Snapshot("topic-1").Completion!;

        transport.Raise("OnUserMessage", new UserMessageNotification(
            "topic-1", "and one more thing", "someone-else", DateTimeOffset.UnixEpoch));
        await TestChat.Eventually(() => AssistantMessages(client).Any(m => m.Content == "thinking"));
        reply.Write(new ChatStreamMessage { Content = " and the rest", MessageId = "m-1" });
        reply.Release();

        await replyEnding;
        AssistantMessages(client).ShouldHaveSingleItem().Content.ShouldBe("thinking and the rest");
    }

    // A flaky network can push the same start twice. The topic is claimed before the resume
    // asks the server anything, so the second push finds it taken and the reply is not doubled.
    [Fact]
    public async Task TwoPushedStreamStarts_BackToBack_ResumeTheReplyOnce()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var resumed = new GatedChatStream();
        transport.Answer("GetStreamState", new StreamState(
            true, [new ChatStreamMessage { Content = "half written", MessageId = "m-1" }], "m-1", null, null));
        transport.Answer("ResumeStream", _ => resumed.Chunks());

        transport.Raise("OnStreamStarted", new StreamStartedNotification("topic-1"));
        transport.Raise("OnStreamStarted", new StreamStartedNotification("topic-1"));

        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));
        transport.Calls.Count(call => call.MethodName == "ResumeStream").ShouldBe(1);
        resumed.Release();
    }

    // Between chunks the reply says nothing — a tool call can hold it for minutes — so attaching
    // to the stream is a wait with no end in sight. What the reply has written so far came back
    // from GetStreamState before that wait began, and it belongs on screen then, not whenever
    // the agent next speaks.
    [Fact]
    public async Task AResume_WhileTheReplyIsBetweenChunks_ShowsWhatItHasWrittenWithoutWaiting()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var resumed = new GatedChatStream { Opening = null };
        transport.Answer("GetStreamState", new StreamState(
            true, [new ChatStreamMessage { Content = "half written", MessageId = "m-1" }], "m-1", "hello", null));
        transport.Answer("ResumeStream", _ => resumed.Chunks());

        transport.Raise("OnStreamStarted", new StreamStartedNotification("topic-1"));

        await TestChat.Eventually(() =>
            client.Streaming.State.StreamingByTopic.GetValueOrDefault("topic-1")?.Content == "half written");
        client.Messages.State.MessagesByTopic["topic-1"]
            .ShouldContain(message => message.Role == "user" && message.Content == "hello");
        resumed.Release();
    }

    [Fact]
    public async Task APushedStreamStart_WhenTheServerHasNothingInProgress_LeavesTheTopicIdle()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);

        transport.Raise("OnStreamStarted", new StreamStartedNotification("topic-1"));

        await TestChat.Eventually(() => transport.Calls.Any(call => call.MethodName == "GetStreamState"));
        client.Streaming.State.StreamingTopics.ShouldNotContain("topic-1");
        client.Service<TopicStreams>().Snapshot("topic-1").HasStream.ShouldBeFalse();
    }

    // An approved tool call comes down the topic's stream labelled with the message it belongs to,
    // and the approval's own push says only that the prompt is gone. The reply that follows keeps
    // writing the same message, so the call and the answer stay in one bubble.
    [Fact]
    public async Task AnApprovedToolCall_ArrivingOnTheStream_KeepsTheReplyInOneMessage()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var reply = new GatedChatStream
        {
            Opening = new ChatStreamMessage
            {
                ApprovalRequest = new ToolApprovalRequestMessage(
                    "approval-1", [new ToolApprovalRequest("m-1", "glob", new Dictionary<string, object?>())])
            }
        };
        transport.Answer("SendMessage", _ => reply.Chunks());
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "hello"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));

        var replyEnding = client.Service<TopicStreams>().Snapshot("topic-1").Completion!;
        transport.Raise("OnApprovalResolved", new ApprovalResolvedNotification("topic-1", "approval-1"));
        reply.Write(new ChatStreamMessage { ToolCalls = "glob()", MessageId = "m-1" });
        reply.Write(new ChatStreamMessage { Content = "Done", MessageId = "m-1" });
        reply.Release();

        await replyEnding;
        var message = AssistantMessages(client).ShouldHaveSingleItem();
        message.Content.ShouldBe("Done");
        message.ToolCalls.ShouldBe("glob()");
    }

    private static IReadOnlyList<ChatMessageModel> AssistantMessages(ScriptedChatClient client) =>
        client.Messages.State.MessagesByTopic
            .GetValueOrDefault("topic-1", [])
            .Where(message => message.Role == "assistant")
            .ToList();

    private static void SeedTopic(ScriptedChatClient client)
    {
        client.Dispatcher.Dispatch(new SetAgents([new AgentCatalogEntry("agent-1", "Agent One", null)]));
        client.Dispatcher.Dispatch(new SelectAgent("agent-1"));
        client.Dispatcher.Dispatch(new TopicsLoaded([StoredTopic.FromMetadata(TestChat.Topic("topic-1"))]));
        client.Dispatcher.Dispatch(new MessagesLoaded("topic-1", []));
    }
}