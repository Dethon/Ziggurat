using System.Collections.Concurrent;
using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using McpChannelSignalR.Internal;

namespace McpChannelSignalR.Services;

public sealed class StreamService(
    SessionService sessionService,
    IThreadStateStore threadStore,
    IPushNotificationService pushNotificationService,
    ILogger<StreamService> logger) : IStreamService, IDisposable
{
    private readonly ConcurrentDictionary<string, BroadcastChannel<ChatStreamMessage>> _responseChannels = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens = new();
    private readonly ConcurrentDictionary<string, StreamBuffer> _streamBuffers = new();
    private readonly ConcurrentDictionary<string, string> _currentPrompts = new();
    private readonly ConcurrentDictionary<string, string> _currentSenderIds = new();

    // Each topic's queue and what hangs off it, kept under _streamLock with the stream itself: a message
    // is queued only while the topic streams, and the stream ends only once nothing is pending or queued.
    private readonly Dictionary<string, TopicQueue> _topics = new();
    private long _lastStreamId;

    // What a stream that ended tells its people's phones, sent once _streamLock is let go.
    private sealed record EndedStream(
        ChannelSession Session, string TopicId, string SpaceSlug, string? TopicName, IReadOnlyList<ChatStreamMessage> Carried);

    // Numbered from the clock rather than from one, so a page that heard this process's predecessor
    // does not take a restarted hub's changes for older ones.
    private long _queueRevision = DateTimeOffset.UtcNow.UtcTicks;

    // A queue's head, taken as the turn before it ended, for the dispatcher to send. A channel rather
    // than a call, so the turn completing (send_reply's call) waits on nothing the head's sending does,
    // and the dispatcher, which writes on these streams, is not something they depend on.
    private readonly Channel<QueuedTurn> _dispatches = Channel.CreateUnbounded<QueuedTurn>(
        new UnboundedChannelOptions { SingleReader = true });

    // Bumped whenever who may see a topic changes (its owner shares it or stops), so a reply streaming
    // to a page asks again whether that page's person still may, and only then.
    private readonly ConcurrentDictionary<string, int> _audiences = new();
    private readonly Lock _streamLock = new();

    // What tells the agent a turn starts or stops goes out in the order the stream's state changed: a
    // stop takes the queue back before its cancel goes out, and a queued turn starts wholly before
    // that cancel (it is the turn cancelled) or not at all, never just behind it, where the agent
    // would take it for a turn of its own. One for every topic, since each step under it is in memory.
    private readonly SemaphoreSlim _agentOrder = new(1, 1);

    // A chunk is added to the buffer and written to the readers as one step, and a reader joining
    // takes the buffer and subscribes as one, so it misses nothing written meanwhile and sees nothing
    // twice.
    private readonly Lock _writeLock = new();
    private bool _disposed;

    public async Task WriteReplyAsync(SendReplyParams p)
    {
        var conversationId = p.ConversationId;
        var content = p.Content;
        var contentType = p.ContentType;
        var isComplete = p.IsComplete;
        var messageId = p.MessageId;

        // A turn with no live session delivers by persistence alone (ADR-0035): streaming is a
        // live-audience concern, the agent's history store already holds the reply, and the
        // browser catches up from it. The miss is designed, so it is quiet.
        var topicId = sessionService.GetTopicIdByConversationId(conversationId);
        if (topicId is null)
        {
            logger.LogDebug(
                "WriteReply: no live session for conversation {ConversationId}; delivering by persistence alone",
                conversationId);
            return;
        }

        // Use agent-provided messageId (from AgentResponseUpdate.MessageId) for proper bubble grouping
        var effectiveMessageId = messageId ?? topicId;

        var message = contentType switch
        {
            ReplyContentType.Text => new ChatStreamMessage { Content = content, MessageId = effectiveMessageId },
            ReplyContentType.Reasoning => new ChatStreamMessage { Reasoning = content, MessageId = effectiveMessageId },
            ReplyContentType.Error => new ChatStreamMessage { Error = content, IsComplete = true },
            ReplyContentType.StreamComplete => new ChatStreamMessage { IsComplete = true, MessageId = effectiveMessageId },
            _ => new ChatStreamMessage { Content = content, MessageId = effectiveMessageId }
        };

        if (HeldForSteer(topicId, p.TurnKey, message, isComplete))
        {
            return;
        }

        await WriteMessageAsync(topicId, message);

        // A turn ends on the one reply that says it is complete, never on its kind: the agent ends a
        // refused or failed turn with an error and then the stream's end, both under the same turn, and
        // counting the error too would start two queued turns at once, or end the stream under the one
        // that started. The hub's own errors (a refusal, nobody listening) are the turn's last word and
        // say so.
        if (!isComplete)
        {
            return;
        }

        if (contentType is not (ReplyContentType.Error or ReplyContentType.StreamComplete))
        {
            await WriteMessageAsync(topicId, new ChatStreamMessage { IsComplete = true, MessageId = effectiveMessageId });
        }

        // A turn an agent started (a schedule's, attached to the topic while a person's turn runs) ending
        // gives up its own place and is no end of the turn a steer was sent into.
        CompleteStream(topicId, startsSteers: p.AgentInitiated != true);
    }

    // A reply under the key of a steer that has not started is that steer's answer, given before the turn
    // it was sent into ended: kept on the steer rather than written into the reply still streaming.
    private bool HeldForSteer(string topicId, string? turnKey, ChatStreamMessage message, bool isComplete)
    {
        if (turnKey is null)
        {
            return false;
        }

        lock (_streamLock)
        {
            if (RunningOf(topicId)?.Steers.FirstOrDefault(s => s.Message.Id == turnKey) is not { } steer)
            {
                return false;
            }

            steer.Held.Add(message);
            steer.Answered |= isComplete;
            return true;
        }
    }

    // Lock protects the compound check-then-act: without it, a concurrent CompleteStream
    // can remove a channel between TryGetValue and return, handing back a completed channel.
    public (BroadcastChannel<ChatStreamMessage> Channel, CancellationToken Token) GetOrCreateStream(
        string topicId,
        string currentPrompt,
        string? currentSenderId,
        CancellationToken parentToken)
    {
        lock (_streamLock)
        {
            if (RunningOf(topicId) is { } running)
            {
                running.RunningFor = null;
            }

            if (_responseChannels.TryGetValue(topicId, out var existingChannel))
            {
                _currentPrompts[topicId] = currentPrompt;
                if (currentSenderId is not null)
                {
                    _currentSenderIds[topicId] = currentSenderId;
                }

                if (_cancellationTokens.TryGetValue(topicId, out var existingCts))
                {
                    return (existingChannel, existingCts.Token);
                }

                var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
                _cancellationTokens[topicId] = linkedCts;
                return (existingChannel, linkedCts.Token);
            }

            var broadcastChannel = new BroadcastChannel<ChatStreamMessage>();
            _responseChannels[topicId] = broadcastChannel;
            TopicOf(topicId).Running = new RunningQueue(new StreamKey(topicId, ++_lastStreamId));

            _streamBuffers.TryRemove(topicId, out _);

            _currentPrompts[topicId] = currentPrompt;
            if (currentSenderId is not null)
            {
                _currentSenderIds[topicId] = currentSenderId;
            }

            var cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            _cancellationTokens[topicId] = cts;

            return (broadcastChannel, cts.Token);
        }
    }

    // A person's send: the topic's stream opened for it, or, when one is running already, a place held
    // on that one — counted as pending, so the stream cannot end while the send is being admitted —
    // which the send gives up by queueing (Enqueue) or by being refused (Release), naming the stream it
    // was held on. The running stream's prompt and sender stay those of the turn it is on.
    public (BroadcastChannel<ChatStreamMessage> Channel, CancellationToken Token, bool Opened, StreamKey Stream) OpenOrHold(
        string topicId, string prompt, string senderId, Account person, CancellationToken parentToken)
    {
        lock (_streamLock)
        {
            var opened = !_responseChannels.ContainsKey(topicId);
            var (channel, token) = opened
                ? GetOrCreateStream(topicId, prompt, senderId, parentToken)
                : (_responseChannels[topicId], TokenOf(topicId, parentToken));
            var running = RunningOf(topicId)!;
            if (opened)
            {
                running.RunningFor = person;
            }

            running.AddPending();
            return (channel, token, opened, running.Stream);
        }
    }

    // A place held on the running stream for a message being admitted to its queue; null when nothing
    // runs to queue behind.
    public StreamKey? Hold(string topicId)
    {
        lock (_streamLock)
        {
            if (RunningOf(topicId) is not { } running)
            {
                return null;
            }

            running.AddPending();
            return running.Stream;
        }
    }

    private CancellationToken TokenOf(string topicId, CancellationToken parentToken)
    {
        if (_cancellationTokens.TryGetValue(topicId, out var existing))
        {
            return existing.Token;
        }

        var linked = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        _cancellationTokens[topicId] = linked;
        return linked.Token;
    }

    // A place held on the running stream (OpenOrHold, Hold), turned into a message waiting at the back
    // of its queue, or, when a stop took that stream meanwhile, handed back to its sender with the rest
    // of it. The place went with the stream either way, and a stream opened since is no part of it.
    public Queueing Enqueue(StreamKey held, QueuedMessage message)
    {
        EndedStream? ended;
        lock (_streamLock)
        {
            if (RunningOn(held) is not { } running)
            {
                return HandBackIfStopped(held, message);
            }

            running.Waiting.Add(message);
            _queueRevision++;
            ended = GiveUpPlace(held.TopicId, startsSteers: false);
        }

        Announce(ended);
        return Queueing.Queued;
    }

    // A message admitted to a stream a stop took meanwhile goes back to its sender with the rest of that
    // stream's queue; any other stream that went is a deleted topic's, with nobody to hand it to.
    private Queueing HandBackIfStopped(StreamKey held, QueuedMessage message)
    {
        if (_topics.GetValueOrDefault(held.TopicId) is not { LastStopped: { } stopped } topic || stopped.Stream != held)
        {
            return Queueing.NotRunning;
        }

        topic.Returned.Add(message);
        stopped.Add(message);
        return Queueing.Returned;
    }

    // A place held on the running stream given up, the message it was held for refused. Only on the
    // stream it was held on: a stop took that one's places with it, and a stream opened since has its own.
    public void Release(StreamKey held)
    {
        EndedStream? ended;
        lock (_streamLock)
        {
            if (RunningOn(held) is null)
            {
                return;
            }

            ended = GiveUpPlace(held.TopicId, startsSteers: false);
        }

        Announce(ended);
    }

    // The topic's queue now, in the order it will run.
    public QueueSnapshot QueueOf(string topicId)
    {
        lock (_streamLock)
        {
            var running = RunningOf(topicId);
            return new QueueSnapshot([.. running?.Waiting ?? []], _queueRevision, running?.RunningFor?.UserId);
        }
    }

    // A queued message taken out of its queue by the person who sent it, in the same step as a turn's
    // end would take it as the head, so it is either withdrawn or sent, never both.
    public Withdrawal Withdraw(string topicId, string queuedId, Account by)
    {
        lock (_streamLock)
        {
            if (RefusalToTake(topicId, queuedId, by) is { } refusal)
            {
                return new WithdrawalRefused(refusal);
            }

            var running = RunningOf(topicId)!;
            var message = TakeOut(running, queuedId);
            return new Withdrawn(message, [.. running.Waiting]);
        }
    }

    // Why the caller may not take a message out of the topic's queue, or null when it waits there and is
    // theirs. A message gone from the queue is told apart by where it went, so a page that lost the race
    // says the right thing: sent, or handed back by a stop.
    private string? RefusalToTake(string topicId, string queuedId, Account by)
    {
        var topic = _topics.GetValueOrDefault(topicId);
        if (topic?.Running?.Waiting.FirstOrDefault(m => m.Id == queuedId) is { } queued)
        {
            return queued.Sender.Principal.Account == by ? null : TopicRefusals.NotTheSenders;
        }

        return topic?.LastStopped is { } stopped && stopped.Returned(queuedId, out var sender)
            ? sender == by ? TopicRefusals.ReturnedToComposer : TopicRefusals.NotTheSenders
            : TopicRefusals.AlreadySent;
    }

    // The same step a turn's end takes the head in, so a message is either taken out or sent, never both.
    private QueuedMessage TakeOut(RunningQueue running, string queuedId)
    {
        var at = running.Waiting.FindIndex(m => m.Id == queuedId);
        var message = running.Waiting[at];
        running.Waiting.RemoveAt(at);
        _queueRevision++;
        return message;
    }

    // A queued message its sender steers the running turn with, taken out of the queue in the same step a
    // turn's end would take it, while that turn is still their own: it waits as pending, ahead of the
    // queue, for the running turn to end (Finish), and the caller sends it to the agent now.
    public Steering Steer(string topicId, string queuedId, Account by)
    {
        lock (_streamLock)
        {
            if (RefusalToTake(topicId, queuedId, by) is { } refusal)
            {
                return new SteerRefused(refusal);
            }

            var running = RunningOf(topicId)!;
            if (running.RunningFor != by)
            {
                return new SteerRefused(TopicRefusals.NotYourTurn);
            }

            var message = TakeOut(running, queuedId);
            running.Steers.Add(new WaitingSteer(message));
            running.AddPending();
            return new Steered(message);
        }
    }

    // A steer the agent could not be sent, back at the head of its queue, while the running turn has not
    // ended yet; false once it has, and the steer's turn has started on the stream.
    public bool Unsteer(string topicId, QueuedMessage steer)
    {
        lock (_streamLock)
        {
            if (RunningOf(topicId) is not { } running || running.Steers.RemoveAll(s => s.Message.Id == steer.Id) == 0)
            {
                return false;
            }

            running.Waiting.Insert(0, steer);
            running.RemovePending();
            _queueRevision++;
            return true;
        }
    }

    public ChannelReader<QueuedTurn> Dispatches => _dispatches.Reader;

    // A dispatched head's turn starting, sent by start while nothing stopped its stream since it was
    // taken: from then on a stop cancels it as the stream's current turn. False, with nothing sent,
    // when a stop came first.
    public Task<bool> StartAsync(QueuedTurn turn, Func<Task> start) =>
        InAgentOrderAsync(async () =>
        {
            if (!Start(turn))
            {
                return false;
            }

            await start();
            return true;
        });

    private bool Start(QueuedTurn turn)
    {
        lock (_streamLock)
        {
            if (RunningOn(turn.Stream) is not { } running || running.Dispatching.RemoveAll(m => m.Id == turn.Message.Id) == 0)
            {
                return false;
            }

            running.RunningFor = turn.Message.Sender.Principal.Account!;
            _queueRevision++;
            return true;
        }
    }

    // Whether the stream a queued turn was taken from is still the topic's: a stop, or a stop and a
    // new send, while its sender was being vouched for leaves it nothing to be written on.
    public bool IsRunning(StreamKey stream)
    {
        lock (_streamLock)
        {
            return RunningOn(stream) is not null;
        }
    }

    // A page resuming the topic's stream (the same join, found by topic): null when none is running.
    // Carried is what the stream carried since the state the page drew (StreamSeen.Since), or, for a
    // page that says nothing of it, everything.
    public (ChannelReader<ChatStreamMessage> Live, IReadOnlyList<ChatStreamMessage> Carried)? Join(
        string topicId, StreamSeen? seen = null)
    {
        lock (_writeLock)
        {
            // ReSharper disable once InconsistentlySynchronizedField
            if (!_responseChannels.TryGetValue(topicId, out var channel))
            {
                return null;
            }

            var (live, carried) = Join(topicId, channel);
            return (live, seen?.Since(carried) ?? carried);
        }
    }

    public IAsyncEnumerable<ChatStreamMessage>? SubscribeToStream(
        string topicId,
        CancellationToken cancellationToken)
    {
        // ReSharper disable once InconsistentlySynchronizedField
        return !_responseChannels.TryGetValue(topicId, out var channel)
            ? null
            : channel.Subscribe().ReadAllAsync(cancellationToken);
    }

    public Task WriteMessageAsync(
        string topicId,
        ChatStreamMessage message)
    {
        Write(topicId, message);
        return Task.CompletedTask;
    }

    private void Write(string topicId, ChatStreamMessage message)
    {
        lock (_writeLock)
        {
            // ReSharper disable once InconsistentlySynchronizedField
            if (!_responseChannels.TryGetValue(topicId, out var channel))
            {
                // No subscriber means no stream was ever created — the same designed miss as above,
                // one map further down.
                logger.LogDebug("WriteMessage: topicId {TopicId} not found in _responseChannels", topicId);
                return;
            }

            _streamBuffers.GetOrAdd(topicId, _ => new StreamBuffer()).Add(message);
            channel.Write(message);
        }
    }

    // A send joining a stream another send opened (two people sending in a shared topic at once) is
    // read on its own wire alone, so it is handed what the stream carried before it subscribed: the
    // other person's message, and the reply to it as far as it got. A new stream has carried nothing.
    public (ChannelReader<ChatStreamMessage> Live, IReadOnlyList<ChatStreamMessage> Carried) Join(
        string topicId, BroadcastChannel<ChatStreamMessage> channel)
    {
        lock (_writeLock)
        {
            var carried = _streamBuffers.TryGetValue(topicId, out var buffer) ? buffer.GetAll() : [];
            return (channel.Subscribe(), carried);
        }
    }

    // How much of a finished reply its push shows: a phone draws a line or two of it.
    public const int PushExcerptLength = 140;
    private const string NoExcerpt = "The agent has finished responding";

    private void CompleteStream(string topicId, bool startsSteers)
    {
        EndedStream? ended;
        lock (_streamLock)
        {
            ended = GiveUpPlace(topicId, startsSteers);
        }

        Announce(ended);
    }

    // A turn on the stream is over, or a place held on it given up: the next waiting turn starts on the
    // same stream, and only once nothing is pending or queued does the stream end. Called under
    // _streamLock, in the same step as whatever gave the place up, so a stop in between can neither lose
    // a message nor end another stream. Answers what the ended stream tells phones, if it ended.
    private EndedStream? GiveUpPlace(string topicId, bool startsSteers)
    {
        var running = RunningOf(topicId);
        var remaining = running?.RemovePending() ?? 0;
        if (startsSteers && running is not null)
        {
            remaining = StartSteers(topicId, running, remaining);
        }

        if (remaining > 0)
        {
            return null;
        }

        if (running is not null && TakeHead(running) is { } head)
        {
            running.PendingOnlyTheHead();
            _currentPrompts[topicId] = head.Message.Message.Content;
            _currentSenderIds[topicId] = head.Message.Sender.UserId;
            _dispatches.Writer.TryWrite(head);
            return null;
        }

        sessionService.TryGetSession(topicId, out var session);

        // Taken before the cleanup below drops the buffer, and read outside the lock every topic's
        // IsStreaming waits on.
        IReadOnlyList<ChatStreamMessage> carried = _streamBuffers.TryGetValue(topicId, out var buffer) ? buffer.GetAll() : [];

        if (_responseChannels.TryRemove(topicId, out var channel))
        {
            channel.Complete();
        }

        CleanupStreamState(topicId);
        return session is { SpaceSlug: { } spaceSlug }
            ? new EndedStream(session, topicId, spaceSlug, session.TopicName, carried)
            : null;
    }

    private void Announce(EndedStream? ended)
    {
        if (ended is not null)
        {
            _ = SendPushNotificationAsync(
                ended.Session, ended.TopicId, ended.SpaceSlug, ended.TopicName, LastSaid(ended.Carried));
        }
    }

    // A turn ending with a steer waiting: the steer's turn is the next, already sent, and the reply splits
    // here, so its message is written now, before anything of its answer. A steer the agent already
    // answered (refused as it arrived) is drawn with that answer, and its turn is over too, so the one
    // after it starts. Answers how many turns are still pending.
    private int StartSteers(string topicId, RunningQueue running, int remaining)
    {
        while (remaining > 0 && running.Steers.Count > 0)
        {
            var steer = running.Steers[0];
            running.Steers.RemoveAt(0);
            var sender = steer.Message.Sender;
            Write(topicId, PersonTurns.MessageOf(sender, steer.Message.Message));
            steer.Held.ForEach(chunk => Write(topicId, chunk));
            _currentPrompts[topicId] = steer.Message.Message.Content;
            _currentSenderIds[topicId] = sender.UserId;
            if (!steer.Answered)
            {
                running.RunningFor = sender.Principal.Account!;
                return remaining;
            }

            running.RunningFor = null;
            remaining = running.RemovePending();
        }

        return remaining;
    }

    private QueuedTurn? TakeHead(RunningQueue running)
    {
        if (running.Waiting.Count == 0)
        {
            return null;
        }

        var head = running.Waiting[0];
        running.Waiting.RemoveAt(0);
        running.RunningFor = null;
        running.Dispatching.Add(head);
        _queueRevision++;
        return new QueuedTurn(head, running.Stream);
    }

    // The reply's last words, not its reasoning, its tool calls or a person's message on the stream.
    private static string? LastSaid(IReadOnlyList<ChatStreamMessage> carried) =>
        carried
            .Reverse()
            .Where(m => m.UserMessage is null && !string.IsNullOrWhiteSpace(m.Content))
            .Select(m => m.Content!.Excerpt(PushExcerptLength))
            .FirstOrDefault(text => text.Length > 0);

    // The topic's name and what the reply said ride on the push, so a private topic's goes to its
    // owner's devices alone, the way its news on the hub does (TopicAudience); any other topic's goes
    // to the space.
    private async Task SendPushNotificationAsync(
        ChannelSession session, string topicId, string spaceSlug, string? topicName, string? said)
    {
        try
        {
            var notice = new PushNotice(
                topicName ?? "New response", said ?? NoExcerpt, $"/{spaceSlug}", new PushTopic(session.AgentId, topicId));
            var topic = await threadStore.GetTopicByChatIdAndThreadIdAsync(
                session.AgentId, session.ChatId, session.ThreadId);

            // A space is its household's, so a topic recording none — or none held — has no space
            // whose phones may hear of it.
            if (topic?.TenantId is not { } household)
            {
                return;
            }

            if (topic is { IsPrivate: true, OwnerId: { } owner })
            {
                await pushNotificationService.SendToPersonAsync(new Account(household, owner), spaceSlug, notice);
                return;
            }

            await pushNotificationService.SendToSpaceAsync(household, spaceSlug, notice);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to send push notification for space {SpaceSlug}", spaceSlug);
        }
    }

    // A person's stop: the turn and the stream cancelled as any cancel, and what waited behind it
    // handed back to whoever queued it rather than run after a reply somebody abandoned, before the
    // agent is told to cancel (cancelTurn), so no turn ending meanwhile can start the next one behind
    // the cancel. Answers the messages handed back, in queue order.
    public Task<IReadOnlyList<QueuedMessage>> StopAsync(string topicId, Func<Task> cancelTurn) =>
        InAgentOrderAsync(async () =>
        {
            var stopped = Stop(topicId);
            await cancelTurn();
            return stopped;
        });

    // A step that tells the agent of a turn, taken in turn with the starts and stops above: a send that
    // opened a stream while a stop was ending the last one reaches the agent after that stop's cancel.
    public async Task<T> InAgentOrderAsync<T>(Func<Task<T>> step)
    {
        await _agentOrder.WaitAsync();
        try
        {
            return await step();
        }
        finally
        {
            _agentOrder.Release();
        }
    }

    private IReadOnlyList<QueuedMessage> Stop(string topicId)
    {
        lock (_streamLock)
        {
            if (RunningOf(topicId) is not { } running)
            {
                CancelStream(topicId);
                return [];
            }

            IReadOnlyList<QueuedMessage> stopped =
                [.. running.Steers.Select(s => s.Message), .. running.Dispatching, .. running.Waiting];
            var topic = _topics[topicId];
            topic.Returned.AddRange(stopped);
            topic.LastStopped = new StoppedStream(running.Stream, stopped);
            CancelStream(topicId);
            return stopped;
        }
    }

    // A sender's returned messages on a topic, handed over once: whichever of their pages asks first.
    public IReadOnlyList<QueuedMessage> TakeReturned(string topicId, Account by)
    {
        lock (_streamLock)
        {
            if (_topics.GetValueOrDefault(topicId) is not { } topic)
            {
                return [];
            }

            IReadOnlyList<QueuedMessage> theirs = [.. topic.Returned.Where(m => m.Sender.Principal.Account == by)];
            topic.Returned.RemoveAll(m => m.Sender.Principal.Account == by);
            DropIfEmpty(topicId, topic);
            return theirs;
        }
    }

    // A deleted topic: its stream cancelled, its queue dropped and whatever a stop returned in it
    // forgotten, since nothing said in a deleted conversation is kept.
    public void Forget(string topicId)
    {
        lock (_streamLock)
        {
            CancelStream(topicId);
            _topics.Remove(topicId);
        }
    }

    public void CancelStream(string topicId)
    {
        lock (_streamLock)
        {
            if (_cancellationTokens.TryRemove(topicId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }

            if (_responseChannels.TryRemove(topicId, out var channel))
            {
                channel.Complete();
            }

            CleanupStreamState(topicId);
        }
    }

    public int AudienceOf(string topicId) => _audiences.GetValueOrDefault(topicId);

    public void AudienceChanged(string topicId) => _audiences.AddOrUpdate(topicId, 1, (_, version) => version + 1);

    public bool IsStreaming(string topicId)
    {
        lock (_streamLock)
        {
            return _responseChannels.ContainsKey(topicId);
        }
    }

    public bool TryIncrementPending(string topicId)
    {
        lock (_streamLock)
        {
            if (!_responseChannels.ContainsKey(topicId))
            {
                return false;
            }

            RunningOf(topicId)!.AddPending();
            return true;
        }
    }

    public StreamState? GetStreamState(string topicId)
    {
        // ReSharper disable once InconsistentlySynchronizedField
        var isProcessing = _responseChannels.ContainsKey(topicId);
        _currentPrompts.TryGetValue(topicId, out var currentPrompt);
        _currentSenderIds.TryGetValue(topicId, out var currentSenderId);

        if (!_streamBuffers.TryGetValue(topicId, out var buffer))
        {
            return isProcessing ? new StreamState(true, [], string.Empty, currentPrompt, currentSenderId) : null;
        }

        var messages = buffer.GetAll();
        var lastMessageId = messages.LastOrDefault()?.MessageId ?? string.Empty;

        return new StreamState(isProcessing, messages, lastMessageId, currentPrompt, currentSenderId);
    }

    private void CleanupStreamState(string topicId)
    {
        _streamBuffers.TryRemove(topicId, out _);
        _currentPrompts.TryRemove(topicId, out _);
        _currentSenderIds.TryRemove(topicId, out _);
        _audiences.TryRemove(topicId, out _);
        if (_topics.GetValueOrDefault(topicId) is { } topic)
        {
            if (topic.Running is { Waiting.Count: > 0 })
            {
                _queueRevision++;
            }

            topic.Running = null;
            DropIfEmpty(topicId, topic);
        }

        if (_cancellationTokens.TryRemove(topicId, out var cts))
        {
            cts.Dispose();
        }
    }

    // Lookups that never create, so asking about a topic with nothing queued leaves nothing behind.
    private RunningQueue? RunningOf(string topicId) => _topics.GetValueOrDefault(topicId)?.Running;

    private RunningQueue? RunningOn(StreamKey stream) => RunningOf(stream.TopicId) is { } running && running.Stream == stream ? running : null;

    private TopicQueue TopicOf(string topicId)
    {
        if (!_topics.TryGetValue(topicId, out var topic))
        {
            _topics[topicId] = topic = new TopicQueue();
        }

        return topic;
    }

    private void DropIfEmpty(string topicId, TopicQueue topic)
    {
        if (topic.IsEmpty)
        {
            _topics.Remove(topicId);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _dispatches.Writer.TryComplete();

        foreach (var channel in _responseChannels.Values)
        {
            channel.Complete();
        }

        foreach (var cts in _cancellationTokens.Values)
        {
            cts.Cancel();
            cts.Dispose();
        }
    }
}