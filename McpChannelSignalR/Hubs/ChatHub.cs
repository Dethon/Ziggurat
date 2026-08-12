using System.Runtime.CompilerServices;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Mcp.Hosting;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.AspNetCore.SignalR;

namespace McpChannelSignalR.Hubs;

public sealed class ChatHub(
    SessionService sessionService,
    StreamService streamService,
    ApprovalService approvalService,
    ChannelNotificationEmitter notificationEmitter,
    IAgentCatalog catalog,
    IThreadStateStore threadStore,
    IPushSubscriptionStore pushSubscriptionStore,
    AttachmentService attachmentService,
    DictationSettings dictationSettings,
    ILogger<ChatHub> logger) : Hub
{
    // What the browser is told when the emit reached nobody. The message may still be sitting in a
    // buffer for an agent that comes back, so this says the turn cannot be answered now rather
    // than claiming it was thrown away.
    private const string NotLiveError = "No agent is connected right now, so this message has not been answered.";

    private bool IsRegistered => Context.Items.ContainsKey("UserId");

    private string? CurrentSpaceSlug =>
        Context.Items.TryGetValue("SpaceSlug", out var slug) ? slug as string : null;

    private string? GetRegisteredUserId()
    {
        return Context.Items.TryGetValue("UserId", out var userId)
            ? userId as string
            : null;
    }

    public Task RegisterUser(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new HubException("User ID cannot be empty");
        }

        Context.Items["UserId"] = userId;
        return Task.CompletedTask;
    }

    public IReadOnlyList<AgentCatalogEntry> GetAgents()
    {
        return catalog.GetAll();
    }

    public bool ValidateAgent(string agentId)
    {
        return catalog.Exists(agentId);
    }

    // Liveness probe. Clients invoke this on foreground to confirm the transport is actually
    // alive (a backgrounded mobile connection can report Connected while the socket is dead).
    // A returned value means a real round-trip completed; no registration required.
    public bool Ping() => true;

    public bool StartSession(string agentId, string topicId, long chatId, long threadId, string? topicName = null)
    {
        return sessionService.StartSession(topicId, agentId, chatId, threadId, CurrentSpaceSlug, topicName);
    }

    public async Task JoinSpace(string spaceSlug)
    {
        if (!SpaceConfig.IsValidSlug(spaceSlug))
        {
            throw new HubException("Invalid space slug");
        }

        await SwitchSpaceGroupAsync(spaceSlug);
    }

    private async Task SwitchSpaceGroupAsync(string spaceSlug)
    {
        if (Context.Items.TryGetValue("SpaceSlug", out var previous) && previous is string prevSlug)
        {
            if (prevSlug == spaceSlug)
            {
                return;
            }

            await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"space:{prevSlug}");
        }

        Context.Items["SpaceSlug"] = spaceSlug;
        await Groups.AddToGroupAsync(Context.ConnectionId, $"space:{spaceSlug}");
    }

    // One call, because the composer asks one question: what am I allowed to send. The recording
    // cap and the mis-tap floor ride along so changing either needs no client deploy.
    public AttachmentLimits GetAttachmentLimits() => attachmentService.Limits with
    {
        MaxDictationMs = (int)dictationSettings.MaxLength.TotalMilliseconds,
        MinDictationMs = (int)dictationSettings.MinLength.TotalMilliseconds
    };

    // No topic and no session: a dictation lands in the composer, so it must be possible on a
    // screen where no conversation has been started yet.
    public DictationTicket CreateDictationTicket()
    {
        if (!IsRegistered)
        {
            throw new HubException("User not registered. Call RegisterUser first.");
        }

        return attachmentService.MintDictation(CurrentSpaceSlug ?? AttachmentService.SpaceDefault);
    }

    // The ticket is scoped to the topic being composed in, so a caller can only put bytes against
    // a conversation the connection already has a session for.
    public UploadTicket CreateUploadTicket(string topicId)
    {
        if (!IsRegistered)
        {
            throw new HubException("User not registered. Call RegisterUser first.");
        }

        if (!sessionService.TryGetSession(topicId, out var session) || session is null)
        {
            throw new HubException("Session not found. Please start a session first.");
        }

        return attachmentService.MintUpload(
            topicId, $"{session.ChatId}:{session.ThreadId}", CurrentSpaceSlug ?? AttachmentService.SpaceDefault);
    }

    // Minted when the transcript renders an attachment, not published: one upload store serves
    // every space, so a long-lived URL would be readable by anyone holding it.
    public AttachmentDownload? CreateAttachmentDownload(string attachmentId)
    {
        if (!IsRegistered)
        {
            throw new HubException("User not registered. Call RegisterUser first.");
        }

        return attachmentService.MintDownload(attachmentId, CurrentSpaceSlug ?? AttachmentService.SpaceDefault);
    }

    public bool IsProcessing(string topicId)
    {
        return streamService.IsStreaming(topicId);
    }

    public StreamState? GetStreamState(string topicId)
    {
        return streamService.GetStreamState(topicId);
    }

    public async IAsyncEnumerable<ChatStreamMessage> ResumeStream(
        string topicId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var state = streamService.GetStreamState(topicId);
        if (state is null || !state.IsProcessing)
        {
            yield break;
        }

        var liveStream = streamService.SubscribeToStream(topicId, cancellationToken);
        if (liveStream is null)
        {
            yield break;
        }

        var pendingApproval = approvalService.GetPendingApprovalForTopic(topicId);
        if (pendingApproval is not null)
        {
            yield return new ChatStreamMessage { ApprovalRequest = pendingApproval };
        }

        await foreach (var msg in liveStream.IgnoreCancellation(cancellationToken))
        {
            yield return msg;
        }
    }

    public async IAsyncEnumerable<ChatStreamMessage> SendMessage(
        string topicId,
        string message,
        string? correlationId,
        AgentConfigPatch? configPatch,
        IReadOnlyList<AttachmentReference>? attachments,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!IsRegistered)
        {
            yield return new ChatStreamMessage
            {
                Error = "User not registered. Please call RegisterUser first.",
                IsComplete = true
            };
            yield break;
        }

        if (!sessionService.TryGetSession(topicId, out var session) || session is null)
        {
            yield return new ChatStreamMessage
            {
                Error = "Session not found. Please start a session first.",
                IsComplete = true
            };
            yield break;
        }

        var userId = GetRegisteredUserId() ?? "Anonymous";
        var conversationId = $"{session.ChatId}:{session.ThreadId}";

        var (broadcastChannel, linkedToken) =
            streamService.GetOrCreateStream(topicId, message, userId, cancellationToken);
        streamService.TryIncrementPending(topicId);

        // Subscribe before emitting so no early reply chunks are lost
        var subscription = broadcastChannel.Subscribe();

        // Refused before anything is written or emitted, for the race where the model changes
        // between picking a file and sending it: no turn is created, no agent is woken, and no
        // browser is shown a message that was never taken. The answer goes out on the same
        // stream-error path an undeliverable message uses, so every browser sees the same end.
        if (CapabilityRefusal(session.AgentId, configPatch, attachments) is { } refused)
        {
            await AnswerRefusedAsync(topicId, conversationId, refused);
            await foreach (var refusalChunk in
                subscription.ReadAllAsync(linkedToken).IgnoreCancellation(cancellationToken))
            {
                yield return refusalChunk;
            }

            yield break;
        }

        // Write user message to buffer for other browsers
        var timestamp = DateTimeOffset.UtcNow;
        var userMessage = new ChatStreamMessage
        {
            Content = message,
            UserMessage = new UserMessageInfo(userId, timestamp),
            Attachments = attachments
        };
        await streamService.WriteMessageAsync(topicId, userMessage);

        var delivered = await notificationEmitter.EmitAsync(
            new ChannelMessageNotification
            {
                ConversationId = conversationId,
                Sender = userId,
                Content = message,
                AgentId = session.AgentId,
                ConfigPatch = configPatch,
                Attachments = attachments,
                Timestamp = DateTimeOffset.UtcNow
            },
            cancellationToken);

        if (!delivered)
        {
            // Nobody is polling, so no reply is coming and the loop below would hold the browser
            // open forever. The error goes through the stream rather than straight back to this
            // caller so every browser on the topic sees the same end, and it ends the turn: one
            // pending prompt finished, in error.
            await AnswerNotLiveAsync(topicId, conversationId);
        }

        // Stream responses back to the browser — the loop ends when the channel completes
        // (i.e. when the last pending agent finishes), not on individual IsComplete messages.
        await foreach (var msg in subscription.ReadAllAsync(linkedToken).IgnoreCancellation(cancellationToken))
        {
            yield return msg;
        }
    }

    public async Task<bool> EnqueueMessage(
        string topicId,
        string message,
        string? correlationId,
        AgentConfigPatch? configPatch,
        IReadOnlyList<AttachmentReference>? attachments = null)
    {
        if (!IsRegistered)
        {
            return false;
        }

        if (!sessionService.TryGetSession(topicId, out var session) || session is null)
        {
            return false;
        }

        if (!streamService.TryIncrementPending(topicId))
        {
            return false;
        }

        var userId = GetRegisteredUserId() ?? "Anonymous";
        var conversationId = $"{session.ChatId}:{session.ThreadId}";

        if (CapabilityRefusal(session.AgentId, configPatch, attachments) is { } refused)
        {
            await AnswerRefusedAsync(topicId, conversationId, refused);
            return true;
        }

        var timestamp = DateTimeOffset.UtcNow;
        var userMessage = new ChatStreamMessage
        {
            Content = message,
            UserMessage = new UserMessageInfo(userId, timestamp),
            Attachments = attachments
        };
        await streamService.WriteMessageAsync(topicId, userMessage);

        var delivered = await notificationEmitter.EmitAsync(new ChannelMessageNotification
        {
            ConversationId = conversationId,
            Sender = userId,
            Content = message,
            AgentId = session.AgentId,
            ConfigPatch = configPatch,
            Attachments = attachments,
            Timestamp = DateTimeOffset.UtcNow
        });

        if (!delivered)
        {
            await AnswerNotLiveAsync(topicId, conversationId);
        }

        // True either way, and deliberately: false is the client's signal that there was no stream
        // to enqueue onto, which makes it open a second one and send the same prompt again. The
        // channel took this prompt; what it could not do is find anyone listening, and the stream
        // above has just been told so.
        return true;
    }

    // The composer already blocks this case; this is the guard for the race where the model
    // changed between picking a file and sending it. The agent does not check a third time.
    private string? CapabilityRefusal(
        string? agentId,
        AgentConfigPatch? configPatch,
        IReadOnlyList<AttachmentReference>? attachments)
    {
        if (attachments is not { Count: > 0 })
        {
            return null;
        }

        return AttachmentCapability.Refusal(
            agentId is null ? null : catalog.Get(agentId),
            configPatch?.Model,
            attachments.Select(a => a.MediaType));
    }

    private async Task AnswerRefusedAsync(string topicId, string conversationId, string refusal)
    {
        logger.LogInformation(
            "Refusing a message with attachments for conversation {ConversationId} (topic {TopicId}): {Reason}",
            conversationId, topicId, refusal);

        await streamService.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = conversationId,
            Content = refusal,
            ContentType = ReplyContentType.Error,
            IsComplete = true
        });
    }

    // The not-live answer, in the shape a turn already ends in: an error reply on the topic's
    // stream. That is what releases the pending prompt this call added, so a topic is not left
    // showing "processing" for a reply that is never coming.
    private async Task AnswerNotLiveAsync(string topicId, string conversationId)
    {
        logger.LogWarning(
            "Nothing is polling for conversation {ConversationId} (topic {TopicId}); " +
            "answering not live instead of streaming a reply that cannot arrive",
            conversationId, topicId);

        await streamService.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = conversationId,
            Content = NotLiveError,
            ContentType = ReplyContentType.Error,
            IsComplete = true
        });
    }

    public async Task CancelTopic(string topicId)
    {
        if (sessionService.TryGetSession(topicId, out var session) && session is not null)
        {
            await notificationEmitter.EmitCancelAsync(new ChannelCancelNotification
            {
                ConversationId = $"{session.ChatId}:{session.ThreadId}",
                AgentId = session.AgentId,
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        streamService.CancelStream(topicId);

        // The turn the prompt belonged to is gone, so the prompt goes with it. Left pending it
        // holds the tool call open and keeps the modal on screen over a stream that has ended.
        await approvalService.CancelPendingApprovalsForTopicAsync(topicId);
    }

    public async Task<IReadOnlyList<TopicMetadata>> GetAllTopics(string agentId, string spaceSlug = "default")
    {
        return await threadStore.GetAllTopicsAsync(agentId, spaceSlug);
    }

    public async Task SaveTopic(TopicMetadata topic, bool isNew = false)
    {
        await threadStore.SaveTopicAsync(topic);
    }

    public async Task<IReadOnlyList<ChatHistoryMessage>> GetHistory(string agentId, long chatId, long threadId)
    {
        return await threadStore.GetHistoryAsync(agentId, chatId, threadId);
    }

    public async Task DeleteTopic(string agentId, string topicId, long chatId, long threadId)
    {
        await notificationEmitter.EmitCancelAsync(new ChannelCancelNotification
        {
            ConversationId = $"{chatId}:{threadId}",
            AgentId = agentId,
            Timestamp = DateTimeOffset.UtcNow
        });

        sessionService.EndSession(topicId);
        streamService.CancelStream(topicId);
        await approvalService.CancelPendingApprovalsForTopicAsync(topicId);

        await threadStore.DeleteAsync(new AgentKey($"{chatId}:{threadId}", agentId));
        await threadStore.DeleteTopicAsync(agentId, chatId, topicId);

        // Removing a conversation removes what was in it. The sweep would reach these eventually;
        // deleting a topic is the person saying they want them gone now.
        attachmentService.DeleteConversation($"{chatId}:{threadId}");
    }

    public async Task SubscribePush(PushSubscriptionDto subscription)
    {
        var userId = GetRegisteredUserId()
            ?? throw new HubException("User not registered. Call RegisterUser first.");

        ValidateSubscription(subscription);

        await pushSubscriptionStore.SaveAsync(userId, subscription, CurrentSpaceSlug ?? "default");
    }

    public async Task ReplacePushSubscription(PushSubscriptionDto subscription, string oldEndpoint)
    {
        var userId = GetRegisteredUserId()
            ?? throw new HubException("User not registered. Call RegisterUser first.");

        ValidateSubscription(subscription);

        if (string.IsNullOrWhiteSpace(oldEndpoint))
        {
            throw new HubException("Old endpoint is required for replacement.");
        }

        await pushSubscriptionStore.SaveAsync(userId, subscription, CurrentSpaceSlug ?? "default",
            replacingEndpoint: oldEndpoint);
    }

    public async Task UnsubscribePush(string endpoint)
    {
        var userId = GetRegisteredUserId()
            ?? throw new HubException("User not registered. Call RegisterUser first.");
        await pushSubscriptionStore.RemoveAsync(userId, endpoint);
    }

    private static void ValidateSubscription(PushSubscriptionDto subscription)
    {
        if (string.IsNullOrWhiteSpace(subscription.Endpoint)
            || !Uri.TryCreate(subscription.Endpoint, UriKind.Absolute, out var uri)
            || uri.Scheme != "https")
        {
            throw new HubException("Endpoint must be a valid HTTPS URL.");
        }

        if (string.IsNullOrWhiteSpace(subscription.P256dh) || string.IsNullOrWhiteSpace(subscription.Auth))
        {
            throw new HubException("P256dh and Auth keys are required.");
        }
    }

    public Task<bool> RespondToApprovalAsync(string approvalId, ToolApprovalResult result)
    {
        return Task.FromResult(RespondToApproval(approvalId, result));
    }

    private bool RespondToApproval(string approvalId, ToolApprovalResult result)
    {
        // Fire and forget - the approval service will resolve the TCS
        _ = approvalService.RespondToApprovalAsync(approvalId, result.ToString());
        return true;
    }

    public bool IsApprovalPending(string approvalId)
    {
        return approvalService.IsApprovalPending(approvalId);
    }

    public ToolApprovalRequestMessage? GetPendingApprovalForTopic(string topicId)
    {
        return approvalService.GetPendingApprovalForTopic(topicId);
    }
}