using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.AgentSettings;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;

namespace WebChat.Client.Services.Streaming;

public sealed class StreamingService(
    IChatMessagingService messagingService,
    IDispatcher dispatcher,
    ITopicService topicService,
    TopicsStore topicsStore,
    MessagesStore messagesStore,
    AgentSettingsStore agentSettingsStore,
    ComposerStore composerStore,
    TopicStreams topicStreams) : IStreamingService
{
    // Serialises deciding whether to open a stream and opening it. TopicStreams answers who
    // holds a topic, but that answer is only atomic on its own; the send has a round trip
    // between asking and acting.
    private readonly SemaphoreSlim _streamLock = new(1, 1);

    public async Task SendMessageAsync(
        StoredTopic topic,
        string message,
        string? correlationId = null,
        IReadOnlyList<AttachmentReference>? attachments = null)
    {
        await _streamLock.WaitAsync();
        try
        {
            var configPatch = GetConfigPatch(topic);
            // Read before the send and cleared after it, so an attachment cannot ride a second
            // message: the composer is emptied by the same action that sends its contents.
            // A retry brings its own references and leaves the composer alone; everything else
            // sends what the composer holds and empties exactly that.
            var sending = attachments is null ? ReadyAttachments(topic.TopicId) : [];
            attachments ??= References(sending);

            // Read once, and carried into the send: the round trip below is long enough for a
            // resume to claim the topic, and what may be ended afterwards is the stream this
            // decision was made about, never whatever holds the topic by then.
            var seen = topicStreams.Snapshot(topic.TopicId);
            if (!seen.HasStream)
            {
                await StartNewStreamAsync(
                    topic, message, correlationId, configPatch, attachments, sending, seen);
                return;
            }

            var enqueued = await messagingService.EnqueueMessageAsync(
                topic.TopicId, message, correlationId, configPatch, attachments);

            // A not-live enqueue is not the server saying "there is no stream to enqueue
            // onto". Falling through here would open a second stream over a transport that
            // cannot carry it and show the user a reply that has already failed.
            if (!enqueued.IsLive)
            {
                dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
                return;
            }

            if (!enqueued.Value)
            {
                await StartNewStreamAsync(
                    topic, message, correlationId, configPatch, attachments, sending, seen);
                return;
            }

            ClearSent(topic.TopicId, sending);
        }
        finally
        {
            _streamLock.Release();
        }
    }

    private AgentConfigPatch? GetConfigPatch(StoredTopic topic) =>
        AgentSettingsSelectors.GetConfigPatch(
            agentSettingsStore.State, topicsStore.State.Agents, topic.AgentId);

    // Only the files that finished. One still uploading has no reference to send, and a refused
    // one never will; both stay in the composer rather than silently going along.
    private IReadOnlyList<ComposerAttachment> ReadyAttachments(string topicId) =>
        composerStore.State.For(topicId)
            .Where(a => a is { Status: AttachmentStatus.Ready, Reference: not null })
            .ToList();

    private static IReadOnlyList<AttachmentReference>? References(
        IReadOnlyList<ComposerAttachment> attachments) =>
        attachments.Count == 0 ? null : attachments.Select(a => a.Reference!).ToList();

    // By local id, so a file picked during the send's round trip stays in the composer instead of
    // being swept away with the ones that travelled.
    private void ClearSent(string topicId, IReadOnlyList<ComposerAttachment> sent)
    {
        if (sent.Count > 0)
        {
            dispatcher.Dispatch(new ClearAttachments(topicId, sent.Select(a => a.LocalId).ToList()));
        }
    }

    // The lease already holds the topic, so there is nothing left to decide here and no lock to
    // take: the resume that was granted it is the only one that can get this far.
    public bool TryShowResumedStream(
        StreamLease lease, ChatMessageModel streamingMessage, string? startMessageId) =>
        topicStreams.TryShowResumed(lease, streamingMessage, startMessageId);

    // Opening the stream is what waits for the reply's next chunk, so it happens after the
    // reply is already showing. Recovery the user never asked for: nothing is said when it
    // cannot be opened — the caller ends the stream, which keeps what was shown as a message.
    public async Task<bool> TryReadResumedStreamAsync(StreamLease lease, StoredTopic topic)
    {
        var chunks = await messagingService.ResumeStreamAsync(topic.TopicId);
        if (!chunks.IsLive)
        {
            return false;
        }

        topicStreams.Read(lease, running => ProcessStreamAsync(topic, chunks.Value!, running));
        return true;
    }

    private async Task StartNewStreamAsync(
        StoredTopic topic,
        string message,
        string? correlationId,
        AgentConfigPatch? configPatch,
        IReadOnlyList<AttachmentReference>? attachments,
        IReadOnlyList<ComposerAttachment> sending,
        TopicStreamSnapshot seen)
    {
        var chunks = await OpenSendStreamAsync(topic, message, correlationId, configPatch, attachments);

        // Open only a stream that has actually started. The old order announced first and
        // discovered afterwards, which is how a user was shown a reply that never spoke.
        if (chunks is null)
        {
            return;
        }

        ClearSent(topic.TopicId, sending);

        // Reached either on an idle topic, where there was nothing to end, or after the server
        // said there was nothing to enqueue onto — which means the reply we saw is over. Ending
        // that one keeps what it wrote and frees the topic for this one.
        topicStreams.EndIfUnchanged(topic.TopicId, seen);

        // Nothing back means a resume claimed the topic while this send was waiting, so the
        // reply is already being read. Both readers are on the topic's one stream server side,
        // so the one holding the topic delivers what this send asked for too.
        topicStreams.TryOpen(
            topic.TopicId,
            new ChatMessageModel { Role = "assistant" },
            currentMessageId: null,
            lease => ProcessStreamAsync(topic, chunks, lease));
    }

    // Null means the send could not be made and the user has been told. The send is theirs, so
    // this is the one stream verb that raises a toast.
    private async Task<IAsyncEnumerable<ChatStreamMessage>?> OpenSendStreamAsync(
        StoredTopic topic, string message, string? correlationId, AgentConfigPatch? configPatch,
        IReadOnlyList<AttachmentReference>? attachments)
    {
        var chunks = await messagingService.SendMessageAsync(
            topic.TopicId, message, correlationId, configPatch, attachments);
        if (chunks.IsLive)
        {
            return chunks.Value!;
        }

        dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
        return null;
    }

    private async Task ProcessStreamAsync(
        StoredTopic topic,
        IAsyncEnumerable<ChatStreamMessage> chunks,
        StreamLease lease)
    {
        // The agent's stream can interleave chunks from different assistant messages: a later
        // message's tool-call display races ahead of an earlier message's content (which lags
        // via send_reply), so MessageIds bounce instead of arriving contiguously. We keep the
        // message we were writing under its MessageId so a revisit can continue appending, and
        // we route late chunks for an already-committed MessageId through UpdateMessage
        // (merging the bubble in place) instead of a fresh AddMessage that AddMessageWithDedup
        // would drop. This is display state for a message, not state about the topic's stream,
        // so it stays here.
        var stash = new Dictionary<string, ChatMessageModel>();

        try
        {
            await foreach (var chunk in chunks)
            {
                if (chunk.ApprovalRequest is not null)
                {
                    dispatcher.Dispatch(new ShowApproval(topic.TopicId, chunk.ApprovalRequest));
                    continue;
                }

                if (chunk.Error is not null)
                {
                    if (!TransientErrorFilter.IsTransientErrorMessage(chunk.Error))
                    {
                        dispatcher.Dispatch(new ShowError(chunk.Error));
                        dispatcher.Dispatch(new AddMessage(topic.TopicId, CreateErrorMessage(chunk.Error)));
                    }

                    continue;
                }

                var currentMessageId = lease.CurrentMessageId;

                // A user message closes off the assistant content written so far; what follows
                // it is a new response, and HandleUserMessage adds the user's own bubble.
                if (chunk.UserMessage is not null)
                {
                    lease.StartMessage(currentMessageId);
                    continue;
                }

                if (chunk.MessageId != currentMessageId && currentMessageId is not null)
                {
                    var finished = lease.StartMessage(chunk.MessageId, Stashed(stash, chunk.MessageId));
                    if (finished is not null)
                    {
                        stash[currentMessageId] = finished;
                    }
                }

                var messageId = chunk.MessageId ?? lease.CurrentMessageId;
                var committed = messagesStore.State.IsFinalized(topic.TopicId, messageId);

                // For an already-committed MessageId revisited mid-stream, update its bubble in
                // place; the live streaming buffer is only used for the current uncommitted
                // accumulator, preserving the single-live-bubble look in the contiguous case.
                var append = committed
                    ? lease.AppendToCommittedMessage(chunk)
                    : lease.Append(chunk);

                if (!append.IsNew)
                {
                    continue;
                }

                if (committed)
                {
                    dispatcher.Dispatch(new UpdateMessage(topic.TopicId, messageId!, append.Message));
                }

                await UpdateLastReadMessage(topic, chunk);
            }
        }
        catch (Exception ex) when (!TransientErrorFilter.IsTransientException(ex))
        {
            dispatcher.Dispatch(new ShowError(ex.Message));
            dispatcher.Dispatch(new AddMessage(topic.TopicId, CreateErrorMessage(ex.Message)));
        }
        catch
        {
            // Transient errors silently ignored - reconnection handles recovery
        }
        finally
        {
            // The single ending. A lease that no longer holds the topic — because the stop
            // button or a delete already ended this stream — changes nothing here.
            lease.Complete();
        }
    }

    private static ChatMessageModel? Stashed(Dictionary<string, ChatMessageModel> stash, string? messageId) =>
        messageId is not null ? stash.GetValueOrDefault(messageId) : null;

    private static ChatMessageModel CreateErrorMessage(string content) => new()
    {
        Role = "assistant",
        Content = content,
        IsError = true,
        Timestamp = DateTimeOffset.UtcNow
    };

    private async Task UpdateLastReadMessage(StoredTopic topic, ChatStreamMessage chunk)
    {
        var currentTopic = topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topic.TopicId);
        if (currentTopic is null || chunk.MessageId is null)
        {
            return;
        }

        var isActivelyViewed = topicsStore.State.SelectedTopicId == topic.TopicId;
        var lastReadMsgId = isActivelyViewed ? chunk.MessageId : currentTopic.LastReadMessageId;

        if (lastReadMsgId is not null && lastReadMsgId == currentTopic.LastReadMessageId)
        {
            return;
        }

        var metadata = currentTopic.ToMetadata() with
        {
            LastMessageAt = DateTimeOffset.UtcNow,
            LastReadMessageId = lastReadMsgId
        };

        var updatedTopic = StoredTopic.FromMetadata(metadata);
        dispatcher.Dispatch(new UpdateTopic(updatedTopic));
        await topicService.SaveTopicAsync(metadata);
    }
}