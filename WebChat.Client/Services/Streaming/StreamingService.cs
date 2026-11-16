using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.SignalR;
using WebChat.Client.Contracts;
using WebChat.Client.Helpers;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.AgentSettings;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
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
    TopicStreams topicStreams,
    IMessagePipeline pipeline) : IStreamingService
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
            var outgoing = Compose(topic, message, correlationId, attachments);

            // Read once, and carried into the send: the round trip below is long enough for a
            // resume to claim the topic, and what may be ended afterwards is the stream this
            // decision was made about, never whatever holds the topic by then.
            var seen = topicStreams.Snapshot(topic.TopicId);
            if (!seen.HasStream)
            {
                await StartNewStreamAsync(outgoing, seen);
                return;
            }

            await EnqueueOrStartAsync(outgoing, seen);
        }
        finally
        {
            _streamLock.Release();
        }
    }

    // A message sent while the topic's reply streams waits in the hub's queue and is drawn by nobody
    // now, so the reply keeps its one bubble: the hub writes it on the stream as its turn starts, and
    // this page draws it there. Decided under the lock, against the stream as it is then: finding none
    // after all, it is sent and drawn as any message is.
    public async Task QueueMessageAsync(
        StoredTopic topic,
        string message,
        string? senderId,
        IReadOnlyList<AttachmentReference>? attachments = null)
    {
        await _streamLock.WaitAsync();
        try
        {
            var outgoing = Compose(topic, message, correlationId: null, attachments);

            var seen = topicStreams.Snapshot(topic.TopicId);
            if (seen.HasStream)
            {
                var queued = await EnqueueAsync(outgoing with { CorrelationId = pipeline.QueueUserMessage() });
                if (queued is null)
                {
                    // Nothing was drawn for it, so its words go back where they were typed; its
                    // files never left the composer.
                    dispatcher.Dispatch(new OfferComposerText(message));
                    return;
                }

                if (queued.Value)
                {
                    ClearSent(outgoing);
                    return;
                }
            }

            // Nothing to queue behind: the reply this page showed is over, and is closed off before
            // the message is drawn under it.
            topicStreams.FinalizeCurrent(topic.TopicId);
            var correlationId = pipeline.SubmitUserMessage(
                topic.TopicId, message, senderId, outgoing.Attachments is { Count: > 0 } references ? references : null);
            await StartNewStreamAsync(outgoing with { CorrelationId = correlationId }, seen);
        }
        finally
        {
            _streamLock.Release();
        }
    }

    // True when the hub queued it, false when nothing was running to queue behind, and null when the
    // person has been told why it went nowhere: not live, or the hub's refusal.
    private async Task<bool?> EnqueueAsync(Outgoing outgoing)
    {
        HubResult<bool> enqueued;
        try
        {
            enqueued = await messagingService.EnqueueMessageAsync(
                outgoing.Topic.TopicId, outgoing.Message, outgoing.CorrelationId,
                outgoing.ConfigPatch, outgoing.Attachments);
        }
        catch (HubException refused)
        {
            dispatcher.Dispatch(new ShowError(Helpers.HubRefusal.MessageOf(refused)));
            return null;
        }

        if (!enqueued.IsLive)
        {
            dispatcher.Dispatch(new ShowError(NotLiveToast.Message));
            return null;
        }

        return enqueued.Value;
    }

    public async Task RerunAsync(
        StoredTopic topic,
        string message,
        string rerunOf,
        AgentConfigPatch? configPatch,
        IReadOnlyList<AttachmentReference> attachments)
    {
        await _streamLock.WaitAsync();
        try
        {
            // The hub refuses to rewrite a topic a reply is running on, so there is nothing to enqueue
            // a reroll behind.
            var seen = topicStreams.Snapshot(topic.TopicId);
            if (seen.HasStream)
            {
                return;
            }

            await StartNewStreamAsync(
                new Outgoing(topic, message, null, configPatch ?? GetConfigPatch(topic), attachments, [], rerunOf), seen);
        }
        finally
        {
            _streamLock.Release();
        }
    }

    public async Task ContinueAsync(StoredTopic topic)
    {
        await _streamLock.WaitAsync();
        try
        {
            var seen = topicStreams.Snapshot(topic.TopicId);
            if (seen.HasStream)
            {
                return;
            }

            await StartNewStreamAsync(
                new Outgoing(topic, "", null, GetConfigPatch(topic), null, [], Continuation: true), seen);
        }
        finally
        {
            _streamLock.Release();
        }
    }

    // One message on its way out: what to say, where it goes, and the composer entries the send
    // empties when it lands. Six arguments travelled together through four methods before this.
    // RerunOf: the message the conversation ends on, answered again (a reroll).
    private sealed record Outgoing(
        StoredTopic Topic,
        string Message,
        string? CorrelationId,
        AgentConfigPatch? ConfigPatch,
        IReadOnlyList<AttachmentReference>? Attachments,
        IReadOnlyList<ComposerAttachment> Sending,
        string? RerunOf = null,
        bool Continuation = false);

    private async Task EnqueueOrStartAsync(Outgoing outgoing, TopicStreamSnapshot seen)
    {
        // A not-live enqueue is not the server saying "there is no stream to enqueue onto".
        // Falling through here would open a second stream over a transport that cannot carry it
        // and show the user a reply that has already failed.
        if (await EnqueueAsync(outgoing) is not { } enqueued)
        {
            return;
        }

        if (!enqueued)
        {
            await StartNewStreamAsync(outgoing, seen);
            return;
        }

        ClearSent(outgoing);
    }

    // Read before the send and cleared after it, so an attachment cannot ride a second message. A retry
    // brings its own references and leaves the composer alone; everything else sends what the composer
    // holds and empties exactly that.
    private Outgoing Compose(
        StoredTopic topic, string message, string? correlationId, IReadOnlyList<AttachmentReference>? attachments)
    {
        IReadOnlyList<ComposerAttachment> sending = attachments is null
            ? ComposerSelectors.Ready(composerStore.State.For(topic.TopicId))
            : [];
        return new Outgoing(
            topic, message, correlationId, GetConfigPatch(topic),
            attachments ?? ComposerSelectors.References(sending),
            sending);
    }

    private AgentConfigPatch? GetConfigPatch(StoredTopic topic) =>
        AgentSettingsSelectors.GetConfigPatch(
            agentSettingsStore.State, topicsStore.State.Agents, topic.AgentId);

    // By local id, so a file picked during the send's round trip stays in the composer instead of
    // being swept away with the ones that travelled.
    private void ClearSent(Outgoing sent)
    {
        if (sent.Sending.Count > 0)
        {
            dispatcher.Dispatch(new ClearAttachments(sent.Topic.TopicId, sent.Sending.Select(a => a.LocalId).ToList()));
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
    public async Task<bool> TryReadResumedStreamAsync(StreamLease lease, StoredTopic topic, StreamSeen? seen = null)
    {
        var chunks = await messagingService.ResumeStreamAsync(topic.TopicId, seen);
        if (!chunks.IsLive)
        {
            return false;
        }

        topicStreams.Read(lease, running => ProcessStreamAsync(topic, chunks.Value!, running));
        return true;
    }

    private async Task StartNewStreamAsync(Outgoing outgoing, TopicStreamSnapshot seen)
    {
        var topic = outgoing.Topic;
        var chunks = await OpenSendStreamAsync(outgoing);

        // Open only a stream that has actually started. The old order announced first and
        // discovered afterwards, which is how a user was shown a reply that never spoke.
        if (chunks is null)
        {
            return;
        }

        ClearSent(outgoing);

        // Reached either on an idle topic, where there was nothing to end, or after the server
        // said there was nothing to enqueue onto — which means the reply we saw is over. Ending
        // that one keeps what it wrote and frees the topic for this one.
        topicStreams.EndIfUnchanged(topic.TopicId, seen);

        // Nothing back means a resume claimed the topic while this send was waiting, so the
        // reply is already being read. Both readers are on the topic's one stream server side,
        // so the one holding the topic delivers what this send asked for too — every chunk of a
        // turn. Not what the hub answers instead of a turn: a refusal completes the topic's
        // stream before the resume's wire is open, and "Session not found" is never on it, so
        // this wire is still read for that. A new conversation's first message is this race
        // every time, since selecting the topic starts a resume.
        var opened = topicStreams.TryOpen(
            topic.TopicId,
            new ChatMessageModel { Role = "assistant" },
            currentMessageId: null,
            lease => ProcessStreamAsync(topic, chunks, lease));
        if (opened is null)
        {
            _ = ShowRefusalsOfAsync(topic, chunks, outgoing.CorrelationId);
        }
    }

    // The losing wire's errors, and nothing else: its content is the holder's to show, and the two
    // wires share no numbering to merge chunks by. An error on it is this send's — the hub refusing it
    // as it queued it behind another member's running reply — so it is drawn under the send's own
    // message, never at the end, where it would sit between that member's message and the reply to it.
    private async Task ShowRefusalsOfAsync(
        StoredTopic topic, IAsyncEnumerable<ChatStreamMessage> chunks, string? correlationId)
    {
        try
        {
            await foreach (var chunk in chunks)
            {
                if (chunk.Error is { } error && !TransientErrorFilter.IsTransientErrorMessage(error))
                {
                    ShowRefusalUnder(topic.TopicId, correlationId, error);
                }
            }
        }
        catch
        {
            // The holder's wire answers for the reply; a drop on this one says nothing new.
        }
    }

    private void ShowRefusalUnder(string topicId, string? correlationId, string error)
    {
        var shown = messagesStore.State.MessagesByTopic.GetValueOrDefault(topicId, []).ToList();
        var sent = correlationId is null ? -1 : shown.FindLastIndex(m => m.CorrelationId == correlationId);
        if (correlationId is null || sent < 0)
        {
            ShowErrorOnce(topicId, error);
            return;
        }

        dispatcher.Dispatch(new ShowError(error));
        if (shown.ElementAtOrDefault(sent + 1) is { IsError: true } under && under.Content == error)
        {
            return;
        }

        dispatcher.Dispatch(new AddMessageUnder(topicId, correlationId, CreateErrorMessage(error)));
    }

    // Once, whichever wire it came on: two readers of one topic can both carry the same answer, and
    // the toast store already keeps one of each.
    private void ShowErrorOnce(string topicId, string error)
    {
        dispatcher.Dispatch(new ShowError(error));
        if (messagesStore.State.MessagesByTopic.GetValueOrDefault(topicId) is { Count: > 0 } shown
            && shown[^1] is { IsError: true } last && last.Content == error)
        {
            return;
        }

        dispatcher.Dispatch(new AddMessage(topicId, CreateErrorMessage(error)));
    }

    // Null means the send could not be made and the user has been told. The send is theirs, so
    // this is the one stream verb that raises a toast.
    private async Task<IAsyncEnumerable<ChatStreamMessage>?> OpenSendStreamAsync(Outgoing outgoing)
    {
        var chunks = outgoing.Continuation
            ? await messagingService.SendContinueAsync(outgoing.Topic.TopicId, outgoing.CorrelationId, outgoing.ConfigPatch)
            : outgoing.RerunOf is { } rerunOf
            ? await messagingService.SendRerunAsync(
                outgoing.Topic.TopicId, outgoing.Message, outgoing.CorrelationId,
                outgoing.ConfigPatch, outgoing.Attachments, rerunOf)
            : await messagingService.SendMessageAsync(
                outgoing.Topic.TopicId, outgoing.Message, outgoing.CorrelationId,
                outgoing.ConfigPatch, outgoing.Attachments);
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
                        ShowErrorOnce(topic.TopicId, chunk.Error);
                    }

                    continue;
                }

                var currentMessageId = lease.CurrentMessageId;

                // A user message closes off the assistant content written so far; what follows
                // it is a new response. The page that sent it shows it already; anybody else's —
                // another person in a shared topic, this person on another device — sent at the
                // same moment as this page's own reaches the page here and nowhere else. One the page
                // shows already (a resumed wire opens with the messages its stream carried, which
                // the stream's state drew) is no new message, and closes nothing: taking it as one
                // cut the reply shown so far from the rest of it.
                if (chunk.UserMessage is { } sent)
                {
                    // One this page queued is known by its own id alone: the same words loaded with
                    // the history are an earlier message, never this one.
                    var mine = pipeline.WasSentByThisClient(sent.CorrelationId);
                    var page = messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId, []);
                    var shown = pipeline.WasQueuedByThisClient(sent.CorrelationId)
                        ? page.Any(m => m.CorrelationId == sent.CorrelationId)
                        : BufferRebuildUtility.IsShown(page, sent.SenderId, chunk.Content ?? "", sent.CorrelationId);
                    if (!mine && shown)
                    {
                        continue;
                    }

                    lease.StartMessage(currentMessageId);
                    if (!mine)
                    {
                        dispatcher.Dispatch(new AddMessage(topic.TopicId, new ChatMessageModel
                        {
                            Role = "user",
                            Content = chunk.Content ?? "",
                            CorrelationId = sent.CorrelationId,
                            SenderId = sent.SenderId,
                            SenderName = sent.SenderName,
                            Timestamp = sent.Timestamp,
                            Attachments = chunk.Attachments
                        }));
                    }

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

            // Once the reply is over rather than once per chunk: the count it is read up to is
            // the store's, and until the agent has persisted the turn there is nothing new to
            // be read up to. A topic nobody is looking at keeps its badge.
            await MarkReadIfViewedAsync(topic);

            // The turn was stored before its stream ended, so the history now names what it drew. Not
            // awaited: ending this reader waits on nothing the next turn does not need.
            _ = LearnEntryIdsAsync(topic);

            // The conversation has been written to, so its row has a new preview, a new position
            // and possibly a badge — none of which this client can work out for itself now that
            // the server owns all three.
            dispatcher.Dispatch(new RefreshTopicList());
        }
    }

    private async Task MarkReadIfViewedAsync(StoredTopic topic)
    {
        if (topicsStore.State.SelectedTopicId != topic.TopicId)
        {
            return;
        }

        await topicService.MarkTopicReadAsync(topic.AgentId, topic.ChatId, topic.TopicId);
    }

    // A turn the person stopped is kept by the agent after the stop ended its stream, so the first read
    // can come before it: asked again, a little later each time, until what the page drew is named.
    private static readonly TimeSpan[] LearnAgainAfter =
        [TimeSpan.Zero, TimeSpan.FromMilliseconds(500), TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4)];

    private async Task LearnEntryIdsAsync(StoredTopic topic)
    {
        try
        {
            foreach (var wait in LearnAgainAfter)
            {
                await Task.Delay(wait);
                var history = await topicService.GetHistoryAsync(topic.AgentId, topic.ChatId, topic.ThreadId);
                if (!history.IsLive)
                {
                    return;
                }

                dispatcher.Dispatch(new EntryIdsLearned(topic.TopicId, history.Value!));
                if (messagesStore.State.MessagesByTopic.GetValueOrDefault(topic.TopicId, [])
                    .All(m => m.EntryId is not null || m.IsError || m.NoReply
                              || (string.IsNullOrEmpty(m.Content) && m.Attachments is not { Count: > 0 })))
                {
                    return;
                }
            }
        }
        catch (Exception)
        {
            // Nobody awaits this: unnamed bubbles only lack their actions until the topic is next read.
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

}