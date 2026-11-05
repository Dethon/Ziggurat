using System.Runtime.CompilerServices;
using Domain.Agents;
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.WebChat;
using Domain.Extensions;
using Domain.Security;
using Mcp.Hosting;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.Internal;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using Microsoft.AspNetCore.Mvc;
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
    ISessionAccounts sessions,
    IHouseholdConfiguration households,
    ILogger<ChatHub> logger) : Hub
{
    // The connection's household's agents, as the household runs them on the deployment's models.
    private readonly HouseholdAgentCatalogue _agents = new(households, catalog);

    // What the browser is told when the emit reached nobody. The message may still be sitting in a
    // buffer for an agent that comes back, so this says the turn cannot be answered now rather
    // than claiming it was thrown away.
    private const string NotLiveError = "No agent is connected right now, so this message has not been answered.";

    // What the browser is told when the accounts service could not vouch for the person just now, so
    // no turn could carry its word; nothing was sent, and sending again later may work.
    private const string UnvouchedError = "Who you are could not be checked right now, so this message has not been answered.";

    // What it is told once the session it signed in with is no longer live — revoked from the account
    // page, or a connection that came with no session to ask about: nothing is sent until it signs in
    // again, and sending again will not help.
    internal const string SignedOutError = "This device was signed out, so this message has not been answered. Sign in again to keep chatting.";

    // What the browser is told when its household's spaces could not be asked for its person: signed
    // out since, or the accounts service unreachable just now.
    private const string UnvouchedSpacesError = "This household's spaces could not be read right now.";

    private const string UnvouchedAgentsError = "This household's agents could not be read right now.";

    // Where the connection keeps the person the proxy's session check named as it arrived, which is
    // also what says it may call anything (SignedInConnectionFilter).
    internal const string AccountItem = "Account";

    // Where it keeps the name that account goes by, asked of the accounts service as it arrived;
    // absent when the service could not say.
    internal const string DisplayNameItem = "DisplayName";

    // Where it keeps the space it joined last, the household's spaces as it joined it, and the removed
    // spaces' slugs that space lists beside its own.
    private const string SpaceSlugItem = "SpaceSlug";
    private const string HouseholdSpacesItem = "HouseholdSpaces";
    private const string ListedWithItem = "ListedWith";

    private const string NoSuchConversation = TopicRefusals.NoSuchConversation;

    // Seeing a topic is not writing in it: someone who sees every topic of the household reads
    // another person's private one and changes nothing in it.
    private const string ReadOnlyConversation = "Only the person whose conversation this is can change it until they share it.";

    // Continuing a shared topic is not deciding it: its name and whether it exists stay its owner's.
    private const string OwnersConversation = "Only the person whose conversation this is can rename or delete it.";

    // Another household's space is answered as one that does not exist, so naming it tells nothing.
    private const string NoSuchSpace = "No such space.";

    private string? CurrentSpaceSlug =>
        Context.Items.TryGetValue(SpaceSlugItem, out var slug) ? slug as string : null;

    // The household's spaces as the connection last joined one of them, and the slugs of removed
    // spaces whose topics the joined space lists beside its own (SpaceListing): both asked once as
    // it joins, so a page costs no read of the configuration and no scan.
    private IReadOnlyList<HouseholdSpace>? JoinedHouseholdSpaces =>
        Context.Items.TryGetValue(HouseholdSpacesItem, out var spaces) ? spaces as IReadOnlyList<HouseholdSpace> : null;

    private IReadOnlyList<string> ListedWith(string spaceSlug) =>
        spaceSlug == CurrentSpaceSlug && Context.Items.TryGetValue(ListedWithItem, out var slugs) && slugs is IReadOnlyList<string> listed
            ? listed
            : [];

    // Where a topic's news is heard: in its own space while the household has it, else in the
    // default space that lists it now — nobody can join a removed one.
    private string HeardIn(string topicSlug) =>
        JoinedHouseholdSpaces is { } spaces ? SpaceListing.ShownIn(topicSlug, spaces) : topicSlug;

    // The person this connection is, set as it arrives; SignedInConnectionFilter admits no call on one
    // without it.
    private ForwardedAccount SignedIn => (ForwardedAccount)Context.Items[AccountItem]!;

    private TopicViewer Viewer => SignedIn.Viewer;

    // Who a turn sent from here acts for: the account itself, never the name, which only labels it.
    private Principal TurnPrincipal => SignedIn.PrincipalOn(ChannelIds.SignalR);

    // What a turn's sender shows the agent: the account's name, or its id when no name was learned.
    private string TurnSender => Context.Items.TryGetValue(DisplayNameItem, out var name) && name is string { Length: > 0 } shown
        ? shown
        : SignedIn.UserId;

    // The connection's household's agents, as its configuration names them, on the deployment's models
    // as the agent registered them (AgentCatalogBuilder.ForHousehold): never another household's.
    public Task<IReadOnlyList<AgentCatalogEntry>> GetAgents() =>
        HouseholdViewAsync(_agents.AgentsAsync, "agents", UnvouchedAgentsError, Context.ConnectionAborted);

    public async Task<bool> ValidateAgent(string agentId) =>
        (await HouseholdAgentsAsync(Context.ConnectionAborted)).Agents.Any(agent => agent.Id == agentId);

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

    private void RequireMayContinue(TopicMetadata? topic)
    {
        RequireSees(topic);
        if (topic is not null && !Viewer.MayContinue(topic))
        {
            throw new HubException(ReadOnlyConversation);
        }
    }

    private void RequireMayRenameOrDelete(TopicMetadata? topic)
    {
        RequireMayContinue(topic);
        if (topic is not null && !Viewer.MayRenameOrDelete(topic))
        {
            throw new HubException(OwnersConversation);
        }
    }

    // A session over a conversation no topic holds yet is a topic its opener has not saved, whose id
    // only they know; any other is held to its topic's rule (TopicOfAsync).
    private Task<ChannelSession?> SessionToReadAsync(string topicId) => SessionHeldToAsync(topicId, RequireSees);

    private Task<ChannelSession?> SessionToContinueAsync(string topicId) => SessionHeldToAsync(topicId, RequireMayContinue);

    private async Task<ChannelSession?> SessionHeldToAsync(string topicId, Action<TopicMetadata?> require)
    {
        if (!sessionService.TryGetSession(topicId, out var session) || session is null)
        {
            return null;
        }

        require(await threadStore.TopicOfAsync(session));
        return session;
    }

    // The household's spaces in the order its configuration puts them, which is the order a person
    // is shown them in.
    public async Task<IReadOnlyList<HouseholdSpace>> GetSpaces() =>
        SpaceListing.Ordered(await HouseholdSpacesAsync(Context.ConnectionAborted));

    public async Task JoinSpace(string spaceSlug)
    {
        if (!SpaceConfig.IsValidSlug(spaceSlug))
        {
            throw new HubException("Invalid space slug");
        }

        var spaces = await HouseholdSpacesAsync(Context.ConnectionAborted);
        if (spaces.All(s => s.Slug != spaceSlug))
        {
            throw new HubException(NoSuchSpace);
        }

        // A removed space's topics are not moved; the default space lists them, so they are found by
        // what the household's topics are kept under, only when that is the space joined.
        Context.Items[HouseholdSpacesItem] = spaces;
        Context.Items[ListedWithItem] = spaceSlug == SpaceListing.DefaultOf(spaces)
            ? SpaceListing.ListedWith(spaceSlug, spaces, await threadStore.SpacesHoldingTopicsAsync(SignedIn.TenantId))
            : Array.Empty<string>();
        await SwitchSpaceGroupAsync(spaceSlug);
    }

    // Asked with the accounts service's fresh word for the connection's person rather than the
    // assertion it arrived with, which lives ten minutes while a chat client stays connected for
    // hours: the household is the one that word names, never one the browser says.
    private async Task<IReadOnlyList<HouseholdSpace>> HouseholdSpacesAsync(CancellationToken ct) =>
        (await HouseholdViewAsync(households.SpacesAsync, "spaces", UnvouchedSpacesError, ct)).Spaces;

    private Task<HouseholdAgentNames> HouseholdAgentsAsync(CancellationToken ct) =>
        HouseholdViewAsync(households.AgentNamesAsync, "agents", UnvouchedAgentsError, ct);

    private async Task<T> HouseholdViewAsync<T>(
        Func<string, HouseholdProof, CancellationToken, Task<T>> view, string what, string refusal, CancellationToken ct)
    {
        if ((await VouchedAsync(ct)).Account is not { } vouched)
        {
            throw new HubException(refusal);
        }

        try
        {
            return await view(SignedIn.TenantId, HouseholdProof.OfAssertion(vouched.Assertion), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or HouseholdConfigRefusedException)
        {
            logger.LogWarning(ex, "Could not read the {What} of household {TenantId}", what, SignedIn.TenantId);
            throw new HubException(refusal);
        }
    }

    // A connection hears its space's news, and a private topic's only as its owner or as someone who
    // sees every topic (TopicAudience).
    private async Task SwitchSpaceGroupAsync(string spaceSlug)
    {
        if (Context.Items.TryGetValue(SpaceSlugItem, out var previous) && previous is string prevSlug)
        {
            if (prevSlug == spaceSlug)
            {
                return;
            }

            await Task.WhenAll(TopicAudience.GroupsOf(SignedIn, prevSlug)
                .Select(group => Groups.RemoveFromGroupAsync(Context.ConnectionId, group)));
        }

        Context.Items[SpaceSlugItem] = spaceSlug;
        await Task.WhenAll(TopicAudience.GroupsOf(SignedIn, spaceSlug)
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
        return attachmentService.MintDictation(CurrentSpaceSlug ?? AttachmentService.SpaceDefault, SignedIn.TenantId);
    }

    // The ticket is scoped to the topic being composed in, so a caller can only put bytes against
    // a conversation the connection already has a session for.
    public async Task<UploadTicket> CreateUploadTicket(string topicId)
    {
        if (await SessionToContinueAsync(topicId) is not { } session)
        {
            throw new HubException("Session not found. Please start a session first.");
        }

        return attachmentService.MintUpload(
            topicId, session.AgentId, session.Identity, CurrentSpaceSlug ?? AttachmentService.SpaceDefault);
    }

    // Minted when the transcript renders an attachment, not published: one upload store serves
    // every space, so a long-lived URL would be readable by anyone holding it. The file's
    // conversation is held to the same rule as its topic, the topic the agent the file names holds
    // it under — the registered catalogue is the home's agents, and a household's own may go by an
    // id it lacks. A file kept before files named their agent was the home's: any of its agents.
    public async Task<AttachmentDownload?> CreateAttachmentDownload(string attachmentId)
    {
        if (attachmentService.ConversationOf(attachmentId) is { } held
            && ConversationIdentity.Parse(held.ConversationId) is { } conversation)
        {
            IEnumerable<string> agents = held.AgentId is { } agentId
                ? [agentId]
                : (catalog.GetAll() ?? []).Select(agent => agent.Id);
            var holders = await Task.WhenAll(agents.Select(agent =>
                threadStore.GetTopicByChatIdAndThreadIdAsync(agent, conversation.ChatId, conversation.ThreadId)));
            holders.ToList().ForEach(RequireSees);
        }

        return attachmentService.MintDownload(attachmentId, CurrentSpaceSlug ?? AttachmentService.SpaceDefault, SignedIn.UserId);
    }

    public async Task<bool> IsProcessing(string topicId)
    {
        await SessionToReadAsync(topicId);
        return streamService.IsStreaming(topicId);
    }

    public async Task<StreamState?> GetStreamState(string topicId)
    {
        await SessionToReadAsync(topicId);
        return streamService.GetStreamState(topicId);
    }

    public async IAsyncEnumerable<ChatStreamMessage> ResumeStream(
        string topicId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await SessionToReadAsync(topicId);
        var state = streamService.GetStreamState(topicId);
        if (state is null || !state.IsProcessing)
        {
            yield break;
        }

        // Joined with what it carried, as a send joining a stream is: the page drew the stream's state
        // a moment ago, and a person's message written since was on neither until this wire carried it.
        // The page skips the ones it shows already.
        if (streamService.Join(topicId) is not var (liveStream, carried))
        {
            yield break;
        }

        foreach (var earlier in carried.Where(m => m.UserMessage is not null))
        {
            yield return earlier;
        }

        var pendingApproval = approvalService.GetPendingApprovalForTopic(topicId);
        if (pendingApproval is not null)
        {
            yield return new ChatStreamMessage { ApprovalRequest = pendingApproval };
        }

        await foreach (var msg in liveStream.ReadAllAsync(cancellationToken).IgnoreCancellation(cancellationToken))
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
        if (await SessionToContinueAsync(topicId) is not { } session)
        {
            yield return new ChatStreamMessage
            {
                Error = "Session not found. Please start a session first.",
                IsComplete = true
            };
            yield break;
        }

        var userId = SignedIn.UserId;
        var conversationId = session.Identity.ConversationId;

        var (broadcastChannel, linkedToken) =
            streamService.GetOrCreateStream(topicId, message, userId, cancellationToken);
        streamService.TryIncrementPending(topicId);

        // Subscribe before emitting so no early reply chunks are lost
        var (subscription, carried) = streamService.Join(topicId, broadcastChannel);

        // Refused before anything is written or emitted, for the race where the model changes
        // between picking a file and sending it: no turn is created, no agent is woken, and no
        // browser is shown a message that was never taken. The answer goes out on the same
        // stream-error path an undeliverable message uses, so every browser sees the same end.
        if (await CapabilityRefusalAsync(session.AgentId, configPatch, attachments, cancellationToken) is { } refused)
        {
            await AnswerRefusedAsync(topicId, conversationId, refused);
            await foreach (var refusalChunk in
                subscription.ReadAllAsync(linkedToken).IgnoreCancellation(cancellationToken))
            {
                yield return refusalChunk;
            }

            yield break;
        }

        // Refused the same way, and as early: a turn the accounts service has not vouched for would
        // only be refused by the agent, after the browser had shown the message as sent.
        var vouching = await VouchedAsync(cancellationToken);
        if (vouching.Account is not { } vouched)
        {
            await AnswerUnvouchedAsync(topicId, conversationId, vouching.Refusal);
            await ForgetIfNeverUsedAsync(session.AgentId, session.Identity.ChatId, topicId);
            await foreach (var unvouchedChunk in
                subscription.ReadAllAsync(linkedToken).IgnoreCancellation(cancellationToken))
            {
                yield return unvouchedChunk;
            }

            yield break;
        }

        // Write user message to buffer for other browsers
        var timestamp = DateTimeOffset.UtcNow;
        var userMessage = new ChatStreamMessage
        {
            Content = message,
            UserMessage = new UserMessageInfo(userId, timestamp, TurnSender, correlationId),
            Attachments = attachments
        };
        await streamService.WriteMessageAsync(topicId, userMessage);

        // The other browsers on the topic - a member reading a shared one, the same person's phone -
        // follow a stream they are told started, replaying the message just buffered; the one sending
        // is streaming already and ignores it. Told as an agent-initiated turn's start is, to those
        // who may see the topic. A message enqueued behind a running reply needs no telling: whoever
        // follows the stream receives it on it.
        var spaceSlug = session.SpaceSlug ?? SpaceConfig.DefaultSlug;
        await hubSender.SendToTopicAsync(
            await threadStore.TopicOfAsync(session), spaceSlug, "OnStreamStarted",
            new StreamStartedNotification(topicId, spaceSlug));

        var delivered = await notificationEmitter.EmitAsync(
            new ChannelMessageNotification
            {
                ConversationId = conversationId,
                Sender = TurnSender,
                Principal = TurnPrincipal,
                Assertion = vouched.Assertion,
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

        // An approval it carried is the other person's, shown to them where it was asked.
        foreach (var earlier in carried.Where(m => m.ApprovalRequest is null))
        {
            yield return earlier;
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
        if (await SessionToContinueAsync(topicId) is not { } session)
        {
            return false;
        }

        if (!streamService.TryIncrementPending(topicId))
        {
            return false;
        }

        var userId = SignedIn.UserId;
        var conversationId = session.Identity.ConversationId;

        if (await CapabilityRefusalAsync(session.AgentId, configPatch, attachments, Context.ConnectionAborted) is { } refused)
        {
            await AnswerRefusedAsync(topicId, conversationId, refused);
            return true;
        }

        var vouching = await VouchedAsync(Context.ConnectionAborted);
        if (vouching.Account is not { } vouched)
        {
            await AnswerUnvouchedAsync(topicId, conversationId, vouching.Refusal);
            return true;
        }

        var timestamp = DateTimeOffset.UtcNow;
        var userMessage = new ChatStreamMessage
        {
            Content = message,
            UserMessage = new UserMessageInfo(userId, timestamp, TurnSender, correlationId),
            Attachments = attachments
        };
        await streamService.WriteMessageAsync(topicId, userMessage);

        var delivered = await notificationEmitter.EmitAsync(new ChannelMessageNotification
        {
            ConversationId = conversationId,
            Sender = TurnSender,
            Principal = TurnPrincipal,
            Assertion = vouched.Assertion,
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
    // changed between picking a file and sending it. The agent does not check a third time. The agent
    // is the connection's household's, on the model it runs there (AgentCatalogBuilder.ForHousehold):
    // the catalogue the agent registered is the home's agents, and a household's own agent is not one
    // of them to be sent anything.
    private async Task<string?> CapabilityRefusalAsync(
        string? agentId,
        AgentConfigPatch? configPatch,
        IReadOnlyList<AttachmentReference>? attachments,
        CancellationToken ct)
    {
        if (attachments is not { Count: > 0 })
        {
            return null;
        }

        var agent = agentId is null
            ? null
            : await HouseholdViewAsync(
                (household, proof, token) => _agents.AgentAsync(household, proof, agentId, token),
                "agents", UnvouchedAgentsError, ct);
        return AttachmentCapability.Refusal(agent, configPatch?.Model, attachments.Select(a => a.MediaType));
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

    // The accounts service's word for this connection's person, asked afresh for every turn, which
    // the turn carries to the agent: the connection's own assertion was checked as it arrived and
    // lives ten minutes, while a chat client stays connected for hours. Asked for the session that
    // assertion named, never for the account by its id, so a session revoked since starts no turn
    // (SignedOutError); one the service could not answer now — unreachable — is UnvouchedError. Either
    // way no turn leaves.
    private async Task<Vouching> VouchedAsync(CancellationToken ct)
    {
        if (SignedIn.SessionId is not { } sessionId)
        {
            return new Vouching(null, SignedOutError);
        }

        try
        {
            return await sessions.SessionAsync(SignedIn.UserId, sessionId, ct) is { } vouched
                ? new Vouching(vouched, "")
                : new Vouching(null, SignedOutError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The accounts service could not vouch for account {UserId}; its turn is not sent",
                SignedIn.UserId);
            return new Vouching(null, UnvouchedError);
        }
    }

    // The person a turn may carry, or what the browser is told instead.
    private sealed record Vouching(ResolvedAccount? Account, string Refusal);

    // A conversation is kept for what was said in it. A first message refused because its person was
    // signed out or disabled since leaves one that never held a message and that nobody can carry on in
    // as them, so it is forgotten rather than left in the list empty. Its browser keeps showing the
    // refusal it was just told; nobody else is told, since nothing was ever said there.
    private async Task ForgetIfNeverUsedAsync(string agentId, long chatId, string topicId)
    {
        if (await threadStore.GetTopicAsync(SignedIn.TenantId, agentId, chatId, topicId) is { MessageCount: 0 })
        {
            await threadStore.DeleteTopicAsync(SignedIn.TenantId, agentId, chatId, topicId);
        }
    }

    private async Task AnswerUnvouchedAsync(string topicId, string conversationId, string refusal)
    {
        logger.LogWarning(
            "No assertion for conversation {ConversationId} (topic {TopicId}): {Refusal}; answering instead of sending a turn",
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
        if (await SessionToContinueAsync(topicId) is { } session)
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
            agentId, spaceSlug, Viewer, cursor, pageSize ?? retention.PageSize, archived, ListedWith(spaceSlug)));
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
    // Renaming one is its owner's, shared or not.
    public async Task SaveTopic(TopicMetadata topic, bool isNew = false)
    {
        var stored = await threadStore.GetTopicAsync(SignedIn.TenantId, topic.AgentId, topic.ChatId, topic.TopicId);
        if (stored is not null)
        {
            RequireMayRenameOrDelete(stored);
            await threadStore.SaveTopicAsync(stored with { Name = topic.Name }, keepTtl: true);
            return;
        }

        // The topics this household holds are not the only ones: a conversation another household's
        // topic holds is theirs, and saving a topic of this one over it would take it from them.
        if (!IsItsOwnConversation(topic.TopicId, topic.ChatId, topic.ThreadId)
            || await threadStore.GetTopicByChatIdAndThreadIdAsync(topic.AgentId, topic.ChatId, topic.ThreadId) is not null)
        {
            throw new HubException(NoSuchConversation);
        }

        await threadStore.SaveTopicAsync(topic with
        {
            OwnerId = SignedIn.UserId,
            TenantId = SignedIn.TenantId,
            Shared = false,
            OwnerName = TurnSender
        });
    }

    // Whether the household sees a topic is its owner's to say and nobody else's, a member reading
    // it shared or someone who sees every topic included. The whole space is told: whoever could not
    // see it now can, and on unsharing their list lets it go (TopicViewer, in the client too).
    public async Task SetTopicShared(string agentId, long chatId, string topicId, bool shared)
    {
        var stored = await threadStore.GetTopicAsync(SignedIn.TenantId, agentId, chatId, topicId)
                     ?? throw new HubException(NoSuchConversation);
        RequireSees(stored);
        if (!Viewer.Owns(stored))
        {
            throw new HubException("Only the person whose conversation this is can share it or stop sharing it.");
        }

        var changed = stored with { Shared = shared };
        await threadStore.SaveTopicAsync(changed, keepTtl: true);
        var heardIn = HeardIn(changed.SpaceSlug);
        await hubSender.SendToGroupAsync(
            TopicAudience.Space(SignedIn.TenantId, heardIn),
            "OnTopicChanged",
            new TopicChangedNotification(TopicChangeType.Updated, topicId, changed, heardIn));
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
            agentId, spaceSlug, Viewer, query, cursor, pageSize ?? retention.PageSize, ListedWith(spaceSlug)));
    }

    // Read state is the store's to work out: the count it would be compared against is one round
    // trip newer than anything the browser holds.
    // How far a topic was read is its own people's: someone who sees every topic reading another
    // person's private one leaves it as unread as its owner left it.
    public async Task MarkTopicRead(string agentId, long chatId, string topicId)
    {
        var stored = await threadStore.GetTopicAsync(SignedIn.TenantId, agentId, chatId, topicId);
        RequireSees(stored);
        if (stored is null || Viewer.MayContinue(stored))
        {
            await threadStore.MarkTopicReadAsync(SignedIn.TenantId, agentId, chatId, topicId);
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

    // Whether a topic exists is its owner's to decide, shared or not.
    public async Task DeleteTopic(string agentId, string topicId, long chatId, long threadId)
    {
        // Another household's topic over the conversation is held to the same rule, which it fails:
        // its history and its stream are not this caller's to name into a delete.
        var stored = await threadStore.GetTopicAsync(SignedIn.TenantId, agentId, chatId, topicId)
                     ?? await threadStore.GetTopicByChatIdAndThreadIdAsync(agentId, chatId, threadId);
        RequireMayRenameOrDelete(stored);

        // The topic's own conversation and no other: a thread it does not hold is not the caller's
        // to name into the delete.
        if (stored is not null && (stored.ThreadId != threadId || stored.TopicId != topicId))
        {
            throw new HubException(NoSuchConversation);
        }

        var spaceSlug = stored is not null ? HeardIn(stored.SpaceSlug) : CurrentSpaceSlug ?? SpaceConfig.DefaultSlug;
        var notification = new TopicChangedNotification(TopicChangeType.Deleted, topicId, SpaceSlug: spaceSlug);

        // A topic already gone is said to be gone again, and nothing else: with no topic to say
        // whose the conversation is, only one its own id names is this chat's to delete.
        if (stored is null && !IsItsOwnConversation(topicId, chatId, threadId))
        {
            await hubSender.SendToGroupAsync(TopicAudience.Space(SignedIn.TenantId, spaceSlug), "OnTopicChanged", notification);
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
        await threadStore.DeleteTopicAsync(SignedIn.TenantId, agentId, chatId, topicId);

        // Removing a conversation removes what was in it. The sweep would reach these eventually;
        // deleting a topic is the person saying they want them gone now.
        attachmentService.DeleteConversation(identity.ConversationId);

        // Said to whoever may see the topic rather than to this caller: another tab holds a row
        // for a conversation that no longer exists, and paging only ever fetches backwards, so
        // nothing would ever refetch that row away. Deleting a topic that is already gone says the
        // same thing again, which is harmless and is what makes a repeated delete safe — to this
        // caller's household, the only one that could hold a row for it.
        await (stored is null
            ? hubSender.SendToGroupAsync(TopicAudience.Space(SignedIn.TenantId, spaceSlug), "OnTopicChanged", notification)
            : hubSender.SendToTopicAsync(stored, spaceSlug, "OnTopicChanged", notification));
    }

    public async Task SubscribePush(PushSubscriptionDto subscription)
    {
        ValidateSubscription(subscription);

        await pushSubscriptionStore.SaveAsync(SignedIn.Account, subscription with { SessionId = SignedIn.SessionId },
            CurrentSpaceSlug ?? "default");
    }

    public async Task ReplacePushSubscription(PushSubscriptionDto subscription, string oldEndpoint)
    {
        ValidateSubscription(subscription);

        if (string.IsNullOrWhiteSpace(oldEndpoint))
        {
            throw new HubException("Old endpoint is required for replacement.");
        }

        await pushSubscriptionStore.SaveAsync(SignedIn.Account, subscription with { SessionId = SignedIn.SessionId },
            CurrentSpaceSlug ?? "default", replacingEndpoint: oldEndpoint);
    }

    public async Task UnsubscribePush(string endpoint)
    {
        await pushSubscriptionStore.RemoveAsync(SignedIn.UserId, endpoint);
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
            ? asker == SignedIn.Account
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
        approvalService.OfferedTo(SignedIn.UserId);

    // False is "not yours to dismiss", or it already ended: an alert is its person's, and only their
    // dismissal, from any of their clients, stops it — everywhere it rings. The service is the
    // method's rather than the hub's, as the alerts are no part of a topic.
    public Task<bool> DismissAlert(string alertId, [FromServices] AlertService alerts) =>
        alerts.DismissAsync(alertId, SignedIn.Account);

    // What still rings for this connection's person, for a chat client that was not open when it
    // began — the one the push brought up, above all.
    public IReadOnlyList<AlertOfferedNotification> GetOfferedAlerts([FromServices] AlertService alerts) =>
        alerts.OfferedTo(SignedIn.Account);

    // Reading is never deciding: whoever sees every topic sees another person's prompt and cannot
    // settle it. A prompt acting for nobody is answered in a topic by its owner, in a shared one also
    // by whoever's turn is running, and in a topic nobody owns by whoever sees it.
    private bool MayAnswerIn(TopicMetadata topic) =>
        topic.OwnerId is null
            ? Viewer.Sees(topic)
            : Viewer.Owns(topic)
              || topic.Shared && streamService.GetStreamState(topic.TopicId)?.CurrentSenderId == SignedIn.UserId;

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

        var topic = await threadStore.TopicOfAsync(session);
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
        await SessionToReadAsync(topicId);
        return approvalService.GetPendingApprovalForTopic(topicId);
    }
}