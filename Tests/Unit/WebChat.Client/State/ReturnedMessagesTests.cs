using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace Tests.Unit.WebChat.Client.State;

// Messages a stop handed back to the person who queued them land in their composer on that topic, from
// whichever page of theirs takes them first: one showing the topic when told, or the next to open it.
public sealed class ReturnedMessagesTests
{
    private static readonly AttachmentReference Photo = new()
    {
        Id = "7-42/abc",
        FileName = "photo.png",
        MediaType = "image/png",
        SizeBytes = 4
    };

    [Fact]
    public async Task ToldWhileShowingTheTopic_TakesThem_AndPutsThemInTheComposer()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        Seed(client, open: "topic-1");
        transport.Answer("TakeReturned", (IReadOnlyList<ReturnedMessage>)
        [
            new ReturnedMessage("q1", "first queued", [Photo], null),
            new ReturnedMessage("q2", "second queued", [], null)
        ]);

        transport.Raise("OnQueueReturned", new QueueReturnedNotification("topic-1", "default"));

        await TestChat.Eventually(() => client.Composer.State.Dictation.Transcript is not null);
        ComposerSelectors.Place("typed", client.Composer.State.Dictation.Transcript!)
            .ShouldBe("first queued\n\nsecond queued\n\ntyped");
        var file = client.Composer.State.For("topic-1").ShouldHaveSingleItem();
        (file.Status, file.Reference).ShouldBe((AttachmentStatus.Ready, Photo));
        transport.Calls.ShouldContain(call =>
            call.MethodName == "TakeReturned" && call.Arguments.SequenceEqual(new object?[] { "topic-1" }));
    }

    // Another topic's composer is no place for them, and taking them there would hand them to the wrong
    // conversation: they wait at the hub until the topic is opened, here or on another device.
    [Fact]
    public async Task ToldWhileShowingAnotherTopic_TakesNothing_UntilThatTopicIsOpened()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        Seed(client, open: "topic-2");
        await TestChat.Eventually(() => transport.Calls.Any(call => call.MethodName == "GetHistory"));
        var asked = new List<object?>();
        transport.Answer("TakeReturned", args =>
        {
            lock (asked)
            {
                asked.Add(args[0]);
            }

            return (IReadOnlyList<ReturnedMessage>)[new ReturnedMessage("q1", "queued on one", [], null)];
        });

        transport.Raise("OnQueueReturned", new QueueReturnedNotification("topic-1", "default"));
        await Eventually.Settle();
        lock (asked)
        {
            asked.ShouldNotContain("topic-1");
        }

        client.Composer.State.Dictation.Transcript.ShouldBeNull();

        client.Dispatcher.Dispatch(new SelectTopic("topic-1"));

        await TestChat.Eventually(() => client.Composer.State.Dictation.Transcript is not null);
        ComposerSelectors.Place("", client.Composer.State.Dictation.Transcript!).ShouldBe("queued on one");
    }

    // A phone asleep through the stop missed the telling, and wakes on the topic it already shows.
    [Fact]
    public async Task BecomingLiveAgainOnTheTopic_TakesWhatWasReturnedMeanwhile()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        Seed(client, open: "topic-1");
        await TestChat.Eventually(() => transport.Calls.Any(call => call.MethodName == "TakeReturned"));
        transport.Answer("TakeReturned", (IReadOnlyList<ReturnedMessage>)[new ReturnedMessage("q1", "queued while asleep", [], null)]);

        await transport.RaiseReconnectingAsync(null);
        await transport.RaiseReconnectedAsync("connection-2");

        await TestChat.Eventually(() => client.Composer.State.Dictation.Transcript is not null);
        ComposerSelectors.Place("", client.Composer.State.Dictation.Transcript!).ShouldBe("queued while asleep");
    }

    private static void Seed(ScriptedChatClient client, string open)
    {
        client.Dispatcher.Dispatch(new SignedIn(new SignedInPerson("fran", "Fran", null, "home", ["member"])));
        client.Dispatcher.Dispatch(new SetAgents([new AgentCatalogEntry("agent-1", "Agent One", null)]));
        client.Dispatcher.Dispatch(new SelectAgent("agent-1"));
        client.Dispatcher.Dispatch(new TopicsLoaded(
            [StoredTopic.FromMetadata(TestChat.Topic("topic-1")), StoredTopic.FromMetadata(TestChat.Topic("topic-2"))]));
        client.Dispatcher.Dispatch(new SelectTopic(open));
    }
}