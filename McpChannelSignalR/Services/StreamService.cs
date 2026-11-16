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
    private readonly ConcurrentDictionary<string, int> _pendingPromptCounts = new();

    // The messages waiting for each topic's running turn to end, and which stream each topic's is,
    // both kept under _streamLock with the stream itself: a message is queued only while the topic
    // streams, and the stream ends only once nothing is pending or queued.
    private readonly Dictionary<string, List<QueuedMessage>> _queues = new();
    private readonly Dictionary<string, long> _streamIds = new();

    // A queue's head taken as the turn before it ended, until the dispatcher writes it on the stream:
    // not started yet, so a stop meanwhile returns it with the rest.
    private readonly Dictionary<string, QueuedMessage> _dispatching = new();
    private long _lastStreamId;

    // What a stop handed back to each sender and no page of theirs has taken yet, in the order it was
    // queued. Kept past the stream it was queued on, under _streamLock, until taken or the topic is
    // deleted: a sender with no page open finds it the next time they open the topic.
    private readonly Dictionary<(string TopicId, Account? Sender), List<QueuedMessage>> _returned = new();

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

        if (isComplete && contentType != ReplyContentType.Error && contentType != ReplyContentType.StreamComplete)
        {
            await WriteMessageAsync(topicId, message);
            var completeMessage = new ChatStreamMessage { IsComplete = true, MessageId = effectiveMessageId };
            await WriteMessageAsync(topicId, completeMessage);
            CompleteStream(topicId);
            return;
        }

        if (contentType is ReplyContentType.Error or ReplyContentType.StreamComplete)
        {
            await WriteMessageAsync(topicId, message);
            CompleteStream(topicId);
            return;
        }

        await WriteMessageAsync(topicId, message);
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
            _streamIds[topicId] = ++_lastStreamId;

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
    public (BroadcastChannel<ChatStreamMessage> Channel, CancellationToken Token, bool Opened, long Stream) OpenOrHold(
        string topicId, string prompt, string senderId, CancellationToken parentToken)
    {
        lock (_streamLock)
        {
            var opened = !_responseChannels.ContainsKey(topicId);
            var (channel, token) = opened
                ? GetOrCreateStream(topicId, prompt, senderId, parentToken)
                : (_responseChannels[topicId], TokenOf(topicId, parentToken));
            _pendingPromptCounts.AddOrUpdate(topicId, 1, (_, count) => count + 1);
            return (channel, token, opened, _streamIds[topicId]);
        }
    }

    // A place held on the running stream for a message being admitted to its queue; null when nothing
    // runs to queue behind.
    public long? Hold(string topicId)
    {
        lock (_streamLock)
        {
            if (!_streamIds.TryGetValue(topicId, out var stream))
            {
                return null;
            }

            _pendingPromptCounts.AddOrUpdate(topicId, 1, (_, count) => count + 1);
            return stream;
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
    // of its queue. False when that stream was stopped meanwhile: the place went with it, and a
    // stream opened since is no part of it.
    public bool Enqueue(string topicId, long stream, QueuedMessage message) => Finish(topicId, stream, message);

    // A place held on the running stream given up, the message it was held for refused.
    public void Release(string topicId, long stream) => Finish(topicId, stream, null);

    // The topic's queue now, in the order it will run.
    public QueueSnapshot QueueOf(string topicId)
    {
        lock (_streamLock)
        {
            return new QueueSnapshot(
                _queues.TryGetValue(topicId, out var queue) ? [.. queue] : [], _queueRevision);
        }
    }

    // A queued message taken out of its queue by the person who sent it, in the same step as a turn's
    // end would take it as the head, so it is either withdrawn or sent, never both.
    public Withdrawal Withdraw(string topicId, string queuedId, Account by)
    {
        lock (_streamLock)
        {
            var queue = _queues.GetValueOrDefault(topicId) ?? [];
            var at = queue.FindIndex(m => m.Id == queuedId);
            if (at < 0)
            {
                return Withdrawal.AlreadySent;
            }

            var message = queue[at];
            if (message.Sender.Principal.Account != by)
            {
                return Withdrawal.NotTheSenders;
            }

            queue.RemoveAt(at);
            _queueRevision++;
            return new Withdrawal(message, [.. queue]);
        }
    }

    public ChannelReader<QueuedTurn> Dispatches => _dispatches.Reader;

    // A dispatched head's turn starting: true while nothing stopped its stream since it was taken, and
    // from then on a stop leaves it to run as the stream's current turn.
    public bool Start(QueuedTurn turn)
    {
        lock (_streamLock)
        {
            var topicId = turn.Message.Sender.TopicId;
            return IsRunning(topicId, turn.Stream)
                   && _dispatching.TryGetValue(topicId, out var dispatching)
                   && dispatching.Id == turn.Message.Id
                   && _dispatching.Remove(topicId);
        }
    }

    // Whether the stream a queued turn was taken from is still the topic's: a stop, or a stop and a
    // new send, while its sender was being vouched for leaves it nothing to be written on.
    public bool IsRunning(string topicId, long stream)
    {
        lock (_streamLock)
        {
            return _streamIds.TryGetValue(topicId, out var running) && running == stream;
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
        lock (_writeLock)
        {
            // ReSharper disable once InconsistentlySynchronizedField
            if (!_responseChannels.TryGetValue(topicId, out var channel))
            {
                // No subscriber means no stream was ever created — the same designed miss as above,
                // one map further down.
                logger.LogDebug("WriteMessage: topicId {TopicId} not found in _responseChannels", topicId);
                return Task.CompletedTask;
            }

            _streamBuffers.GetOrAdd(topicId, _ => new StreamBuffer()).Add(message);
            channel.Write(message);
        }

        return Task.CompletedTask;
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

    private void CompleteStream(string topicId) => Finish(topicId, heldOn: null, queueing: null);

    // A turn on the stream is over, or a place held on it given up (queueing its message, or not): the
    // next queued message's turn starts on the same stream, and only once nothing is pending or queued
    // does the stream end. A place is given up only on the stream it was held on, in the same step as
    // its message is queued, so a stop in between can neither lose the message nor end another stream.
    private bool Finish(string topicId, long? heldOn, QueuedMessage? queueing)
    {
        ChannelSession? finished = null;
        string? spaceSlug = null;
        string? topicName = null;
        IReadOnlyList<ChatStreamMessage> carried = [];

        lock (_streamLock)
        {
            if (heldOn is { } held && !(_streamIds.TryGetValue(topicId, out var running) && running == held))
            {
                return false;
            }

            if (queueing is not null)
            {
                if (!_queues.TryGetValue(topicId, out var queue))
                {
                    _queues[topicId] = queue = [];
                }

                queue.Add(queueing);
                _queueRevision++;
            }

            // Decrement pending count — only tear down when the last agent finishes
            var remaining = _pendingPromptCounts.AddOrUpdate(topicId, 0, (_, count) => count - 1);
            if (remaining > 0)
            {
                return true;
            }

            if (TakeHead(topicId) is { } head)
            {
                _pendingPromptCounts[topicId] = 1;
                _currentPrompts[topicId] = head.Message.Message.Content;
                _currentSenderIds[topicId] = head.Message.Sender.UserId;
                _dispatches.Writer.TryWrite(head);
                return true;
            }

            // Resolve space slug and topic name before cleanup
            if (sessionService.TryGetSession(topicId, out var session))
            {
                finished = session;
                spaceSlug = session?.SpaceSlug;
                topicName = session?.TopicName;
            }

            // Taken before the cleanup below drops the buffer, and read outside the lock every topic's
            // IsStreaming waits on.
            if (_streamBuffers.TryGetValue(topicId, out var buffer))
            {
                carried = buffer.GetAll();
            }

            if (_responseChannels.TryRemove(topicId, out var channel))
            {
                channel.Complete();
            }

            CleanupStreamState(topicId);
        }

        if (finished is not null && spaceSlug is not null)
        {
            _ = SendPushNotificationAsync(finished, topicId, spaceSlug, topicName, LastSaid(carried));
        }

        return true;
    }

    private QueuedTurn? TakeHead(string topicId)
    {
        if (!_queues.TryGetValue(topicId, out var queue) || queue.Count == 0
            || !_streamIds.TryGetValue(topicId, out var stream))
        {
            return null;
        }

        var head = queue[0];
        queue.RemoveAt(0);
        _dispatching[topicId] = head;
        _queueRevision++;
        return new QueuedTurn(head, stream);
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
    // handed back to whoever queued it rather than run after a reply somebody abandoned. Answers the
    // messages handed back, in queue order.
    public IReadOnlyList<QueuedMessage> Stop(string topicId)
    {
        lock (_streamLock)
        {
            IReadOnlyList<QueuedMessage> stopped =
            [
                .. _dispatching.TryGetValue(topicId, out var dispatching) ? [dispatching] : Array.Empty<QueuedMessage>(),
                .. _queues.GetValueOrDefault(topicId) ?? []
            ];
            stopped
                .GroupBy(m => m.Sender.Principal.Account)
                .ToList()
                .ForEach(sender => ReturnedTo(topicId, sender.Key).AddRange(sender));
            CancelStream(topicId);
            return stopped;
        }
    }

    private List<QueuedMessage> ReturnedTo(string topicId, Account? sender)
    {
        if (!_returned.TryGetValue((topicId, sender), out var returned))
        {
            _returned[(topicId, sender)] = returned = [];
        }

        return returned;
    }

    // A sender's returned messages on a topic, handed over once: whichever of their pages asks first.
    public IReadOnlyList<QueuedMessage> TakeReturned(string topicId, Account by)
    {
        lock (_streamLock)
        {
            return _returned.Remove((topicId, by), out var returned) ? returned : [];
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

            _pendingPromptCounts.AddOrUpdate(topicId, 1, (_, count) => count + 1);
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
        _pendingPromptCounts.TryRemove(topicId, out _);
        _audiences.TryRemove(topicId, out _);
        _streamIds.Remove(topicId);
        _dispatching.Remove(topicId);
        if (_queues.Remove(topicId, out var queue) && queue.Count > 0)
        {
            _queueRevision++;
        }

        if (_cancellationTokens.TryRemove(topicId, out var cts))
        {
            cts.Dispose();
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