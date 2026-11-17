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

// Every chunk of every reply asks whether it is a waiting steer's answer (send_reply is the hot path),
// and with no steer waiting under its key that question takes no lock: the stream lock every topic's
// queue, stop and dispatch go through is not taken a few hundred times a reply for nothing. Held by
// another topic's step, it must not hold the chunk up.
public sealed class StreamServiceSteerCheckTests : IDisposable
{
    private const string Topic = "topic-1";
    private const string Other = "topic-2";

    private static readonly Account Fran = new("home", "fran");

    private readonly SessionService _sessions = new();
    private readonly GateClock _clock = new();
    private readonly StreamService _streams;

    public StreamServiceSteerCheckTests()
    {
        _streams = new StreamService(
            _sessions, new Mock<IThreadStateStore>().Object, new Mock<IPushNotificationService>().Object, _clock,
            NullLogger<StreamService>.Instance);
        _sessions.StartSession(Topic, "jack", 7, 42, "default", "Chat");
        _sessions.StartSession(Other, "jack", 8, 43, "default", "Other");
    }

    [Fact]
    public async Task AChunkUnderAKeyNoSteerWaitsUnder_IsWritten_WhileTheStreamLockIsHeld()
    {
        Open(Topic);

        await ChunkIsWrittenWhileTheLockIsHeldAsync(TurnKey.Mint());
    }

    // However a steer stops waiting — drawn as the running turn ends, put back when nobody heard it, or
    // handed back by a stop — its key is let go with it: the steer's own reply is every chunk under it.
    [Theory]
    [InlineData("drawn")]
    [InlineData("put back")]
    [InlineData("stopped")]
    public async Task ASteerThatStoppedWaiting_LeavesItsKeysChunksUnlocked(string how)
    {
        Open(Topic);
        var steer = Steer();
        switch (how)
        {
            case "drawn":
                await _streams.WriteReplyAsync(Reply("cut short", isComplete: true, turnKey: null));
                break;
            case "put back":
                _streams.Unsteer(Topic, steer).ShouldBeTrue();
                break;
            default:
                await _streams.StopAsync(Topic, _ => Task.CompletedTask);
                break;
        }

        await ChunkIsWrittenWhileTheLockIsHeldAsync(steer.Id);
    }

    // A queued message steered into the running turn, as SteerQueued takes it out of the queue and then
    // hears it was sent.
    private QueuedMessage Steer()
    {
        var held = _streams.Hold(Topic)!.Value;
        _streams.Enqueue(held, Sender(Topic, 7, 42), new TurnMessage("steer me", null, null, null));
        var queued = _streams.QueueOf(Topic).Messages.Single();
        var steer = _streams.Steer(Topic, queued.Id, Fran).ShouldBeOfType<Steered>().Message;
        _streams.SteerHeard(Topic, steer);
        return steer;
    }

    // Another topic's message being queued holds the stream lock (it reads the clock under it) while the
    // chunk is written.
    private async Task ChunkIsWrittenWhileTheLockIsHeldAsync(string turnKey)
    {
        Open(Other);
        var held = _streams.Hold(Other)!.Value;
        _clock.Close();
        try
        {
            var holding = Task.Run(() => _streams.Enqueue(held, Sender(Other, 8, 43), new TurnMessage("later", null, null, null)));
            _clock.Entered.ShouldBeTrue("the other topic's step to be holding the lock");

            var write = Task.Run(() => _streams.WriteReplyAsync(Reply("a chunk", isComplete: false, turnKey)));

            await Eventually.Until(() => write.IsCompleted, "the chunk to be written with the lock held elsewhere");
            holding.IsCompleted.ShouldBeFalse("the lock was let go before the chunk was written");
            await write;
            _clock.Open();
            await holding;
        }
        finally
        {
            _clock.Open();
        }
    }

    // A person's turn running on the topic, whose own it is.
    private void Open(string topic) =>
        _streams.OpenOrHold(topic, "first", Fran.UserId, Fran, CancellationToken.None);

    private static SenderSnapshot Sender(string topic, long chat, long thread) =>
        new(topic, "session-1", new Principal(ChannelIds.SignalR, Fran.UserId, Fran), "Fran",
            new ConversationIdentity(chat, thread), "jack", "default");

    private static SendReplyParams Reply(string words, bool isComplete, string? turnKey) => new()
    {
        ConversationId = "7:42",
        Content = words,
        ContentType = ReplyContentType.Text,
        IsComplete = isComplete,
        MessageId = words,
        TurnKey = turnKey
    };

    public void Dispose()
    {
        _clock.Open();
        _streams.Dispose();
    }

    // The wall clock, unless closed: then the next read waits until it is opened again, holding whatever
    // lock its reader holds.
    private sealed class GateClock : TimeProvider
    {
        private readonly ManualResetEventSlim _open = new(true);
        private readonly ManualResetEventSlim _entered = new(false);

        public bool Entered => _entered.Wait(TimeSpan.FromSeconds(60));

        public void Close()
        {
            _entered.Reset();
            _open.Reset();
        }

        public void Open() => _open.Set();

        public override DateTimeOffset GetUtcNow()
        {
            if (!_open.IsSet)
            {
                _entered.Set();
                // Longer than any wait of the test's, so the lock is let go by the test alone.
                _open.Wait(TimeSpan.FromMinutes(5));
            }

            return System.GetUtcNow();
        }
    }
}