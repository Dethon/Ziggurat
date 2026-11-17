using Domain.Channels;
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpChannelSignalR.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

// A steer is taken out of the queue, then sent to the agent, and only then is it known whether anybody
// heard it. The running turn can end in between: a steer drawn there that nobody heard would leave the
// agent waiting for a message that never comes, gathering it with the steers that did. So the turn's end
// draws only a steer the agent was heard to be sent; one still being sent is drawn once it is heard, or,
// nobody hearing it, goes back to the head of the queue, which goes on.
public sealed class StreamServiceSteerHeardTests : IDisposable
{
    private const string Topic = "topic-1";
    private static readonly Account Fran = new("home", "fran");

    private readonly SessionService _sessions = new();
    private readonly StreamService _streams;

    public StreamServiceSteerHeardTests()
    {
        _streams = new StreamService(
            _sessions, new Mock<IThreadStateStore>().Object, new Mock<IPushNotificationService>().Object,
            TimeProvider.System, NullLogger<StreamService>.Instance);
        _sessions.StartSession(Topic, "jack", 7, 42, "default", "Chat");
    }

    [Fact]
    public async Task ASteerTheTurnEndsBeforeItIsHeard_IsNotDrawn_AndNobodyHearingIt_GoesBackToTheHeadAndRunsNext()
    {
        Open();
        Queue("older");
        var steer = Steer(Queue("steer me"));

        await EndTheRunningTurnAsync();

        Drawn().ShouldBe(["first"]);
        _streams.QueueOf(Topic).Steering!.Select(s => s.Message.Content).ShouldBe(["steer me"]);
        _streams.IsStreaming(Topic).ShouldBeTrue();

        _streams.Unsteer(Topic, steer).ShouldBeTrue();

        _streams.Dispatches.TryRead(out var next).ShouldBeTrue();
        next.Message.Message.Content.ShouldBe("steer me");
        _streams.QueueOf(Topic).Messages.Select(m => m.Message.Content).ShouldBe(["older"]);
    }

    [Fact]
    public async Task ASteerHeardAfterTheTurnEnded_IsDrawnThen_AndItsAnswerEndsTheStream()
    {
        Open();
        var steer = Steer(Queue("steer me"));
        await EndTheRunningTurnAsync();
        // The agent ran it as soon as it heard it, the turn it was meant to cut being over: its answer waits
        // on the steer rather than stream under nothing.
        await _streams.WriteReplyAsync(Reply("its answer", ReplyContentType.Text, isComplete: false, steer.Id));
        Drawn().ShouldBe(["first"]);

        _streams.SteerHeard(Topic, steer);

        Drawn().ShouldBe(["first", "steer me"]);
        _streams.GetStreamState(Topic)!.BufferedMessages.Select(m => m.Content).ShouldContain("its answer");
        _streams.QueueOf(Topic).RunningFor.ShouldBe(Fran.UserId);
        _streams.QueueOf(Topic).Steering!.ShouldBeEmpty();
        await _streams.WriteReplyAsync(Reply("", ReplyContentType.StreamComplete, isComplete: true, steer.Id));
        _streams.IsStreaming(Topic).ShouldBeFalse();
    }

    [Fact]
    public async Task ASteerHeardBeforeTheTurnEnds_IsDrawnThere()
    {
        Open();
        var steer = Steer(Queue("steer me"));
        _streams.SteerHeard(Topic, steer);

        await EndTheRunningTurnAsync();

        Drawn().ShouldBe(["first", "steer me"]);
        _streams.QueueOf(Topic).Steering!.ShouldBeEmpty();
    }

    // A person's turn running on the topic, whose own it is, its message drawn.
    private void Open()
    {
        _streams.OpenOrHold(Topic, "first", Fran.UserId, Fran, CancellationToken.None);
        _streams.WriteMessageAsync(Topic, PersonTurns.MessageOf(Sender(), new TurnMessage("first", null, null, null), DateTimeOffset.UtcNow));
    }

    private QueuedMessage Queue(string words)
    {
        var held = _streams.Hold(Topic)!.Value;
        _streams.Enqueue(held, Sender(), new TurnMessage(words, null, null, null));
        return _streams.QueueOf(Topic).Messages.Last();
    }

    // As SteerQueued takes it out of the queue, before it is sent.
    private QueuedMessage Steer(QueuedMessage queued) =>
        _streams.Steer(Topic, queued.Id, Fran).ShouldBeOfType<Steered>().Message;

    private Task EndTheRunningTurnAsync() =>
        _streams.WriteReplyAsync(Reply("", ReplyContentType.StreamComplete, isComplete: true, "first-turn"));

    // The people's messages the stream drew, in order.
    private IReadOnlyList<string> Drawn() =>
        [.. _streams.GetStreamState(Topic)!.BufferedMessages.Where(m => m.UserMessage is not null).Select(m => m.Content!)];

    private static SenderSnapshot Sender() =>
        new(Topic, "session-1", new Principal(ChannelIds.SignalR, Fran.UserId, Fran), "Fran",
            new ConversationIdentity(7, 42), "jack", "default");

    private static SendReplyParams Reply(string words, ReplyContentType type, bool isComplete, string turnKey) => new()
    {
        ConversationId = "7:42",
        Content = words,
        ContentType = type,
        IsComplete = isComplete,
        MessageId = "m",
        TurnKey = turnKey
    };

    public void Dispose() => _streams.Dispose();
}