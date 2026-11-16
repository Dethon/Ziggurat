using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Queue;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace Tests.Unit.WebChat.Client.State;

// A person's own queued message, withdrawn from its chip before its turn starts: removed, or taken back
// into the composer to be reworked and sent again.
public sealed class QueuedWithdrawalTests
{
    private static readonly AttachmentReference Photo = new()
    {
        Id = "7-42/abc",
        FileName = "photo.png",
        MediaType = "image/png",
        SizeBytes = 4
    };

    [Fact]
    public async Task Edit_PutsTheMessagesWordsBeforeWhatTheComposerHolds_ItsFilesReady_AndTheChipGoes()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        Seed(client);
        transport.Raise("OnQueueChanged", new QueueChangedNotification("topic-1",
            [new QueuedMessageItem("q1", "the queued words", 1, "fran", "Fran", DateTimeOffset.UnixEpoch)], Revision: 5));
        transport.Answer("TakeBackQueued", new ReturnedMessage("q1", "the queued words", [Photo], null));

        client.Dispatcher.Dispatch(new EditQueuedMessage("topic-1", "q1"));

        await TestChat.Eventually(() => client.Composer.State.For("topic-1").Count == 1);
        var file = client.Composer.State.For("topic-1").ShouldHaveSingleItem();
        (file.Status, file.Reference, file.FileName, file.MediaType).ShouldBe(
            (AttachmentStatus.Ready, Photo, "photo.png", "image/png"));
        var returned = client.Composer.State.Dictation.Transcript.ShouldNotBeNull();
        ComposerSelectors.Place("half a thought", returned).ShouldBe("the queued words\n\nhalf a thought");
        ComposerSelectors.Place("", returned).ShouldBe("the queued words");
        Chips(client).ShouldBeEmpty();
    }

    [Fact]
    public async Task Remove_AsksTheHubToRemoveIt_AndTheChipGoes_LeavingTheComposerAlone()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        Seed(client);
        transport.Raise("OnQueueChanged", new QueueChangedNotification("topic-1",
            [new QueuedMessageItem("q1", "the queued words", 1, "fran", "Fran", DateTimeOffset.UnixEpoch)], Revision: 5));
        transport.Answer("RemoveQueued", (object?)null);

        client.Dispatcher.Dispatch(new RemoveQueuedMessage("topic-1", "q1"));

        await TestChat.Eventually(() => Chips(client).Count == 0);
        transport.Calls.ShouldContain(call =>
            call.MethodName == "RemoveQueued" && call.Arguments.SequenceEqual(new object?[] { "topic-1", "q1" }));
        client.Composer.State.For("topic-1").ShouldBeEmpty();
        client.Composer.State.Dictation.Transcript.ShouldBeNull();
    }

    // Its turn started first: the person is told, and is not shown a withdrawal that did not happen.
    [Fact]
    public async Task EditingAMessageAlreadySent_IsToldSo_AndPutsNothingInTheComposer()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        Seed(client);
        transport.Raise("OnQueueChanged", new QueueChangedNotification("topic-1",
            [new QueuedMessageItem("q1", "the queued words", 1, "fran", "Fran", DateTimeOffset.UnixEpoch)], Revision: 5));
        transport.Answer("TakeBackQueued", (Func<object?[], object?>)(_ => throw new HubException(
            $"An unexpected error occurred invoking 'TakeBackQueued' on the server. HubException: {TopicRefusals.AlreadySent}")));

        client.Dispatcher.Dispatch(new EditQueuedMessage("topic-1", "q1"));

        await TestChat.Eventually(() => client.Toasts.State.Toasts.Any(t => t.Message == TopicRefusals.AlreadySent));
        client.Composer.State.For("topic-1").ShouldBeEmpty();
        client.Composer.State.Dictation.Transcript.ShouldBeNull();
    }

    // Everyone on a shared topic sees its queue; only a message's sender may withdraw it.
    [Fact]
    public async Task OnlyTheReadersOwnChips_OfferEditAndRemove()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        Seed(client);

        transport.Raise("OnQueueChanged", new QueueChangedNotification("topic-1",
        [
            new QueuedMessageItem("q1", "mine", 0, "fran", "Fran", DateTimeOffset.UnixEpoch),
            new QueuedMessageItem("q2", "marta's", 0, "marta", "Marta", DateTimeOffset.UnixEpoch)
        ], Revision: 5));

        Chips(client).Select(chip => (chip.Id, chip.Actions)).ShouldBe([
            ("q1", QueueChipActions.Edit | QueueChipActions.Remove),
            ("q2", QueueChipActions.None)
        ]);
    }

    // Several messages given back at once (a stop returns every one a person queued) land in the order
    // they were queued, all before what is typed, beside the files the composer already held.
    [Fact]
    public async Task SeveralReturnedMessages_LandInTheOrderTheyWereQueued_BesideTheFilesAlreadyThere()
    {
        await using var client = new ScriptedChatClient();
        await client.ConnectAsync();
        Seed(client);
        var held = Photo with { Id = "7-42/held", FileName = "held.png" };
        var second = Photo with { Id = "7-42/second", FileName = "second.png" };
        client.Dispatcher.Dispatch(new ReturnToComposer("topic-1", [new ReturnedMessage("q0", "", [held], null)]));

        client.Dispatcher.Dispatch(new ReturnToComposer("topic-1",
        [
            new ReturnedMessage("q1", "first queued", [Photo], null),
            new ReturnedMessage("q2", "second queued", [second], null)
        ]));

        var returned = client.Composer.State.Dictation.Transcript.ShouldNotBeNull();
        ComposerSelectors.Place("typed", returned).ShouldBe("first queued\n\nsecond queued\n\ntyped");
        client.Composer.State.For("topic-1").Select(file => file.Reference!.Id).ShouldBe(
            ["7-42/held", Photo.Id, "7-42/second"]);
        client.Composer.State.For("topic-1").ShouldAllBe(file => file.Status == AttachmentStatus.Ready);
    }

    // A message taken back to be reworked was queued on a model of its sender's choosing: the composer's
    // next send goes on it, whatever the picker shows now, and the send after that on the picker's.
    [Fact]
    public async Task ATakenBackMessage_IsSentAgainOnTheModelItWasQueuedWith_ThenThePickersChoiceResumes()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        Seed(client, new AgentCatalogEntry("agent-1", "Agent One", null, DefaultModel: "agent/default"));
        var queuedWith = new AgentConfigPatch { Model = "picked/then" };
        transport.Answer("TakeBackQueued", new ReturnedMessage("q1", "the queued words", [], queuedWith));
        var replies = AnswerEverySend(transport);
        client.Dispatcher.Dispatch(new EditQueuedMessage("topic-1", "q1"));
        await TestChat.Eventually(() => client.Composer.State.Dictation.Transcript is not null);

        client.Dispatcher.Dispatch(new SendMessage("topic-1", "the queued words"));
        await TestChat.Eventually(() => Sends(transport).Count == 1);
        replies.ReleaseAll();
        await TestChat.Eventually(() => !client.Streaming.State.StreamingTopics.Contains("topic-1"));
        client.Dispatcher.Dispatch(new SendMessage("topic-1", "and then"));
        await TestChat.Eventually(() => Sends(transport).Count == 2);
        replies.ReleaseAll();

        Sends(transport).ShouldBe([("the queued words", queuedWith), ("and then", null)]);
    }

    private static IReadOnlyList<(string Message, AgentConfigPatch? Patch)> Sends(FakeHubConnection transport) =>
    [
        .. transport.Calls
            .Where(call => call.MethodName == "SendMessage")
            .Select(call => ((string)call.Arguments[1]!, (AgentConfigPatch?)call.Arguments[3]))
    ];

    // Each send's reply stays open until the test releases every reply so far.
    private static Replies AnswerEverySend(FakeHubConnection transport)
    {
        var replies = new Replies();
        transport.Answer("SendMessage", _ => replies.Next().Chunks());
        return replies;
    }

    private sealed class Replies
    {
        private readonly List<GatedChatStream> _opened = [];

        public GatedChatStream Next()
        {
            lock (_opened)
            {
                var reply = new GatedChatStream();
                _opened.Add(reply);
                return reply;
            }
        }

        public void ReleaseAll()
        {
            lock (_opened)
            {
                _opened.ForEach(reply => reply.Release());
            }
        }
    }

    private static IReadOnlyList<QueueChip> Chips(ScriptedChatClient client) =>
        QueueSelectors.Chips(client.Service<QueueStore>().State, "topic-1", client.UserIdentity.State.SignedIn?.UserId);

    private static void Seed(ScriptedChatClient client, AgentCatalogEntry? agent = null)
    {
        client.Dispatcher.Dispatch(new SignedIn(new SignedInPerson("fran", "Fran", null, "home", ["member"])));
        client.Dispatcher.Dispatch(new SetAgents([agent ?? new AgentCatalogEntry("agent-1", "Agent One", null)]));
        client.Dispatcher.Dispatch(new SelectAgent("agent-1"));
        client.Dispatcher.Dispatch(new TopicsLoaded([StoredTopic.FromMetadata(TestChat.Topic("topic-1"))]));
        client.Dispatcher.Dispatch(new SelectTopic("topic-1"));
    }
}