using Domain.DTOs.WebChat;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Streaming;

namespace WebChat.Client.Services.Streaming;

public enum TopicStreamPhase
{
    None,
    Resuming,
    Streaming
}

// What a topic's stream currently is, for a caller that needs to ask rather than write.
// Stream is the loop pulling chunks off the wire; Completion finishes when the topic stream
// ends, which is the earlier of the two when the stop button or a delete ended it.
public sealed record TopicStreamSnapshot(
    TopicStreamPhase Phase,
    Task? Stream,
    Task? Completion,
    ChatMessageModel? Message,
    string? CurrentMessageId)
{
    public static TopicStreamSnapshot None { get; } = new(TopicStreamPhase.None, null, null, null, null);

    // Which stream this was a snapshot of. A caller can do nothing with it but hand it back, so
    // "end the stream I saw" stays a question TopicStreams answers rather than a comparison a
    // caller writes.
    internal StreamLease? Of { get; init; }

    public bool HasStream => Phase is not TopicStreamPhase.None;

    public bool IsResuming => Phase is TopicStreamPhase.Resuming;

    public bool IsStreaming => Phase is TopicStreamPhase.Streaming;
}

// What appending gives back: the assistant message accumulated so far, and whether this chunk
// added anything to it. A caller that keeps its own copy of either is keeping a second truth.
public readonly record struct StreamAppend(ChatMessageModel Message, bool IsNew)
{
    public static StreamAppend Nothing { get; } = new(new ChatMessageModel { Role = "assistant" }, false);
}

// A topic stream is a topic's one reply in flight, from the send or resume that opened it to
// its single ending. This module holds one record per topic and is the only thing that moves a
// topic between having no stream, resuming and streaming. It is also the only writer of the
// streaming slice of state, which is the projection it publishes for rendering — see
// docs/adr/0017.
public sealed class TopicStreams(IDispatcher dispatcher, MessagesStore messagesStore)
{
    private readonly Dictionary<string, TopicStream> _byTopic = [];
    private readonly Lock _lock = new();

    // Null means the topic already has a stream. The caller then holds nothing, and holding
    // nothing is the only way to be unable to open a second reply over a live one.
    public StreamLease? TryOpen(
        string topicId,
        ChatMessageModel message,
        string? currentMessageId,
        Func<StreamLease, Task> run)
    {
        StreamLease lease;
        lock (_lock)
        {
            if (_byTopic.ContainsKey(topicId))
            {
                return null;
            }

            lease = new StreamLease(this, topicId);
            _byTopic[topicId] = new TopicStream(lease)
            {
                Phase = TopicStreamPhase.Streaming,
                Message = message,
                CurrentMessageId = currentMessageId
            };
        }

        // StreamStarted resets the buffer, so it goes out before the first chunk can arrive.
        dispatcher.Dispatch(new StreamStarted(topicId));
        Attach(lease, run);
        return lease;
    }

    // A resume claims the topic before it knows whether there is anything to resume, so two
    // reconnects in a row cannot both decide to resume the same reply.
    public StreamLease? TryBeginResume(string topicId)
    {
        lock (_lock)
        {
            if (_byTopic.ContainsKey(topicId))
            {
                return null;
            }

            var lease = new StreamLease(this, topicId);
            _byTopic[topicId] = new TopicStream(lease) { Phase = TopicStreamPhase.Resuming };
            return lease;
        }
    }

    // The resume found a reply in progress: the same record becomes the stream, in place, with
    // what the reply has written so far as its accumulator. This happens before the resume has
    // a wire to read, because attaching to one waits for the reply's next chunk and between
    // chunks — a tool call, a slow first token — that can be minutes away. What the server
    // already said the reply had written is showable now.
    public bool TryShowResumed(StreamLease lease, ChatMessageModel message, string? currentMessageId)
    {
        lock (_lock)
        {
            if (Held(lease) is not { Phase: TopicStreamPhase.Resuming } stream)
            {
                return false;
            }

            stream.Phase = TopicStreamPhase.Streaming;
            stream.Message = message;
            stream.CurrentMessageId = currentMessageId;
        }

        dispatcher.Dispatch(new StreamStarted(lease.TopicId));
        return true;
    }

    // The loop that reads the wire, on the stream a resume is already showing. Opening one and
    // reading it are two moments for a resume, so they are two calls.
    public void Read(StreamLease lease, Func<StreamLease, Task> run) => Attach(lease, run);

    public TopicStreamSnapshot Snapshot(string topicId)
    {
        lock (_lock)
        {
            return _byTopic.TryGetValue(topicId, out var stream)
                ? new TopicStreamSnapshot(
                    stream.Phase,
                    stream.Stream,
                    stream.Lease.Completion,
                    stream.Message,
                    stream.CurrentMessageId)
                { Of = stream.Lease }
                : TopicStreamSnapshot.None;
        }
    }

