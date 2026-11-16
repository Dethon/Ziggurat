using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Queue;
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

    private static IReadOnlyList<QueueChip> Chips(ScriptedChatClient client) =>
        QueueSelectors.Chips(client.Service<QueueStore>().State, "topic-1", client.UserIdentity.State.SignedIn?.UserId);

    private static void Seed(ScriptedChatClient client)
    {
        client.Dispatcher.Dispatch(new SignedIn(new SignedInPerson("fran", "Fran", null, "home", ["member"])));
        client.Dispatcher.Dispatch(new SetAgents([new AgentCatalogEntry("agent-1", "Agent One", null)]));
        client.Dispatcher.Dispatch(new SelectAgent("agent-1"));
        client.Dispatcher.Dispatch(new TopicsLoaded([StoredTopic.FromMetadata(TestChat.Topic("topic-1"))]));
        client.Dispatcher.Dispatch(new SelectTopic("topic-1"));
    }
}