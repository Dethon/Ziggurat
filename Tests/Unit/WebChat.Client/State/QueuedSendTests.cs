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

    // Two messages steered into one reply go in together: the hub writes both where the reply was cut, one
    // after the other, and then their one answer, which the page draws as one bubble under both.
    [Fact]
    public async Task TwoQueuedMessagesWrittenTogether_AreDrawnOneAfterTheOther_ThenTheirOneAnswer()
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
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "the other one"));
        await TestChat.Eventually(() => transport.Calls.Count(call => call.MethodName == "EnqueueMessage") == 1);
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "and in red"));
        await TestChat.Eventually(() => transport.Calls.Count(call => call.MethodName == "EnqueueMessage") == 2);
        var queuedAs = transport.Calls.Where(call => call.MethodName == "EnqueueMessage")
            .Select(call => (string?)call.Arguments[2]).ToList();
        var replyEnding = client.Service<global::WebChat.Client.Services.Streaming.TopicStreams>().Snapshot("topic-1").Completion!;

        reply.Write(new ChatStreamMessage { IsComplete = true, MessageId = "m-1" });
        reply.Write(new ChatStreamMessage
        {
            Content = "the other one", UserMessage = new UserMessageInfo("fran", DateTimeOffset.UtcNow, "Fran", queuedAs[0])
        });
        reply.Write(new ChatStreamMessage
        {
            Content = "and in red", UserMessage = new UserMessageInfo("fran", DateTimeOffset.UtcNow, "Fran", queuedAs[1])
        });
        reply.Write(new ChatStreamMessage { Content = "The other one, ", MessageId = "m-2" });
        reply.Write(new ChatStreamMessage { Content = "in red.", MessageId = "m-2" });
        reply.Write(new ChatStreamMessage { IsComplete = true, MessageId = "m-2" });
        reply.Release();
        await replyEnding;

        Shown(client).ShouldBe([
            "user: first", "assistant: thinking", "user: the other one", "user: and in red", "assistant: The other one, in red."
        ]);
    }

    // One of two messages steered together refused by the agent after the hub drew them: its refusal comes
    // right under the steers, before their one answer, and the page draws it there, the answer after it, as
    // for a steer refused as it arrived.
    [Fact]
    public async Task TwoSteersDrawnTogether_TheSecondRefusedLate_AreDrawnWithTheRefusal_ThenTheAnswer()
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
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "the other one"));
        await TestChat.Eventually(() => transport.Calls.Count(call => call.MethodName == "EnqueueMessage") == 1);
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "and in red"));
        await TestChat.Eventually(() => transport.Calls.Count(call => call.MethodName == "EnqueueMessage") == 2);
        var queuedAs = transport.Calls.Where(call => call.MethodName == "EnqueueMessage")
            .Select(call => (string?)call.Arguments[2]).ToList();
        var replyEnding = client.Service<global::WebChat.Client.Services.Streaming.TopicStreams>().Snapshot("topic-1").Completion!;

        reply.Write(new ChatStreamMessage { IsComplete = true, MessageId = "m-1" });
        reply.Write(new ChatStreamMessage
        {
            Content = "the other one", UserMessage = new UserMessageInfo("fran", DateTimeOffset.UtcNow, "Fran", queuedAs[0])
        });
        reply.Write(new ChatStreamMessage
        {
            Content = "and in red", UserMessage = new UserMessageInfo("fran", DateTimeOffset.UtcNow, "Fran", queuedAs[1])
        });
        reply.Write(new ChatStreamMessage { Error = "You were disabled.", IsComplete = true });
        reply.Write(new ChatStreamMessage { Content = "The other one.", MessageId = "m-2" });
        reply.Write(new ChatStreamMessage { IsComplete = true, MessageId = "m-2" });
        reply.Release();
        await replyEnding;

        Shown(client).ShouldBe([
            "user: first", "assistant: thinking", "user: the other one", "user: and in red",
            "assistant: You were disabled.", "assistant: The other one."
        ]);
        client.Messages.State.MessagesByTopic["topic-1"].Select(m => m.IsError)
            .ShouldBe([false, false, false, false, true, false]);
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

    // Nothing was drawn for it, so its words go back to the composer rather than being lost.
    [Fact]
    public async Task AQueuedSendTheHubRefuses_IsToldAsTheRefusal_AndItsWordsGoBackToTheComposer()
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
        client.Composer.State.Handed.ShouldNotBeNull().Text.ShouldBe("second");
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
            new QueueChip("q1", "second", 2, null, Mine: true, QueueChipActions.Edit | QueueChipActions.Remove),
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

    // A stop as the queued message's turn was about to start hands it back: the page shows its words in
    // the composer, ready to send again, and never as a message that was sent.
    [Fact]
    public async Task AQueuedMessageAStopHandsBack_IsInTheComposer_AndNeverDrawnAsSent()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        client.Dispatcher.Dispatch(new SignedIn(new SignedInPerson("fran", "Fran", null, "home", ["member"])));
        var reply = new GatedChatStream();
        transport.Answer("SendMessage", _ => reply.Chunks());
        transport.Answer("EnqueueMessage", true);
        transport.Answer("CancelTopic", (object?)null);
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "first"));
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "second"));
        await TestChat.Eventually(() => transport.Calls.Any(call => call.MethodName == "EnqueueMessage"));
        var replyEnding = client.Service<global::WebChat.Client.Services.Streaming.TopicStreams>().Snapshot("topic-1").Completion!;
        transport.Answer("TakeReturned", (IReadOnlyList<ReturnedMessage>)[new ReturnedMessage("q1", "second", [], null)]);

        client.Dispatcher.Dispatch(new CancelStreaming("topic-1"));
        reply.Release();
        await replyEnding;
        transport.Raise("OnQueueReturned", new QueueReturnedNotification("topic-1", "default"));

        await TestChat.Eventually(() => client.Composer.State.Handed is not null);
        global::WebChat.Client.State.Composer.ComposerSelectors.Place("", client.Composer.State.Handed!)
            .ShouldBe("second");
        await Eventually.Settle();
        Shown(client).ShouldNotContain("user: second");
        transport.Calls.Count(call => call.MethodName == "SendMessage").ShouldBe(1);
    }

    // A send from a page that had not heard another member's reply start is queued behind it by the hub,
    // and refused as it is queued: the refusal comes first on the send's wire, with no end, and the wire
    // then follows the running reply. The refusal stays outside that reply, which keeps its one bubble.
    [Fact]
    public async Task ASendQueuedBehindAnotherMembersReplyAndRefused_LeavesThatReplyWhole()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        client.Dispatcher.Dispatch(new SignedIn(new SignedInPerson("fran", "Fran", null, "home", ["member"])));
        var wire = new GatedChatStream { Opening = null };
        transport.Answer("SendMessage", _ => wire.Chunks());
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "second"));
        await TestChat.Eventually(() => transport.Calls.Any(call => call.MethodName == "SendMessage"));

        wire.Write(new ChatStreamMessage { Error = "This device was signed out" });
        await TestChat.Eventually(() => client.Streaming.State.StreamingTopics.Contains("topic-1"));
        var replyEnding = client.Service<global::WebChat.Client.Services.Streaming.TopicStreams>().Snapshot("topic-1").Completion!;
        wire.Write(new ChatStreamMessage
        {
            Content = "first", UserMessage = new UserMessageInfo("marta", DateTimeOffset.UtcNow, "Marta", "marta-1")
        });
        wire.Write(new ChatStreamMessage { Content = "thinking", MessageId = "m-1" });
        wire.Write(new ChatStreamMessage { Content = " and done", MessageId = "m-1" });
        wire.Write(new ChatStreamMessage { IsComplete = true, MessageId = "m-1" });
        wire.Release();
        await replyEnding;

        Shown(client).ShouldBe([
            "user: second", "assistant: This device was signed out", "user: first", "assistant: thinking and done"
        ]);
    }

    // The same refusal on a send that lost the topic to a resume already reading the other member's reply:
    // only the refusal is shown off the losing wire, and it is the send's, so it is drawn under the send's
    // own message, never between the other member's message and the reply to it.
    [Fact]
    public async Task ASendQueuedAndRefusedWhileAResumeReadsAnotherMembersReply_LeavesThatReplyWhole()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        client.Dispatcher.Dispatch(new SignedIn(new SignedInPerson("fran", "Fran", null, "home", ["member"])));
        var wire = new GatedChatStream { Opening = null };
        var resumed = new GatedChatStream { Opening = null };
        transport.Answer("SendMessage", _ => wire.Chunks());
        transport.Answer("GetStreamState", new StreamState(
            true, [new ChatStreamMessage { Content = "thinking", MessageId = "m-1" }], "m-1", "first", "marta"));
        transport.Answer("ResumeStreamFrom", _ => resumed.Chunks());
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "second"));
        await TestChat.Eventually(() => transport.Calls.Any(call => call.MethodName == "SendMessage"));

        transport.Raise("OnStreamStarted", new StreamStartedNotification("topic-1"));
        await TestChat.Eventually(() =>
            client.Streaming.State.StreamingByTopic.GetValueOrDefault("topic-1")?.Content == "thinking");
        var replyEnding = client.Service<global::WebChat.Client.Services.Streaming.TopicStreams>().Snapshot("topic-1").Completion!;
        wire.Write(new ChatStreamMessage { Error = "This device was signed out" });
        wire.Release();
        // The refusal drawn, not just its toast, which is dispatched first: the losing wire is done
        // before the reply goes on, as the page's one thread would have it.
        await TestChat.Eventually(() => Shown(client).Contains("assistant: This device was signed out"));
        resumed.Write(new ChatStreamMessage { Content = " and done", MessageId = "m-1" });
        resumed.Write(new ChatStreamMessage { IsComplete = true, MessageId = "m-1" });
        resumed.Release();
        await replyEnding;

        Shown(client).ShouldBe([
            "user: second", "assistant: This device was signed out", "user: first", "assistant: thinking and done"
        ]);
    }

    // A hub that says the refusal is of the send itself (queued behind a reply the page had not heard
    // start, and refused as it was queued): the page drew the message as sent, so it takes it off again,
    // tells the refusal and puts its words and files back in the composer, as for one EnqueueMessage
    // refused. The other member's reply is left whole, read on the send's own wire or on a resume's.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASendRefusedAsItWasQueued_IsTakenOffThePage_AndItsWordsAndFilesGoBackToTheComposer(bool resumeHoldsTheTopic)
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        client.Dispatcher.Dispatch(new SignedIn(new SignedInPerson("fran", "Fran", null, "home", ["member"])));
        var wire = new GatedChatStream { Opening = null };
        var reply = resumeHoldsTheTopic ? new GatedChatStream { Opening = null } : wire;
        transport.Answer("SendMessage", _ => wire.Chunks());
        transport.Answer("GetStreamState", new StreamState(
            true, [new ChatStreamMessage { Content = "thinking", MessageId = "m-1" }], "m-1", "first", "marta"));
        transport.Answer("ResumeStreamFrom", _ => reply.Chunks());
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "second", [Photo]));
        await TestChat.Eventually(() => transport.Calls.Any(call => call.MethodName == "SendMessage"));
        Shown(client).ShouldBe(["user: second"]);
        if (resumeHoldsTheTopic)
        {
            transport.Raise("OnStreamStarted", new StreamStartedNotification("topic-1"));
            await TestChat.Eventually(() =>
                client.Streaming.State.StreamingByTopic.GetValueOrDefault("topic-1")?.Content == "thinking");
        }

        wire.Write(new ChatStreamMessage { Error = "This device was signed out", RefusedSend = true });
        await TestChat.Eventually(() => client.Composer.State.Handed is not null);
        var replyEnding = client.Service<global::WebChat.Client.Services.Streaming.TopicStreams>().Snapshot("topic-1").Completion!;
        if (!resumeHoldsTheTopic)
        {
            reply.Write(new ChatStreamMessage
            {
                Content = "first", UserMessage = new UserMessageInfo("marta", DateTimeOffset.UtcNow, "Marta", "marta-1")
            });
            reply.Write(new ChatStreamMessage { Content = "thinking", MessageId = "m-1" });
        }
        else
        {
            wire.Release();
        }

        reply.Write(new ChatStreamMessage { Content = " and done", MessageId = "m-1" });
        reply.Write(new ChatStreamMessage { IsComplete = true, MessageId = "m-1" });
        reply.Release();
        await replyEnding;

        Shown(client).ShouldNotContain("user: second");
        Shown(client).ShouldNotContain("assistant: This device was signed out");
        Shown(client).Last().ShouldBe("assistant: thinking and done");
        client.Toasts.State.Toasts.ShouldContain(t => t.Message.Contains("signed out"));
        global::WebChat.Client.State.Composer.ComposerSelectors.Place("", client.Composer.State.Handed!).ShouldBe("second");
        client.Composer.State.For("topic-1").ShouldHaveSingleItem().Reference.ShouldBe(Photo);
    }

    // A send the page drew as sent that the hub then hands back, a stop having landed as it was queued or
    // vouched for: the hub tells the sender's every page which sends those were, so the page that drew it
    // takes it off again, whichever of the person's pages takes the words into its composer.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASendTheHubHandsBack_IsTakenOffThePageThatDrewIt(bool takenHere)
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client);
        client.Dispatcher.Dispatch(new SignedIn(new SignedInPerson("fran", "Fran", null, "home", ["member"])));
        var wire = new GatedChatStream { Opening = null };
        transport.Answer("SendMessage", _ => wire.Chunks());
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "second"));
        await TestChat.Eventually(() => transport.Calls.Any(call => call.MethodName == "SendMessage"));
        var sentAs = (string)transport.Calls.Single(call => call.MethodName == "SendMessage").Arguments[2]!;
        Shown(client).ShouldBe(["user: second"]);
        transport.Answer("TakeReturned", (IReadOnlyList<ReturnedMessage>)(takenHere
            ? [new ReturnedMessage("q1", "second", [], null, sentAs)]
            : []));

        wire.Release();
        transport.Raise("OnQueueReturned", new QueueReturnedNotification("topic-1", "default", [sentAs]));

        await TestChat.Eventually(() => !Shown(client).Contains("user: second"));
        if (takenHere)
        {
            await TestChat.Eventually(() => client.Composer.State.Handed is not null);
            global::WebChat.Client.State.Composer.ComposerSelectors.Place("", client.Composer.State.Handed!).ShouldBe("second");
        }
    }

    // Retry on a refusal that is not the last bubble sends the refused message again as a new one, at the
    // end, where a message sent now goes: what followed it stays as it was, and so does the refused
    // message, as the conversation keeps it (a failed turn stays where it was when another follows it).
    [Fact]
    public async Task RetryingARefusalThatIsNotTheLastBubble_SendsItsMessageAgain_AtTheEnd()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        SeedTopic(client,
        [
            new ChatHistoryMessage("u1", "user", "second", "fran", DateTimeOffset.UnixEpoch, Attachments: [Photo]),
            new ChatHistoryMessage(null, "assistant", "This device was signed out", null, DateTimeOffset.UnixEpoch, IsError: true),
            new ChatHistoryMessage("u2", "user", "first", "marta", DateTimeOffset.UnixEpoch),
            new ChatHistoryMessage("a2", "assistant", "done", null, DateTimeOffset.UnixEpoch)
        ]);
        client.Dispatcher.Dispatch(new SignedIn(new SignedInPerson("fran", "Fran", null, "home", ["member"])));
        var wire = new GatedChatStream { Opening = null };
        transport.Answer("SendMessage", _ => wire.Chunks());
        var refused = client.Messages.State.MessagesByTopic["topic-1"][0];

        client.Dispatcher.Dispatch(new RetryRefused("topic-1", refused));

        await TestChat.Eventually(() => transport.Calls.Any(call => call.MethodName == "SendMessage"));
        var sent = transport.Calls.Single(call => call.MethodName == "SendMessage");
        sent.Arguments[1].ShouldBe("second");
        ((IEnumerable<AttachmentReference>)sent.Arguments[4]!).ShouldBe([Photo]);
        Shown(client).ShouldBe([
            "user: second", "assistant: This device was signed out", "user: first", "assistant: done", "user: second"
        ]);
        wire.Release();
    }

    private static readonly AttachmentReference Photo = new()
    {
        Id = "7-42/abc",
        FileName = "photo.png",
        MediaType = "image/png",
        SizeBytes = 4
    };

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