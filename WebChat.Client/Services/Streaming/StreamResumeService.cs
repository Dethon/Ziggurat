using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Pipeline;

namespace WebChat.Client.Services.Streaming;

public sealed class StreamResumeService(
    IChatMessagingService messagingService,
    ITopicService topicService,
    IApprovalService approvalService,
    IStreamingService streamingService,
    IDispatcher dispatcher,
    IMessagePipeline pipeline,
    TopicStreams topicStreams) : IStreamResumeService
{
    public async Task TryResumeStreamAsync(StoredTopic topic)
    {
        // One question, not three. Nothing back means the topic is already resuming or already
        // streaming, and either way this resume has nothing to do.
        var lease = topicStreams.TryBeginResume(topic.TopicId);
        if (lease is null)
        {
            return;
        }

        // The lease is handed to the stream that takes over the topic. Until that happens it is
        // this method's to release, or a resume that finds nothing would hold the topic forever.
        var handedOver = false;
        try
        {
            handedOver = await ResumeAsync(topic, lease);
        }
        finally
        {
            if (!handedOver)
            {
                lease.Complete();
            }
        }
    }

    private async Task<bool> ResumeAsync(StoredTopic topic, StreamLease lease)
    {
        var streamState = await messagingService.GetStreamStateAsync(topic.TopicId);

        // A null answer already means something real — there is no stream in progress —
        // so not live has to stay its own case rather than fold into the same return.
        if (!streamState.IsLive)
        {
            return false;
        }

        var state = streamState.Value;
        if (state is null || state is { IsProcessing: false, BufferedMessages.Count: 0 })
        {
            return false;
        }

        if (pipeline.MessagesFor(topic.TopicId) is null)
        {
            var history = await topicService.GetHistoryAsync(topic.AgentId, topic.ChatId, topic.ThreadId);
            if (!history.IsLive)
            {
                return false;
            }

            pipeline.LoadHistory(topic.TopicId, history.Value!);
        }

        // The server's answer is the whole truth for this conversation, so it both surfaces
        // a prompt this client never saw and takes away one that was answered or timed out
        // while it was disconnected. A read that could not be made says nothing either way.
        var pendingApproval = await approvalService.GetPendingApprovalForTopicAsync(topic.TopicId);
        if (pendingApproval.IsLive)
        {
            dispatcher.Dispatch(new TopicApprovalsReconciled(topic.TopicId, pendingApproval.Value));
        }

        // Single rebuild: buffer + history → merged result
        var existingHistory = pipeline.MessagesFor(topic.TopicId) ?? [];
        var result = BufferRebuildUtility.ResumeFromBuffer(
            state.BufferedMessages, existingHistory, state.CurrentPrompt, state.CurrentSenderId);

        // Upgrade the resume to a stream before replaying the buffer into it: the upgrade is
        // what creates the live buffer the replay fills. It comes before opening the wire
        // because opening it waits for the reply's next chunk, and the whole point of a resume
        // is that a client joining mid-reply sees the reply now.
        if (!streamingService.TryShowResumedStream(lease, result.StreamingMessage, state.CurrentMessageId))
        {
            return false;
        }

        pipeline.ResumeFromBuffer(result, topic.TopicId, state.CurrentMessageId);

        // A wire that cannot be opened leaves the caller to end the stream, which keeps what
        // was just shown as a message rather than taking it back off the screen.
        return await streamingService.TryReadResumedStreamAsync(lease, topic);
    }
}