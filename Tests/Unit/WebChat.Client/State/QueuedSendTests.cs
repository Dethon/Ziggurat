using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Extensions;
using WebChat.Client.Models;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Queue;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace Tests.Unit.WebChat.Client.State;

// A message sent while a reply streams is queued by the hub rather than drawn: it shows as a chip
// above the composer until its turn starts, when the hub writes it on the stream and the page draws it
// there, after the reply it followed.
public sealed class QueuedSendTests
{
    [Fact]
    public async Task ASendWhileAReplyStreams_IsQueued_DrawsNothing_AndLeavesTheReplyBubbleOpen()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var reply = new GatedChatStream();
        transport.Answer("SendMessage", _ => reply.Chunks());
        transport.Answer("EnqueueMessage", true);
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "first"));
        await TestChat.Eventually(() =>
            client.Streaming.State.StreamingByTopic.GetValueOrDefault("topic-1")?.Content == "thinking");

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "second"));

        await TestChat.Eventually(() => transport.Calls.Any(call => call.MethodName == "EnqueueMessage"));
        transport.Calls.Count(call => call.MethodName == "SendMessage").ShouldBe(1);
        Shown(client).ShouldBe(["user: first"]);
        client.Streaming.State.StreamingByTopic["topic-1"].Content.ShouldBe("thinking");
        reply.Release();
    }

    [Fact]
    public async Task AQueuedMessage_IsDrawnOnce_WhenTheHubWritesItOnTheStream_AfterTheReplyItFollowed()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        client.Dispatcher.Dispatch(new SignedIn(new SignedInPerson("fran", "Fran", null, "home", ["member"])));
        var reply = new GatedChatStream();
        transport.Answer("SendMessage", _ => reply.Chunks());
        transport.Answer("EnqueueMessage", true);
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "first"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "ok"));
        await TestChat.Eventually(() => transport.Calls.Any(call => call.MethodName == "EnqueueMessage"));
        var queuedAs = (string?)transport.Calls.Single(call => call.MethodName == "EnqueueMessage").Arguments[2];
        var replyEnding = client.Service<global::WebChat.Client.Services.Streaming.TopicStreams>().Snapshot("topic-1").Completion!;

        reply.Write(new ChatStreamMessage { Content = " done", MessageId = "m-1" });
        reply.Write(new ChatStreamMessage { IsComplete = true, MessageId = "m-1" });
        reply.Write(new ChatStreamMessage
        {
            Content = "ok", UserMessage = new UserMessageInfo("fran", DateTimeOffset.UtcNow, "Fran", queuedAs)
        });
        reply.Write(new ChatStreamMessage { Content = "Noted.", MessageId = "m-2" });
        reply.Release();
        await replyEnding;

        Shown(client).ShouldBe(["user: first", "assistant: thinking done", "user: ok", "assistant: Noted."]);
    }

    // The same words sent earlier and loaded with the history carry no send's id; a queued message is
    // known by its own, so it is drawn even when its words repeat one already on the page.
    [Fact]
    public async Task AQueuedMessageRepeatingWordsAlreadyOnThePage_IsStillDrawn()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client, [new ChatHistoryMessage("u0", "user", "ok", "fran", DateTimeOffset.UnixEpoch)]);
        client.Dispatcher.Dispatch(new SignedIn(new SignedInPerson("fran", "Fran", null, "home", ["member"])));
        var reply = new GatedChatStream();
        transport.Answer("SendMessage", _ => reply.Chunks());
        transport.Answer("EnqueueMessage", true);
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "first"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "ok"));
        await TestChat.Eventually(() => transport.Calls.Any(call => call.MethodName == "EnqueueMessage"));
        var queuedAs = (string?)transport.Calls.Single(call => call.MethodName == "EnqueueMessage").Arguments[2];
        var replyEnding = client.Service<global::WebChat.Client.Services.Streaming.TopicStreams>().Snapshot("topic-1").Completion!;

        reply.Write(new ChatStreamMessage { IsComplete = true, MessageId = "m-1" });
        reply.Write(new ChatStreamMessage
        {
            Content = "ok", UserMessage = new UserMessageInfo("fran", DateTimeOffset.UtcNow, "Fran", queuedAs)
        });
        reply.Release();
        await replyEnding;

        Shown(client).Count(line => line == "user: ok").ShouldBe(2);
    }

    // The hub said nothing is running after all: the page sends it as a new message, drawn as one.
    [Fact]
    public async Task AQueuedSendFindingNothingRunning_IsSentAndDrawnAsANewMessage()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var older = new GatedChatStream();
        var newer = new GatedChatStream();
        var opened = 0;
        transport.Answer("SendMessage", _ => opened++ == 0 ? older.Chunks() : newer.Chunks());
        transport.Answer("EnqueueMessage", false);
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "first"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "second"));

        await TestChat.Eventually(() => transport.Calls.Count(call => call.MethodName == "SendMessage") == 2);
        await TestChat.Eventually(() => Shown(client).Contains("user: second"));
        Shown(client).ShouldBe(["user: first", "assistant: thinking", "user: second"]);
        older.Release();
        newer.Release();
    }

    [Fact]
    public async Task AQueuedSendTheHubRefuses_IsToldAsTheRefusal()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        var reply = new GatedChatStream();
        transport.Answer("SendMessage", _ => reply.Chunks());
        transport.Answer("EnqueueMessage", (Func<object?[], object?>)(_ =>
            throw new HubException("An unexpected error occurred invoking 'EnqueueMessage' on the server. HubException: This device was signed out")));
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "first"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "second"));

        await TestChat.Eventually(() => client.Toasts.State.Toasts.Any(t => t.Message.Contains("signed out")));
        Shown(client).ShouldBe(["user: first"]);
        reply.Release();
    }

    [Fact]
    public async Task TheQueue_ShowsAsChipsInTheOrderItWillRun_AndLosesEachAsItIsSent()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        client.Dispatcher.Dispatch(new SignedIn(new SignedInPerson("fran", "Fran", null, "home", ["member"])));

        transport.Raise("OnQueueChanged", new QueueChangedNotification("topic-1",
        [
            new QueuedMessageItem("q1", "second", 2, "fran", "Fran", DateTimeOffset.UnixEpoch),
            new QueuedMessageItem("q2", "third, from Marta", 0, "marta", "Marta", DateTimeOffset.UnixEpoch)
        ], Revision: 5));

        Chips(client).ShouldBe([
            new QueueChip("q1", "second", 2, null, Mine: true),
            new QueueChip("q2", "third, from Marta", 0, "Marta", Mine: false)
        ]);

        transport.Raise("OnQueueChanged", new QueueChangedNotification("topic-1",
            [new QueuedMessageItem("q2", "third, from Marta", 0, "marta", "Marta", DateTimeOffset.UnixEpoch)],
            Revision: 7));
        transport.Raise("OnQueueChanged", new QueueChangedNotification("topic-1",
        [
            new QueuedMessageItem("q1", "second", 2, "fran", "Fran", DateTimeOffset.UnixEpoch),
            new QueuedMessageItem("q2", "third, from Marta", 0, "marta", "Marta", DateTimeOffset.UnixEpoch)
        ], Revision: 6));

        Chips(client).Select(chip => chip.Id).ShouldBe(["q2"]);

        transport.Raise("OnQueueChanged", new QueueChangedNotification("topic-1", [], Revision: 8));
        Chips(client).ShouldBeEmpty();
    }

    private static IReadOnlyList<QueueChip> Chips(ScriptedChatClient client) =>
        QueueSelectors.Chips(client.Service<QueueStore>().State, "topic-1", client.UserIdentity.State.SignedIn?.UserId);

    private static IReadOnlyList<string> Shown(ScriptedChatClient client) =>
    [
        .. client.Messages.State.MessagesByTopic.GetValueOrDefault("topic-1", [])
            .Select(message => $"{message.Role}: {message.Content}")
    ];

    private static void SeedTopic(ScriptedChatClient client, IReadOnlyList<ChatHistoryMessage>? history = null)
    {
        client.Dispatcher.Dispatch(new SetAgents([new AgentCatalogEntry("agent-1", "Agent One", null)]));
        client.Dispatcher.Dispatch(new SelectAgent("agent-1"));
        client.Dispatcher.Dispatch(new TopicsLoaded([StoredTopic.FromMetadata(TestChat.Topic("topic-1"))]));
        client.Dispatcher.Dispatch(new SelectTopic("topic-1"));
        client.Dispatcher.Dispatch(new MessagesLoaded("topic-1",
            [.. (history ?? []).Select(h => h.ToChatMessageModel())]));
    }
}