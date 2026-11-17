using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.State.AgentSettings;
using WebChat.Client.State.Queue;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace Tests.Unit.WebChat.Client.State;

// Steer now on a person's own queued message: offered only while the running turn is their own, it asks
// the hub to send the message into that turn, and the chip goes once the hub took it.
public sealed class QueuedSteerTests
{
    [Theory]
    [InlineData("fran", true)]
    [InlineData("marta", false)]
    [InlineData(null, false)]
    public async Task SteerNow_IsOfferedOnTheReadersOwnChips_OnlyWhileTheRunningTurnIsTheirOwn(
        string? runningFor, bool offered)
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        Seed(client);

        transport.Raise("OnQueueChanged", new QueueChangedNotification("topic-1",
        [
            new QueuedMessageItem("q1", "mine", 0, "fran", "Fran", DateTimeOffset.UnixEpoch),
            new QueuedMessageItem("q2", "marta's", 0, "marta", "Marta", DateTimeOffset.UnixEpoch)
        ], Revision: 5, RunningFor: runningFor));

        var own = QueueChipActions.Edit | QueueChipActions.Remove;
        Chips(client).Select(chip => (chip.Id, chip.Actions)).ShouldBe([
            ("q1", offered ? own | QueueChipActions.Steer : own),
            ("q2", QueueChipActions.None)
        ]);
    }

    // Taken by the hub, the message waits for the running turn to end as a chip marked steering, with
    // nothing left to do to it, rather than vanish until it is drawn.
    [Fact]
    public async Task SteerNow_AsksTheHubToSteerWithIt_AndTheChipWaitsAsSteering()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        Seed(client);
        transport.Raise("OnQueueChanged", new QueueChangedNotification("topic-1",
            [new QueuedMessageItem("q1", "the other one", 0, "fran", "Fran", DateTimeOffset.UnixEpoch)],
            Revision: 5, RunningFor: "fran"));
        transport.Answer("SteerQueued", (object?)null);

        client.Dispatcher.Dispatch(new SteerQueuedMessage("topic-1", "q1"));

        await TestChat.Eventually(() => Chips(client) is [{ Steering: true }]);
        Chips(client).Single().Actions.ShouldBe(QueueChipActions.None);
        transport.Calls.ShouldContain(call =>
            call.MethodName == "SteerQueued" && call.Arguments.SequenceEqual(new object?[] { "topic-1", "q1" }));
    }

    // The running turn moved on before the press reached the hub: the person is told why, and the
    // message keeps its chip, waiting its turn.
    [Fact]
    public async Task ASteerTheHubRefuses_IsToldSo_AndTheChipStays()
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        Seed(client);
        transport.Raise("OnQueueChanged", new QueueChangedNotification("topic-1",
            [new QueuedMessageItem("q1", "the other one", 0, "fran", "Fran", DateTimeOffset.UnixEpoch)],
            Revision: 5, RunningFor: "fran"));
        transport.Answer("SteerQueued", (Func<object?[], object?>)(_ => throw new HubException(
            $"An unexpected error occurred invoking 'SteerQueued' on the server. HubException: {TopicRefusals.NotYourTurn}")));

        client.Dispatcher.Dispatch(new SteerQueuedMessage("topic-1", "q1"));

        await TestChat.Eventually(() => client.Toasts.State.Toasts.Any(t => t.Message == TopicRefusals.NotYourTurn));
        Chips(client).Select(chip => chip.Id).ShouldBe(["q1"]);
    }

    // Every page of the topic shows a steer as the hub tells it, ahead of the queue, marked steering and
    // with no action for anybody, until the hub tells it was drawn.
    [Theory]
    [InlineData("fran")]
    [InlineData("marta")]
    public async Task ASteerTheHubTells_ShowsAheadOfTheQueueAsSteering_UntilItIsDrawn(string steeredBy)
    {
        await using var client = new ScriptedChatClient();
        var transport = await client.ConnectAsync();
        Seed(client);

        transport.Raise("OnQueueChanged", new QueueChangedNotification("topic-1",
            [new QueuedMessageItem("q1", "waiting", 0, "fran", "Fran", DateTimeOffset.UnixEpoch)],
            Revision: 5, RunningFor: steeredBy,
            Steering: [new QueuedMessageItem("s1", "steering in", 0, steeredBy, steeredBy, DateTimeOffset.UnixEpoch)]));

        Chips(client).Select(chip => (chip.Id, chip.Steering, chip.Actions == QueueChipActions.None)).ShouldBe([
            ("s1", true, true),
            ("q1", false, false)
        ]);

        transport.Raise("OnQueueChanged", new QueueChangedNotification("topic-1",
            [new QueuedMessageItem("q1", "waiting", 0, "fran", "Fran", DateTimeOffset.UnixEpoch)],
            Revision: 6, RunningFor: steeredBy));

        Chips(client).Select(chip => chip.Id).ShouldBe(["q1"]);
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