    // The verbs below are for callers that legitimately touch a topic stream without having
    // opened it: another person's message, the stop button, a topic being deleted. Each does
    // nothing on a topic with no reply in flight, which is where "a chunk for an idle topic" is
    // answered, once.

    // Shows the accumulator a resume rebuilt in the live buffer. Nothing to publish on a topic
    // with no reply in flight, and nothing new to publish on one that has written nothing.
    public void PublishCurrent(string topicId)
    {
        ChatMessageModel message;
        string? messageId;
        lock (_lock)
        {
            if (Streaming(topicId) is not { Message.HasContent: true } stream)
            {
                return;
            }

            message = stream.Message;
            messageId = stream.CurrentMessageId;
        }

        Publish(topicId, new Grown(new StreamAppend(message, true), messageId));
    }

    // Closing off the half-written bubble does not end the message: the reply can carry on
    // writing the same id. What is committed here leaves the accumulator, so the stream keeps
    // it as the half that already has a bubble — an update for that message carries both.
    public void FinalizeCurrent(string topicId)
    {
        ChatMessageModel finished;
        string? messageId;
        lock (_lock)
        {
            if (Streaming(topicId) is not { Message.HasContent: true } stream)
            {
                return;
            }

            messageId = stream.CurrentMessageId;
            finished = Whole(stream, messageId, stream.Message);
            stream.Message = new ChatMessageModel { Role = "assistant" };
            if (messageId is not null)
            {
                stream.Committed[messageId] = finished;
            }
        }

        Commit(topicId, finished, messageId);
        dispatcher.Dispatch(new ResetStreamingContent(topicId));
    }

    // Ends the stream a caller saw earlier, and only that one. A caller that has been away —
    // the send, over its round trip — may come back to a topic claimed since by a resume, which
    // takes no lock precisely so it can claim one; that reply is not this caller's to end.
    // Nothing was seen means nothing to end.
    public void EndIfUnchanged(string topicId, TopicStreamSnapshot seen)
    {
        TopicStream? stream;
        lock (_lock)
        {
            stream = _byTopic.GetValueOrDefault(topicId);
            if (stream is null || seen.Of is null || !ReferenceEquals(stream.Lease, seen.Of))
            {
                return;
            }

            _byTopic.Remove(topicId);
        }

        Close(stream);
    }

    public void End(string topicId)
    {
        TopicStream? stream;
        lock (_lock)
        {
            stream = _byTopic.GetValueOrDefault(topicId);
            _byTopic.Remove(topicId);
        }

        Close(stream);
    }

    internal StreamAppend Append(StreamLease lease, ChatStreamMessage chunk)
    {
        Grown grown;
        lock (_lock)
        {
            grown = Grow(Streaming(Held(lease)), chunk);
        }

        Publish(lease.TopicId, grown);
        return grown.Append;
    }

    // The current message already has a bubble of its own, so the accumulation stays out of the
    // live buffer and the caller updates that bubble instead. Two live copies of one message is
    // what the single-live-bubble look exists to avoid.
    internal StreamAppend AppendToCommittedMessage(StreamLease lease, ChatStreamMessage chunk)
    {
        lock (_lock)
        {
            if (Streaming(Held(lease)) is not { } stream)
            {
                return StreamAppend.Nothing;
            }

            var grown = Grow(stream, chunk);
            return grown.Append with { Message = Whole(stream, grown.MessageId, grown.Append.Message) };
        }
    }

    // A turn boundary. Returns the message this stream was writing, so a caller that wants to
    // come back to it can keep it; null when the lease is stale or there was nothing to commit.
    internal ChatMessageModel? StartMessage(StreamLease lease, string? messageId, ChatMessageModel? resume)
    {
        ChatMessageModel? finished;
        string? finishedId;
        bool wasWriting;
        lock (_lock)
        {
            if (Streaming(Held(lease)) is not { } stream)
            {
                return null;
            }

            finishedId = stream.CurrentMessageId;
            wasWriting = stream.Message.HasContent;
            finished = wasWriting ? Whole(stream, finishedId, stream.Message) : null;
            stream.Message = resume ?? new ChatMessageModel { Role = "assistant" };
            stream.CurrentMessageId = messageId;

            // The caller is handed the whole message to bring back later, so the stream stops
            // keeping the half it committed — two keepers of one half would show it twice.
            if (finished is not null && finishedId is not null)
            {
                stream.Committed.Remove(finishedId);
            }
        }

        if (finished is not null)
        {
            Commit(lease.TopicId, finished, finishedId);
        }

        // The buffer showed the message that has just been swapped out, tool call and all, so it
        // is cleared even when nothing was left to commit.
        if (wasWriting)
        {
            dispatcher.Dispatch(new ResetStreamingContent(lease.TopicId));
        }

        return finished;
    }

