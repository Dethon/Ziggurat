using System.Net.WebSockets;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Space;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

// A call the user initiated raises one error toast when it could not be made. The toast store
// suppresses a repeat of the same message, so a resume with several failures is one toast.
public sealed class NotLiveUserActionTests
{
    [Fact]
    public async Task AMessageTyped_WhileNotLive_RaisesOneToastAndAddsNoMessage()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        SeedTopic(client);

        client.GoNotLive();
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "hello"));

        await TestChat.Eventually(() => client.Toasts.State.Toasts.Count == 1);
        client.Messages.State.MessagesByTopic.GetValueOrDefault("topic-1", []).ShouldBeEmpty();
    }

    [Fact]
    public async Task ANewConversation_StartedWhileNotLive_RaisesOneToastAndAddsNoTopic()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        client.Dispatcher.Dispatch(new SetAgents([new AgentCatalogEntry("agent-1", "Agent One", null)]));
        client.Dispatcher.Dispatch(new SelectAgent("agent-1"));

        client.GoNotLive();
        client.Dispatcher.Dispatch(new SendMessage(null, "hello"));

        await TestChat.Eventually(() => client.Toasts.State.Toasts.Count == 1);
        client.Topics.State.Topics.ShouldBeEmpty();
    }

    // The second half of the same defect: the send goes through the enqueue call, and a false
    // from that means "there is no stream to enqueue onto". Not live must not read as false,
    // or the client opens a stream that has already failed and announces it.
    [Fact]
    public async Task AnEnqueue_ThatCouldNotBeMade_OpensNoNewStream()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var stream = new GatedChatStream();
        transport.Answer("SendMessage", _ => stream.Chunks());

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "first"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));

        client.GoNotLive();
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "second"));

        await TestChat.Eventually(() => client.Toasts.State.Toasts.Count == 1);
        transport.Calls.Count(call => call.MethodName == "SendMessage").ShouldBe(1);
        stream.Release();
    }

    [Fact]
    public async Task AServerAnsweringNoToTheEnqueue_StillOpensANewStream()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var stream = new GatedChatStream();
        transport.Answer("SendMessage", _ => stream.Chunks());
        transport.Answer("EnqueueMessage", false);

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "first"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "second"));

        await TestChat.Eventually(() => transport.Calls.Count(call => call.MethodName == "SendMessage") == 2);
        client.Toasts.State.Toasts.ShouldBeEmpty();
        stream.Release();
    }

    [Fact]
    public async Task AServerAnsweringYesToTheEnqueue_StillOpensNoNewStream()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var stream = new GatedChatStream();
        transport.Answer("SendMessage", _ => stream.Chunks());
        transport.Answer("EnqueueMessage", true);

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "first"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "second"));

        await TestChat.Eventually(() => transport.Calls.Any(call => call.MethodName == "EnqueueMessage"));
        transport.Calls.Count(call => call.MethodName == "SendMessage").ShouldBe(1);
        client.Toasts.State.Toasts.ShouldBeEmpty();
        stream.Release();
    }

    // A hub stream touches the wire on its first pull, not when it is asked for, so a transport
    // that is already dead faults there. That is the same not-live window as any other call:
    // one toast, and no raw SignalR message left in the transcript as an error bubble.
    [Fact]
    public async Task ASend_WhoseStreamDiesOnOpen_RaisesTheNotLiveToastAndAddsNoErrorBubble()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var stream = new GatedChatStream
        {
            FaultOnOpen = new WebSocketException(WebSocketError.ConnectionClosedPrematurely)
        };
        transport.Answer("SendMessage", _ => stream.Chunks());

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "hello"));

        await TestChat.Eventually(() => client.Toasts.State.Toasts.Count == 1);
        client.Toasts.State.Toasts.Single().Message.ShouldBe(NotLiveToast.Message);
        client.Messages.State.MessagesByTopic
            .GetValueOrDefault("topic-1", [])
            .ShouldNotContain(message => message.IsError);
    }

    [Fact]
    public async Task AMessage_SentWhileLive_StillReachesTheServerAndTheTranscript()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        transport.Answer("SendMessage", _ => Chunks(new ChatStreamMessage { Content = "hi", IsComplete = true }));

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "hello"));

        await TestChat.Eventually(() =>
            client.Messages.State.MessagesByTopic.GetValueOrDefault("topic-1", []).Any(m => m.Role == "user"));
        client.Toasts.State.Toasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task ADelete_ThatCouldNotBeMade_LeavesTheConversationInTheSidebar()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        SeedTopic(client);

        client.GoNotLive();
        client.Dispatcher.Dispatch(new RemoveTopic("topic-1", "agent-1", 10, 20));

        await TestChat.Eventually(() => client.Toasts.State.Toasts.Count == 1);
        await TestChat.Eventually(() => client.Topics.State.Topics.Any(topic => topic.TopicId == "topic-1"));
        client.Topics.State.Topics.Single().Name.ShouldBe("Topic");
    }

    [Fact]
    public async Task ADelete_OfTheOpenConversation_ThatCouldNotBeMade_LeavesItSelected()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        SeedTopic(client);
        client.Dispatcher.Dispatch(new SelectTopic("topic-1"));

        client.GoNotLive();
        client.Dispatcher.Dispatch(new RemoveTopic("topic-1", "agent-1", 10, 20));

        await TestChat.Eventually(() => client.Topics.State.SelectedTopicId == "topic-1");
        client.Topics.State.Topics.Single().TopicId.ShouldBe("topic-1");
    }

    // The transport can die during the round trip: the state check passed, the invoke throws.
    // That is the same not-live window, so the user gets the same toast and keeps the row.
    [Fact]
    public async Task ADelete_WhoseTransportDiesMidCall_RaisesTheToastAndLeavesTheRow()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        transport.Answer("DeleteTopic", _ => throw new InvalidOperationException(
            "The 'InvokeCoreAsync' method cannot be called if the connection is not active"));

        client.Dispatcher.Dispatch(new RemoveTopic("topic-1", "agent-1", 10, 20));

        await TestChat.Eventually(() => client.Toasts.State.Toasts.Count == 1);
        client.Topics.State.Topics.Single().TopicId.ShouldBe("topic-1");
    }

    [Fact]
    public async Task ADelete_WhileLive_StillRemovesTheConversation()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);

        client.Dispatcher.Dispatch(new RemoveTopic("topic-1", "agent-1", 10, 20));

        await TestChat.Eventually(() => transport.Calls.Any(call => call.MethodName == "DeleteTopic"));
        await TestChat.Eventually(() => client.Topics.State.Topics.Count == 0);
        client.Toasts.State.Toasts.ShouldBeEmpty();
    }

    [Fact]
    public async Task ACancel_ThatCouldNotBeMade_RaisesOneToastAndLeavesTheReplyRunning()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var stream = new GatedChatStream();
        transport.Answer("SendMessage", _ => stream.Chunks());
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "first"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));

        client.GoNotLive();
        client.Dispatcher.Dispatch(new CancelStreaming("topic-1"));

        await TestChat.Eventually(() => client.Toasts.State.Toasts.Count == 1);
        client.Streaming.State.StreamingTopics.ShouldContain("topic-1");
        stream.Release();
    }

    [Fact]
    public async Task ACancel_WhileLive_StillStopsTheReply()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var stream = new GatedChatStream();
        transport.Answer("SendMessage", _ => stream.Chunks());
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "first"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));

        var replyEnding = client.Service<TopicStreams>().Snapshot("topic-1").Completion!;
        client.Dispatcher.Dispatch(new CancelStreaming("topic-1"));

        await replyEnding;
        client.Streaming.State.StreamingTopics.ShouldNotContain("topic-1");
        client.Toasts.State.Toasts.ShouldBeEmpty();
        stream.Release();
    }

    [Fact]
    public async Task AnApprovalAnswered_WhileNotLive_LeavesThePromptOnScreen()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        client.Dispatcher.Dispatch(new ShowApproval("topic-1", new ToolApprovalRequestMessage("approval-1", [])));

        client.GoNotLive();
        await client.Service<ApprovalResponder>().RespondAsync("approval-1", ToolApprovalResult.Approved);

        client.Approvals.State.CurrentRequest?.ApprovalId.ShouldBe("approval-1");
        client.Toasts.State.Toasts.Count.ShouldBe(1);
    }

    // A server that refuses is live and has answered — the approval is no longer pending, so
    // the prompt goes away exactly as it does today.
    [Fact]
    public async Task AnApprovalTheServerRefuses_StillClearsThePrompt()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        client.Dispatcher.Dispatch(new ShowApproval("topic-1", new ToolApprovalRequestMessage("approval-1", [])));
        transport.Answer("RespondToApprovalAsync", false);

        await client.Service<ApprovalResponder>().RespondAsync("approval-1", ToolApprovalResult.Approved);

        client.Approvals.State.CurrentRequest.ShouldBeNull();
        client.Toasts.State.Toasts.ShouldBeEmpty();
    }

    // Switching space is the user's own action. The join is what puts the browser in the
    // server's space group, so a not-live join must not clear the sidebar and validate a space
    // the server never joined — it says so and leaves the current space in place.
    [Fact]
    public async Task ASpaceSwitch_ThatCouldNotBeMade_RaisesOneToastAndKeepsTheCurrentSpace()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        SeedTopic(client);

        client.GoNotLive();
        await client.Service<SpaceEffect>().HandleSelectSpaceAsync("hearth");

        client.Toasts.State.Toasts.Count.ShouldBe(1);
        client.Topics.State.Topics.ShouldNotBeEmpty();
        client.Space.State.SpaceName.ShouldBe(SpaceState.Initial.SpaceName);
    }

    // Tapping a conversation is the user's own action too: a session that could not be started
    // opens a thread that answers nothing, so it says so and loads nothing.
    [Fact]
    public async Task AConversationTapped_WhileNotLive_RaisesOneToastAndLoadsNoHistory()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        client.Dispatcher.Dispatch(new SetAgents([new AgentCatalogEntry("agent-1", "Agent One", null)]));
        client.Dispatcher.Dispatch(new SelectAgent("agent-1"));
        client.Dispatcher.Dispatch(new TopicsLoaded([StoredTopic.FromMetadata(TestChat.Topic("topic-1"))]));

        client.GoNotLive();
        client.Dispatcher.Dispatch(new SelectTopic("topic-1"));

        await TestChat.Eventually(() => client.Toasts.State.Toasts.Count == 1);
        client.Messages.State.MessagesByTopic.ShouldNotContainKey("topic-1");
    }

    [Fact]
    public async Task TwoFailedUserActions_InTheSameWindow_ProduceOneToast()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        SeedTopic(client);

        client.GoNotLive();
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "hello"));
        await TestChat.Eventually(() => client.Toasts.State.Toasts.Count == 1);
        client.Dispatcher.Dispatch(new RemoveTopic("topic-1", "agent-1", 10, 20));

        await TestChat.Eventually(() => client.Topics.State.Topics.Any(topic => topic.TopicId == "topic-1"));
        client.Toasts.State.Toasts.Count.ShouldBe(1);
    }

    private static void SeedTopic(ScriptedChatClient client)
    {
        client.Dispatcher.Dispatch(new SetAgents([new AgentCatalogEntry("agent-1", "Agent One", null)]));
        client.Dispatcher.Dispatch(new SelectAgent("agent-1"));
        client.Dispatcher.Dispatch(new TopicsLoaded([StoredTopic.FromMetadata(TestChat.Topic("topic-1"))]));
        client.Dispatcher.Dispatch(new MessagesLoaded("topic-1", []));
    }

    private static async IAsyncEnumerable<ChatStreamMessage> Chunks(params ChatStreamMessage[] chunks)
    {
        foreach (var chunk in chunks)
        {
            yield return chunk;
            await Task.Yield();
        }
    }
}