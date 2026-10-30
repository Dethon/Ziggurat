using System.Runtime.CompilerServices;
using Domain.Agents;
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Domain.Security;
using Mcp.Hosting;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Internal;
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
    IHubNotificationSender hubSender,
    RetentionSettings retention,
    DictationSettings dictationSettings,
    ILogger<ChatHub> logger) : Hub
{
    // What the browser is told when the emit reached nobody. The message may still be sitting in a
    // buffer for an agent that comes back, so this says the turn cannot be answered now rather
    // than claiming it was thrown away.
    private const string NotLiveError = "No agent is connected right now, so this message has not been answered.";

    // Where the connection keeps the person the proxy's session check named as it arrived, which is
    // also what says it may call anything (RegisteredConnectionFilter).
    internal const string AccountItem = "Account";

    // Where it keeps the name that account goes by, asked of the accounts service as it arrived;
    // absent when the service could not say.
    internal const string DisplayNameItem = "DisplayName";

    // This channel's own name, as the agent's channel endpoints call it and as a principal names the
    // channel that vouched for it.
    internal const string ChannelId = "signalr";

    // The one answer for a topic the caller may not see, worded as for one that does not exist, so
    // naming another person's private topic tells the namer nothing about it — not even that it is.
    private const string NoSuchConversation = "No such conversation.";

    // Seeing a topic is not writing in it: someone who sees every topic of the household reads
    // another person's private one and changes nothing in it.
    private const string ReadOnlyConversation = "Only the person whose conversation this is can change it until they share it.";

    private string? CurrentSpaceSlug =>
        Context.Items.TryGetValue("SpaceSlug", out var slug) ? slug as string : null;

    // Set as the connection arrives; RegisteredConnectionFilter admits no call on one without it.
    private ForwardedAccount Account => (ForwardedAccount)Context.Items[AccountItem]!;

    private string RegisteredUserId => Account.UserId;

    private TopicViewer Viewer => Account.Viewer;

    // Who a turn sent from here acts for: the account itself, never the name, which only labels it.
    private Principal TurnPrincipal => Account.PrincipalOn(ChannelId);

    // What a turn's sender shows the agent: the account's name, or its id when no name was learned.
    private string TurnSender => Context.Items.TryGetValue(DisplayNameItem, out var name) && name is string { Length: > 0 } shown
        ? shown
        : Account.UserId;

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
    // A returned value means a real round-trip completed.
    public bool Ping() => true;

    // Answered no, as a session the server will not start always was: for another person's private
    // topic, for a conversation some other topic holds, and for one no topic holds that is not the
    // conversation this topic's own id names — another channel's, which nobody reads by naming it.
    public async Task<bool> StartSession(string agentId, string topicId, long chatId, long threadId, string? topicName = null)
    {
        var held = await threadStore.GetTopicByChatIdAndThreadIdAsync(agentId, chatId, threadId);
        var mayOpen = held is null
            ? IsItsOwnConversation(topicId, chatId, threadId)
            : held.TopicId == topicId && Viewer.Sees(held);
        return mayOpen && sessionService.StartSession(topicId, agentId, chatId, threadId, CurrentSpaceSlug, topicName);
    }

    // A conversation the chat client opens is the one its topic's id names, so a topic that holds
    // nothing yet can only ever be over a conversation nobody else has.
    private static bool IsItsOwnConversation(string topicId, long chatId, long threadId) =>
        ConversationIdGenerator.CreateFor(topicId) == new ConversationIdentity(chatId, threadId);

    private void RequireSees(TopicMetadata? topic)
    {
        if (topic is not null && !Viewer.Sees(topic))
        {
            throw new HubException(NoSuchConversation);
        }
    }

    private void RequireMayChange(TopicMetadata? topic)
    {
        RequireSees(topic);
        if (topic is not null && !Viewer.MayChange(topic))
        {
            throw new HubException(ReadOnlyConversation);
        }
    }

    // Sessions are this process's and every connection's, so one existing says nothing about who
    // opened it: the topic its conversation belongs to says who may act on it. A session over a
    // conversation no topic holds yet is a topic its opener has not saved, whose id only they know.
    private async Task<ChannelSession?> SessionOfAsync(string topicId, bool toChange = false)
    {
        if (!sessionService.TryGetSession(topicId, out var session) || session is null)
        {
            return null;
        }

        var topic = await threadStore.GetTopicByChatIdAndThreadIdAsync(session.AgentId, session.ChatId, session.ThreadId);
        if (toChange)
        {
            RequireMayChange(topic);
        }
        else
        {
            RequireSees(topic);
        }

        return session;
    }

    public async Task JoinSpace(string spaceSlug)
    {
        if (!SpaceConfig.IsValidSlug(spaceSlug))
        {
            throw new HubException("Invalid space slug");
        }

        await SwitchSpaceGroupAsync(spaceSlug);
    }

    // A connection hears its space's news, and a private topic's only as its owner or as someone who
    // sees every topic (TopicAudience).
    private async Task SwitchSpaceGroupAsync(string spaceSlug)
    {
        if (Context.Items.TryGetValue("SpaceSlug", out var previous) && previous is string prevSlug)
        {
            if (prevSlug == spaceSlug)
            {
                return;
            }

            await Task.WhenAll(TopicAudience.GroupsOf(Account, prevSlug)
                .Select(group => Groups.RemoveFromGroupAsync(Context.ConnectionId, group)));
        }

        Context.Items["SpaceSlug"] = spaceSlug;
        await Task.WhenAll(TopicAudience.GroupsOf(Account, spaceSlug)
            .Select(group => Groups.AddToGroupAsync(Context.ConnectionId, group)));
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
        return attachmentService.MintDictation(CurrentSpaceSlug ?? AttachmentService.SpaceDefault);
    }

    // The ticket is scoped to the topic being composed in, so a caller can only put bytes against
    // a conversation the connection already has a session for.
    public async Task<UploadTicket> CreateUploadTicket(string topicId)
    {
        if (await SessionOfAsync(topicId, toChange: true) is not { } session)
        {
            throw new HubException("Session not found. Please start a session first.");
        }

        return attachmentService.MintUpload(
            topicId, session.Identity, CurrentSpaceSlug ?? AttachmentService.SpaceDefault);
    }

    // Minted when the transcript renders an attachment, not published: one upload store serves
    // every space, so a long-lived URL would be readable by anyone holding it. The file's
    // conversation is held to the same rule as its topic; the file names no agent, so it is the
    // topic any registered agent holds it under.
    public async Task<AttachmentDownload?> CreateAttachmentDownload(string attachmentId)
    {
        if (attachmentService.ConversationOf(attachmentId) is { } conversationId
            && ConversationIdentity.Parse(conversationId) is { } conversation)
        {
            var holders = await Task.WhenAll((catalog.GetAll() ?? []).Select(agent =>
                threadStore.GetTopicByChatIdAndThreadIdAsync(agent.Id, conversation.ChatId, conversation.ThreadId)));
            holders.ToList().ForEach(RequireSees);
        }

        return attachmentService.MintDownload(attachmentId, CurrentSpaceSlug ?? AttachmentService.SpaceDefault);
    }

    public async Task<bool> IsProcessing(string topicId)
    {
        await SessionOfAsync(topicId);
        return streamService.IsStreaming(topicId);
    }

    public async Task<StreamState?> GetStreamState(string topicId)
    {
        await SessionOfAsync(topicId);
        return streamService.GetStreamState(topicId);
    }

    public async IAsyncEnumerable<ChatStreamMessage> ResumeStream(
        string topicId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await SessionOfAsync(topicId);
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
        if (await SessionOfAsync(topicId, toChange: true) is not { } session)
        {
            yield return new ChatStreamMessage
            {
                Error = "Session not found. Please start a session first.",
                IsComplete = true
            };
            yield break;
        }

        var userId = RegisteredUserId;
        var conversationId = session.Identity.ConversationId;

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
                Sender = TurnSender,
                Principal = TurnPrincipal,
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
        if (await SessionOfAsync(topicId, toChange: true) is not { } session)
        {
            return false;
        }

        if (!streamService.TryIncrementPending(topicId))
        {
            return false;
        }

        var userId = RegisteredUserId;
        var conversationId = session.Identity.ConversationId;

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
            Sender = TurnSender,
            Principal = TurnPrincipal,
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
        if (await SessionOfAsync(topicId, toChange: true) is { } session)
        {
            await notificationEmitter.EmitCancelAsync(new ChannelCancelNotification
            {
                ConversationId = session.Identity.ConversationId,
                AgentId = session.AgentId,
                Timestamp = DateTimeOffset.UtcNow
            });
        }

        streamService.CancelStream(topicId);

        // The turn the prompt belonged to is gone, so the prompt goes with it. Left pending it
        // holds the tool call open and keeps the modal on screen over a stream that has ended.
        await approvalService.CancelPendingApprovalsForTopicAsync(topicId);
    }

    // One page, most recently written first, plus where the next one starts. The page size is
    // the server's to decide unless a caller has a reason of its own, so a client that wants
    // the ordinary sidebar asks for nothing and gets what the retention policy says.
    public async Task<TopicPage> GetTopicPage(
        string agentId,
        string spaceSlug = SpaceConfig.DefaultSlug,
        string? cursor = null,
        int? pageSize = null,
        bool archived = false)
    {
        return WithLiveStreams(await threadStore.GetTopicPageAsync(
            agentId, spaceSlug, Account.Viewer, cursor, pageSize ?? retention.PageSize, archived));
    }

    // Answered here rather than asked per topic: which replies are in flight is this process's
    // own state, so it costs nothing to say alongside the rows it is about. Every page of topics
    // goes through this, searches included — a row is a row however it was found.
    private TopicPage WithLiveStreams(TopicPage page) => page with
    {
        LiveTopicIds = [.. page.Topics.Select(t => t.TopicId).Where(streamService.IsStreaming)]
    };

    // The client's copy of a topic is always a round trip stale, and for a topic last driven by
    // voice or a schedule it is stale by design. The name is the one field a browser may set on
    // an existing topic; everything the server owns is read back before the write, and a rename
    // keeps the TTL where it was — only the conversation's own writes extend its life.
    //
    // A new topic is the connection's person's, private until they share it, whatever the browser
    // sent: whose it is was settled when the connection arrived. It must be over the conversation
    // its own id names, so saving one never adopts a conversation another channel or person has.
    public async Task SaveTopic(TopicMetadata topic, bool isNew = false)
    {
        var stored = await threadStore.GetTopicAsync(topic.AgentId, topic.ChatId, topic.TopicId);
        if (stored is not null)
        {
            RequireMayChange(stored);
            await threadStore.SaveTopicAsync(stored with { Name = topic.Name }, keepTtl: true);
            return;
        }

        if (!IsItsOwnConversation(topic.TopicId, topic.ChatId, topic.ThreadId))
        {
            throw new HubException(NoSuchConversation);
        }

        await threadStore.SaveTopicAsync(topic with
        {
            OwnerId = Account.UserId,
            TenantId = Account.TenantId,
            Shared = false,
            OwnerName = TurnSender
        });
    }

    // Whether the household sees a topic is its owner's to say and nobody else's, a member reading
    // it shared or someone who sees every topic included. The whole space is told: whoever could not
    // see it now can, and on unsharing their list lets it go (TopicViewer, in the client too).
    public async Task SetTopicShared(string agentId, long chatId, string topicId, bool shared)
    {
        var stored = await threadStore.GetTopicAsync(agentId, chatId, topicId)
                     ?? throw new HubException(NoSuchConversation);
        RequireSees(stored);
        if (!Viewer.Owns(stored))
        {
            throw new HubException("Only the person whose conversation this is can share it or stop sharing it.");
        }

        var changed = stored with { Shared = shared };
        await threadStore.SaveTopicAsync(changed, keepTtl: true);
        await hubSender.SendToGroupAsync(
            TopicAudience.Space(changed.SpaceSlug),
            "OnTopicChanged",
            new TopicChangedNotification(TopicChangeType.Updated, topicId, changed, changed.SpaceSlug));
    }

    // On the server, so it covers conversations this client never loaded — which is every
    // conversation past the first page, and the whole archive.
    public async Task<TopicPage> SearchTopics(
        string agentId,
        string query,
        string spaceSlug = SpaceConfig.DefaultSlug,
        string? cursor = null,
        int? pageSize = null)
    {
        return WithLiveStreams(await threadStore.SearchTopicsAsync(
            agentId, spaceSlug, Account.Viewer, query, cursor, pageSize ?? retention.PageSize));
    }

    // Read state is the store's to work out: the count it would be compared against is one round
    // trip newer than anything the browser holds.
    // How far a topic was read is its own people's: someone who sees every topic reading another
    // person's private one leaves it as unread as its owner left it.
    public async Task MarkTopicRead(string agentId, long chatId, string topicId)
    {
        var stored = await threadStore.GetTopicAsync(agentId, chatId, topicId);
        RequireSees(stored);
        if (stored is null || Viewer.MayChange(stored))
        {
            await threadStore.MarkTopicReadAsync(agentId, chatId, topicId);
        }
    }

    // A conversation no topic holds is read by nobody here: it is another channel's, or one this
    // chat has not saved yet and so has nothing in it to read.
    public async Task<IReadOnlyList<ChatHistoryMessage>> GetHistory(string agentId, long chatId, long threadId)
    {
        var held = await threadStore.GetTopicByChatIdAndThreadIdAsync(agentId, chatId, threadId);
        if (held is null)
        {
            return [];
        }

        RequireSees(held);
        return await threadStore.GetHistoryAsync(agentId, chatId, threadId);
    }

    public async Task DeleteTopic(string agentId, string topicId, long chatId, long threadId)
    {
        var stored = await threadStore.GetTopicAsync(agentId, chatId, topicId);
        RequireMayChange(stored);

        // The topic's own conversation and no other: a thread it does not hold is not the caller's
        // to name into the delete.
        if (stored is not null && stored.ThreadId != threadId)
        {
            throw new HubException(NoSuchConversation);
        }

        var spaceSlug = stored?.SpaceSlug ?? CurrentSpaceSlug ?? SpaceConfig.DefaultSlug;
        var notification = new TopicChangedNotification(TopicChangeType.Deleted, topicId, SpaceSlug: spaceSlug);

        // A topic already gone is said to be gone again, and nothing else: with no topic to say
        // whose the conversation is, only one its own id names is this chat's to delete.
        if (stored is null && !IsItsOwnConversation(topicId, chatId, threadId))
        {
            await hubSender.SendToTopicAsync(null, spaceSlug, "OnTopicChanged", notification);
            return;
        }

        var identity = new ConversationIdentity(chatId, threadId);
        await notificationEmitter.EmitCancelAsync(new ChannelCancelNotification
        {
            ConversationId = identity.ConversationId,
            AgentId = agentId,
            Timestamp = DateTimeOffset.UtcNow
        });

        sessionService.EndSession(topicId);
        streamService.CancelStream(topicId);
        await approvalService.CancelPendingApprovalsForTopicAsync(topicId);

        await threadStore.DeleteAsync(new AgentKey(identity.ConversationId, agentId));
        await threadStore.DeleteTopicAsync(agentId, chatId, topicId);

        // Removing a conversation removes what was in it. The sweep would reach these eventually;
        // deleting a topic is the person saying they want them gone now.
        attachmentService.DeleteConversation(identity.ConversationId);

        // Said to whoever may see the topic rather than to this caller: another tab holds a row
        // for a conversation that no longer exists, and paging only ever fetches backwards, so
        // nothing would ever refetch that row away. Deleting a topic that is already gone says the
        // same thing again, which is harmless and is what makes a repeated delete safe.
        await hubSender.SendToTopicAsync(stored, spaceSlug, "OnTopicChanged", notification);
    }

    public async Task SubscribePush(PushSubscriptionDto subscription)
    {
        var userId = RegisteredUserId;

        ValidateSubscription(subscription);

        await pushSubscriptionStore.SaveAsync(userId, subscription, CurrentSpaceSlug ?? "default");
    }

    public async Task ReplacePushSubscription(PushSubscriptionDto subscription, string oldEndpoint)
    {
        var userId = RegisteredUserId;

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
        var userId = RegisteredUserId;
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

    // False is "not yours to answer": the prompt stays pending for the one who may. An approval is
    // its asker's, and only that account's answer settles it, from any of their connections; one
    // acting for nobody is answered by its topic's own rule.
    public async Task<bool> RespondToApprovalAsync(string approvalId, ToolApprovalResult result)
    {
        var topic = await ApprovalTopicAsync(approvalId);
        var mayAnswer = approvalService.AskerOf(approvalId) is { } asker
            ? asker.UserId == Account.UserId && asker.TenantId == Account.TenantId
            : topic is null || MayAnswerIn(topic);
        if (!mayAnswer)
        {
            return false;
        }

        // Fire and forget - the approval service will resolve the TCS
        _ = approvalService.RespondToApprovalAsync(approvalId, result.ToString());
        return true;
    }

    // The questions another channel asked that are offered to this connection's person, for a chat
    // client that was not open when they were.
    public IReadOnlyList<ToolApprovalRequestMessage> GetOfferedApprovals() =>
        approvalService.OfferedTo(Account.UserId);

    // Reading is never deciding: whoever sees every topic sees another person's prompt and cannot
    // settle it. A prompt acting for nobody is answered in a topic by its owner, in a shared one also
    // by whoever's turn is running, and in a topic nobody owns by whoever sees it.
    private bool MayAnswerIn(TopicMetadata topic) =>
        topic.OwnerId is null
        || Viewer.Owns(topic)
        || topic.Shared && streamService.GetStreamState(topic.TopicId)?.CurrentSenderId == RegisteredUserId;

    // The stored topic a pending approval was asked in, refused like the topic itself to anyone who
    // may not see it. Null for an approval already answered, or one asked in a conversation no
    // topic holds yet.
    private async Task<TopicMetadata?> ApprovalTopicAsync(string approvalId)
    {
        if (approvalService.TopicOfApproval(approvalId) is not { } topicId
            || !sessionService.TryGetSession(topicId, out var session) || session is null)
        {
            return null;
        }

        var topic = await threadStore.GetTopicByChatIdAndThreadIdAsync(session.AgentId, session.ChatId, session.ThreadId);
        RequireSees(topic);
        return topic;
    }

    public async Task<bool> IsApprovalPending(string approvalId)
    {
        await ApprovalTopicAsync(approvalId);
        return approvalService.IsApprovalPending(approvalId);
    }

    public async Task<ToolApprovalRequestMessage?> GetPendingApprovalForTopic(string topicId)
    {
        await SessionOfAsync(topicId);
        return approvalService.GetPendingApprovalForTopic(topicId);
    }
}