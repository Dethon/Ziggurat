using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.State.Queue;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace Tests.Unit.WebChat.Client.State;

// The queue is the topic's, so a page shows it as the hub holds it, not only as it has been told since
// it arrived: one opening the topic, resuming its stream or coming back from an interruption asks for
// it, and keeps whichever of what it asked and what it was told was read later.
public sealed class QueueCatchUpTests
{
    [Fact]
    public async Task OpeningATopic_ShowsItsQueueAsTheHubHoldsIt_OtherPeoplesChipsNamedAndWithoutActions()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        transport.Answer("GetQueue", Queue(revision: 5,
            Item("q1", "marta's words", "marta", "Marta"),
            Item("q2", "my words", "fran", "Fran")));

        Seed(client, open: "topic-1");

        await TestChat.Eventually(() => Chips(client).Count == 2);
        Chips(client).ShouldBe([
            new QueueChip("q1", "marta's words", 0, "Marta", Mine: false),
            new QueueChip("q2", "my words", 0, null, Mine: true, QueueChipActions.Edit | QueueChipActions.Remove)
        ]);
        transport.Calls.ShouldContain(call =>
            call.MethodName == "GetQueue" && call.Arguments.SequenceEqual(new object?[] { "topic-1" }));
    }

    private static QueueChangedNotification Queue(long revision, params QueuedMessageItem[] items) =>
        new("topic-1", items, revision, "default");

    private static QueuedMessageItem Item(string id, string words, string senderId, string senderName) =>
        new(id, words, 0, senderId, senderName, DateTimeOffset.UnixEpoch);

    private static IReadOnlyList<QueueChip> Chips(ScriptedChatClient client) =>
        QueueSelectors.Chips(client.Service<QueueStore>().State, "topic-1", client.UserIdentity.State.SignedIn?.UserId);

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