    internal void Complete(StreamLease lease)
    {
        TopicStream? stream;
        lock (_lock)
        {
            stream = Held(lease);
            if (stream is not null)
            {
                _byTopic.Remove(lease.TopicId);
            }
        }

        Close(stream);
    }

    internal string? CurrentMessageIdOf(StreamLease lease)
    {
        lock (_lock)
        {
            return Held(lease)?.CurrentMessageId;
        }
    }

    private void Attach(StreamLease lease, Func<StreamLease, Task> run)
    {
        var task = run(lease);
        lock (_lock)
        {
            var stream = Held(lease);
            if (stream is not null)
            {
                stream.Stream = task;
            }
        }
    }

    // Ending is one path whichever way it was reached: whatever the reply had written is kept
    // as a message, the topic goes back to having no stream, and the lease that opened it can
    // no longer do anything.
    private void Close(TopicStream? stream)
    {
        if (stream is null)
        {
            return;
        }

        if (stream.Phase is TopicStreamPhase.Streaming)
        {
            Commit(
                stream.Lease.TopicId,
                Whole(stream, stream.CurrentMessageId, stream.Message),
                stream.CurrentMessageId);
            dispatcher.Dispatch(new StreamCompleted(stream.Lease.TopicId));
        }

        stream.Lease.MarkEnded();
    }

    private static Grown Grow(TopicStream? stream, ChatStreamMessage chunk)
    {
        if (stream is null)
        {
            return new Grown(StreamAppend.Nothing, null);
        }

        var before = stream.Message;
        var after = BufferRebuildUtility.AccumulateChunk(before, chunk);

        stream.Message = after;
        if (chunk.MessageId is not null)
        {
            stream.CurrentMessageId = chunk.MessageId;
        }

        var isNew =
            after.Content.Length > before.Content.Length ||
            (after.Reasoning?.Length ?? 0) > (before.Reasoning?.Length ?? 0) ||
            (after.ToolCalls?.Length ?? 0) > (before.ToolCalls?.Length ?? 0);

        return new Grown(new StreamAppend(after, isNew), stream.CurrentMessageId);
    }

    // A message the stream closed off mid-reply lives in two halves: the one that already has a
    // bubble and the one the accumulator has written since. Anything that hands the message on
    // as a whole — a commit, an update to the bubble — has to join them back together.
    private static ChatMessageModel Whole(TopicStream stream, string? messageId, ChatMessageModel written) =>
        messageId is not null && stream.Committed.TryGetValue(messageId, out var shown)
            ? Joined(shown, written)
            : written;

    private static ChatMessageModel Joined(ChatMessageModel first, ChatMessageModel second) => first with
    {
        Content = first.Content + second.Content,
        Reasoning = Joined(first.Reasoning, second.Reasoning, ""),
        ToolCalls = Joined(first.ToolCalls, second.ToolCalls, "\n"),
        MessageId = second.MessageId ?? first.MessageId,
        Timestamp = second.Timestamp ?? first.Timestamp
    };

    private static string? Joined(string? first, string? second, string separator) =>
        string.IsNullOrEmpty(first) ? second
            : string.IsNullOrEmpty(second) ? first
                : first + separator + second;

    private void Publish(string topicId, Grown grown)
    {
        if (!grown.Append.IsNew)
        {
            return;
        }

        var message = grown.Append.Message;
        dispatcher.Dispatch(new StreamChunk(
            topicId, message.Content, message.Reasoning, message.ToolCalls, grown.MessageId));
    }

    private void Commit(string topicId, ChatMessageModel message, string? messageId)
    {
        if (!message.HasContent)
        {
            return;
        }

        if (messagesStore.State.IsFinalized(topicId, messageId))
        {
            dispatcher.Dispatch(new UpdateMessage(topicId, messageId!, message));
            return;
        }

        // AddMessage records the id, so the next read sees the message as committed.
        dispatcher.Dispatch(new AddMessage(topicId, message, messageId));
    }

    private TopicStream? Held(StreamLease lease) =>
        _byTopic.TryGetValue(lease.TopicId, out var stream) && ReferenceEquals(stream.Lease, lease)
            ? stream
            : null;

    private TopicStream? Streaming(string topicId) => Streaming(_byTopic.GetValueOrDefault(topicId));

    private static TopicStream? Streaming(TopicStream? stream) =>
        stream is { Phase: TopicStreamPhase.Streaming } ? stream : null;

    private readonly record struct Grown(StreamAppend Append, string? MessageId);

    private sealed class TopicStream(StreamLease lease)
    {
        public StreamLease Lease { get; } = lease;

        public TopicStreamPhase Phase { get; set; }

        public Task? Stream { get; set; }

        public ChatMessageModel Message { get; set; } = new() { Role = "assistant" };

        public string? CurrentMessageId { get; set; }

        // Per message id, the half this stream committed and took out of the accumulator.
        public Dictionary<string, ChatMessageModel> Committed { get; } = [];
    }
}