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
using Microsoft.Extensions.AI;

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

    // Built here from what the hub is already given rather than injected, so the hub's dependencies
    // stay what every hub test constructs it with; it holds no state, so one per hub costs nothing.
    private readonly PersonTurns _turns = new(streamService, notificationEmitter, sessions, logger);

    private readonly QueueNews _queueNews = new(streamService, threadStore, hubSender);

    // What the browser is told when its household's spaces could not be asked for its person: signed
    // out since, or the accounts service unreachable just now.
    private const string UnvouchedSpacesError = "This household's spaces could not be read right now.";

    private const string UnvouchedAgentsError = "This household's agents could not be read right now.";

    private const string UnvouchedPeopleError = "This household's people could not be read right now.";

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

    // What a turn's sender shows the agent: the account's name, or its id when no name was learned.
    private string TurnSender => Context.Items.TryGetValue(DisplayNameItem, out var name) && name is string { Length: > 0 } shown
        ? shown
        : SignedIn.UserId;

    // Who a turn sent from here is, as of now: what a turn needs to be sent and vouched for once this
    // call is over.
    private SenderSnapshot SenderOf(string topicId, ChannelSession session) =>
        SenderSnapshot.Of(SignedIn, TurnSender, topicId, session);

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
        if ((await _turns.VouchingAsync(SignedIn.UserId, SignedIn.SessionId, ct)).Account is not { } vouched)
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

    // A page from before StreamSeen: handed the people's messages the stream carried, which it skips
    // where it shows them.
    public IAsyncEnumerable<ChatStreamMessage> ResumeStream(
        string topicId,
        CancellationToken cancellationToken) =>
        ResumeStreamFrom(topicId, null, cancellationToken);

    // A page resuming a stream read its state a moment ago, and says how much of it it drew: whatever
    // the stream carried since — the rest of a message it had begun, a person's message, the end — is
    // handed to it first, taken with the subscription, so nothing written between the two is lost.
    public async IAsyncEnumerable<ChatStreamMessage> ResumeStreamFrom(
        string topicId,
        StreamSeen? seen,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await SessionToReadAsync(topicId);
        var state = streamService.GetStreamState(topicId);
        if (state is null || !state.IsProcessing)
        {
            yield break;
        }

        if (streamService.Join(topicId, seen) is not var (liveStream, carried))
        {
            yield break;
        }

        // A page that said what it drew is handed the rest; one from before is handed the people's
        // messages alone, since it cannot be told the rest of a message from the whole of it.
        foreach (var earlier in carried.Where(m => seen is not null ? m.ApprovalRequest is null : m.UserMessage is not null))
        {
            yield return earlier;
        }

        var pendingApproval = approvalService.GetPendingApprovalForTopic(topicId);
        if (pendingApproval is not null)
        {
            yield return new ChatStreamMessage { ApprovalRequest = pendingApproval };
        }

        await foreach (var msg in WhileTheyMaySee(topicId, liveStream.ReadAllAsync(cancellationToken))
                           .IgnoreCancellation(cancellationToken))
        {
            yield return msg;
        }
    }

    // A reply streams to a page only while its person may see the topic: its owner making it private
    // mid-reply took the row off the other members' lists, and the hub kept sending them the reply as
    // it was written. Asked again only when who may see it changed (StreamService.AudienceOf).
    private async IAsyncEnumerable<ChatStreamMessage> WhileTheyMaySee(
        string topicId, IAsyncEnumerable<ChatStreamMessage> chunks)
    {
        var seen = streamService.AudienceOf(topicId);
        await foreach (var chunk in chunks)
        {
            if (streamService.AudienceOf(topicId) is var now && now != seen)
            {
                seen = now;
                if (!sessionService.TryGetSession(topicId, out var session) || session is null
                    || await threadStore.TopicOfAsync(session) is not { } topic || !Viewer.Sees(topic))
                {
                    yield break;
                }
            }

            yield return chunk;
        }
    }

    public IAsyncEnumerable<ChatStreamMessage> SendMessage(
        string topicId,
        string message,
        string? correlationId,
        AgentConfigPatch? configPatch,
        IReadOnlyList<AttachmentReference>? attachments,
        CancellationToken cancellationToken) =>
        SendTurn(topicId, message, correlationId, configPatch, attachments, rerunOf: null, cancellationToken);

    // A reroll's turn: the person's message the conversation already ends on (rerunOf, its entry id),
    // sent again as itself so the agent answers it again rather than hearing it twice. A method of its
    // own, because an installed page from before binds SendMessage's arguments as they were.
    public IAsyncEnumerable<ChatStreamMessage> SendRerun(
        string topicId,
        string message,
        string? correlationId,
        AgentConfigPatch? configPatch,
        IReadOnlyList<AttachmentReference>? attachments,
        string rerunOf,
        CancellationToken cancellationToken) =>
        SendTurn(topicId, message, correlationId, configPatch, attachments, rerunOf, cancellationToken);

    // Continue: the model carries on the reply that was cut off — stopped, or out of room — with an ask of
    // its own words the conversation keeps hidden (Continuation). Allowed wherever a message may be sent.
    public async IAsyncEnumerable<ChatStreamMessage> SendContinue(
        string topicId,
        string? correlationId,
        AgentConfigPatch? configPatch,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (await SessionToContinueAsync(topicId) is { } session)
        {
            var messages = await threadStore.GetMessagesAsync(new AgentKey(session.Identity.ConversationId, session.AgentId).ToString()) ?? [];
            // Back to the person's turn, past any Continue's ask (a continuation stopped before it said anything).
            var lastReply = messages.Reverse().TakeWhile(m => !Transcript.StartsPersonsTurn(m))
                .FirstOrDefault(m => m.Role == ChatRole.Assistant && !string.IsNullOrEmpty(m.Text));
            if (lastReply?.IsInterrupted() != true)
            {
                throw new HubException(TopicRefusals.NothingToContinue);
            }
        }

        await foreach (var chunk in SendTurn(
                           topicId, Continuation.Instruction, correlationId, configPatch, null, rerunOf: null, cancellationToken,
                           continuation: true))
        {
            yield return chunk;
        }
    }

    private async IAsyncEnumerable<ChatStreamMessage> SendTurn(
        string topicId,
        string message,
        string? correlationId,
        AgentConfigPatch? configPatch,
        IReadOnlyList<AttachmentReference>? attachments,
        string? rerunOf,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        bool continuation = false)
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

        // The stream's prompt is what a page joining it draws as the person's message: Continue's ask is none.
        var (broadcastChannel, linkedToken, opened, stream) =
            streamService.OpenOrHold(topicId, continuation ? "" : message, userId, cancellationToken);

        // Subscribe before emitting so no early reply chunks are lost
        var (subscription, carried) = streamService.Join(topicId, broadcastChannel);

        // A message finding a reply already running waits behind it, as one the page knew to queue
        // does: a page that had not heard the stream start yet must not jump the queue. Its wire
        // follows the stream, which carries the message once its turn starts.
        if (!opened && rerunOf is null && !continuation)
        {
            var (_, refusal) = await QueueAsync(
                session, SenderOf(topicId, session), new TurnMessage(message, correlationId, configPatch, attachments),
                stream, cancellationToken);
            if (refusal is not null)
            {
                yield return new ChatStreamMessage { Error = refusal };
            }

            await foreach (var chunk in FollowAsync(topicId, carried, subscription, linkedToken, cancellationToken))
            {
                yield return chunk;
            }

            yield break;
        }

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
        var sender = SenderOf(topicId, session);
        var turn = new TurnMessage(message, correlationId, configPatch, attachments, rerunOf, continuation);
        if (await _turns.VouchAsync(sender, cancellationToken) is not { } vouched)
        {
            await ForgetIfNeverUsedAsync(session.AgentId, session.Identity.ChatId, topicId);
            await foreach (var unvouchedChunk in
                subscription.ReadAllAsync(linkedToken).IgnoreCancellation(cancellationToken))
            {
                yield return unvouchedChunk;
            }

            yield break;
        }

        await _turns.WriteMessageAsync(sender, turn);

        // The other browsers on the topic - a member reading a shared one, the same person's phone -
        // follow a stream they are told started, replaying the message just buffered; the one sending
        // is streaming already and ignores it. Told as an agent-initiated turn's start is, to those
        // who may see the topic. A message enqueued behind a running reply needs no telling: whoever
        // follows the stream receives it on it.
        await hubSender.SendToTopicAsync(
            await threadStore.TopicOfAsync(session), sender.SpaceSlug, "OnStreamStarted",
            new StreamStartedNotification(topicId, sender.SpaceSlug));

        await streamService.InAgentOrderAsync(() => _turns.EmitAsync(vouched, turn, cancellationToken));

        await foreach (var chunk in FollowAsync(topicId, carried, subscription, linkedToken, cancellationToken))
        {
            yield return chunk;
        }
    }

    // What the stream carried before this wire subscribed, then the stream itself — the loop ends when
    // the channel completes (when nothing is pending or queued), not on individual IsComplete messages.
    private async IAsyncEnumerable<ChatStreamMessage> FollowAsync(
        string topicId,
        IReadOnlyList<ChatStreamMessage> carried,
        System.Threading.Channels.ChannelReader<ChatStreamMessage> subscription,
        CancellationToken linkedToken,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // An approval it carried is the other person's, shown to them where it was asked.
        foreach (var earlier in carried.Where(m => m.ApprovalRequest is null))
        {
            yield return earlier;
        }

        // The stream's token is its opener's; a wire that joined it ends with its own caller too.
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(linkedToken, cancellationToken);
        await foreach (var msg in WhileTheyMaySee(topicId, subscription.ReadAllAsync(reading.Token))
                           .IgnoreCancellation(cancellationToken))
        {
            yield return msg;
        }
    }

    // A message sent while a reply runs: held in the topic's queue and sent once the running turn
    // ends (QueueDispatcher). False when nothing is running to queue behind, which is the page's
    // signal to open a stream itself; a refusal is the hub's answer, since the stream it would end
    // belongs to the turn that is running.
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

        if (streamService.Hold(topicId) is not { } stream)
        {
            return false;
        }

        var turn = new TurnMessage(message, correlationId, configPatch, attachments);
        var (queued, refusal) = await QueueAsync(session, SenderOf(topicId, session), turn, stream, Context.ConnectionAborted);
        return refusal is null ? queued : throw new HubException(refusal);
    }

    public async Task<IReadOnlyList<QueuedMessageItem>> GetQueue(string topicId)
    {
        await SessionToReadAsync(topicId);
        return [.. streamService.QueueOf(topicId).Messages.Select(m => m.ToItem())];
    }

    // A queued message its sender withdraws before its turn starts, and the files uploaded for it that
    // nothing else names: neither the conversation (a message re-run with its files) nor the queue. A
    // page names its files by id and nothing checked, as it was queued, that they were this
    // conversation's, so only this conversation's are deleted. The message is gone once withdrawn, so
    // a conversation that cannot be read just now fails nothing: its files are left to the sweep.
    public async Task RemoveQueued(string topicId, string queuedId)
    {
        var withdrawal = await WithdrawAsync(topicId, queuedId);
        var removed = withdrawal.Message!;
        var conversationId = removed.Sender.Conversation.ConversationId;
        ConversationHistory conversation;
        try
        {
            conversation = await threadStore.GetConversationAsync(
                new AgentKey(conversationId, removed.Sender.AgentId).ToString());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not read conversation {ConversationId} to delete a removed message's files", conversationId);
            return;
        }

        attachmentService.Delete((removed.Message.Attachments ?? [])
            .Select(a => a.Id)
            .Where(id => attachmentService.ConversationOf(id)?.ConversationId == conversationId)
            .Except(conversation.HeldAttachments())
            .Except(withdrawal.StillQueuedAttachments()));
    }

    // A queued message its sender takes back into their composer, to rework and send again; its files
    // are kept, since the composer holds them now.
    public async Task<ReturnedMessage> TakeBackQueued(string topicId, string queuedId) =>
        (await WithdrawAsync(topicId, queuedId)).Message!.ToReturned();

    // The caller's messages a stop handed back on this topic, in the order they were queued, for their
    // composer: taken once, so they land on whichever of the person's pages asks first.
    // Asked whether the caller still sees the topic before anything is taken, so a refusal leaves
    // their words held rather than lost.
    public async Task<IReadOnlyList<ReturnedMessage>> TakeReturned(string topicId)
    {
        await SessionToReadAsync(topicId);
        return [.. streamService.TakeReturned(topicId, SignedIn.Account).Select(m => m.ToReturned())];
    }

    private async Task<Withdrawal> WithdrawAsync(string topicId, string queuedId)
    {
        await SessionToReadAsync(topicId);
        var withdrawal = streamService.Withdraw(topicId, queuedId, SignedIn.Account);
        if (withdrawal.Message is not { } withdrawn)
        {
            throw new HubException(withdrawal.Refusal);
        }

        await _queueNews.TellOrLogAsync(withdrawn.Sender, logger);
        return withdrawal;
    }

    // Admitted as any send is — the capability guard, then the accounts service's word for the
    // session — before it waits, so an obvious refusal shows at once; it is vouched for again as it
    // leaves. Either way the place the caller held on the running stream is given up: by queueing,
    // or, refused, by releasing it, since ending a turn on the stream would end the one running. Not
    // queued, and not refused, when that stream was stopped meanwhile: there is nothing to queue behind.
    private async Task<(bool Queued, string? Refusal)> QueueAsync(
        ChannelSession session, SenderSnapshot sender, TurnMessage turn, long stream, CancellationToken ct)
    {
        string? refusal;
        try
        {
            refusal = await CapabilityRefusalAsync(session.AgentId, turn.ConfigPatch, turn.Attachments, ct);
            if (refusal is null && await _turns.VouchingAsync(sender.UserId, sender.SessionId, ct) is { Account: null } unvouched)
            {
                refusal = unvouched.Refusal;
            }
        }
        catch
        {
            streamService.Release(sender.TopicId, stream);
            throw;
        }

        if (refusal is not null)
        {
            streamService.Release(sender.TopicId, stream);
            return (false, refusal);
        }

        if (!streamService.Enqueue(sender.TopicId, stream, QueuedMessage.Of(sender, turn)))
        {
            return (false, null);
        }

        await _queueNews.TellAsync(sender);
        return (true, null);
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

    public async Task CancelTopic(string topicId)
    {
        var session = await SessionToContinueAsync(topicId);

        // Nothing queued for a reply somebody abandoned runs after it: each message goes back to the
        // person who queued it. Starting the next one instead would race the agent's teardown, which
        // drops whatever arrives behind a cancel.
        var returned = await streamService.StopAsync(topicId, async () =>
        {
            if (session is not null)
            {
                await notificationEmitter.EmitCancelAsync(new ChannelCancelNotification
                {
                    ConversationId = session.Identity.ConversationId,
                    AgentId = session.AgentId,
                    Timestamp = DateTimeOffset.UtcNow
                });
            }
        });
        if (returned.Count > 0)
        {
            await _queueNews.TellOrLogAsync(returned[0].Sender, logger);
            await _queueNews.TellReturnedOrLogAsync(returned, logger);
        }

        // The turn the prompt belonged to is gone, so the prompt goes with it. Left pending it
        // holds the tool call open and keeps the modal on screen over a stream that has ended.
        await approvalService.CancelPendingApprovalsForTopicAsync(topicId);
    }

    // One page, most recently written first, plus where the next one starts. The page size is
    // the server's to decide unless a caller has a reason of its own, so a client that wants
    // the ordinary sidebar asks for nothing and gets what the retention policy says.
    public Task<TopicPage> GetTopicPage(
        string agentId,
        string spaceSlug = SpaceConfig.DefaultSlug,
        string? cursor = null,
        int? pageSize = null,
        bool archived = false) =>
        GetTopicPageFiltered(agentId, spaceSlug, cursor, pageSize, archived, TopicFilter.Everyone);

    // The same page narrowed to the list the person chose (TopicViewer.Lists): a filter only ever
    // narrows what the connection's viewer sees, so it needs no check of its own. A method of its own
    // rather than a parameter on the one above, because a hub binds every argument and an installed app
    // from before the filter still calls that one with five.
    public async Task<TopicPage> GetTopicPageFiltered(
        string agentId, string spaceSlug, string? cursor, int? pageSize, bool archived, TopicFilter? filter)
    {
        return WithLiveStreams(await threadStore.GetTopicPageAsync(
            agentId, spaceSlug, Viewer, cursor, pageSize ?? retention.PageSize, archived, ListedWith(spaceSlug), filter));
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
            var renamed = stored with { Name = topic.Name };
            await threadStore.SaveTopicAsync(renamed, keepTtl: true);

            // Told to whoever may have it on screen, as a share change is: a household member reading
            // a shared topic, the owner's other device, whoever sees all. Before, their list and header
            // kept the old name until a reply next ended in the topic.
            var heardIn = HeardIn(renamed.SpaceSlug);
            await hubSender.SendToTopicAsync(renamed, heardIn, "OnTopicChanged",
                new TopicChangedNotification(TopicChangeType.Updated, renamed.TopicId, renamed, heardIn));
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
        streamService.AudienceChanged(topicId);
        var heardIn = HeardIn(changed.SpaceSlug);
        await hubSender.SendToGroupAsync(
            TopicAudience.Space(SignedIn.TenantId, heardIn),
            "OnTopicChanged",
            new TopicChangedNotification(TopicChangeType.Updated, topicId, changed, heardIn));
    }

    // On the server, so it covers conversations this client never loaded — which is every
    // conversation past the first page, and the whole archive.
    public Task<TopicPage> SearchTopics(
        string agentId,
        string query,
        string spaceSlug = SpaceConfig.DefaultSlug,
        string? cursor = null,
        int? pageSize = null) =>
        SearchTopicsFiltered(agentId, query, spaceSlug, cursor, pageSize, TopicFilter.Everyone);

    public async Task<TopicPage> SearchTopicsFiltered(
        string agentId, string query, string spaceSlug, string? cursor, int? pageSize, TopicFilter? filter)
    {
        return WithLiveStreams(await threadStore.SearchTopicsAsync(
            agentId, spaceSlug, Viewer, query, cursor, pageSize ?? retention.PageSize, ListedWith(spaceSlug), filter));
    }

    // Whose topics someone who sees all may narrow their list to: the household's other people, by the
    // name they go by now. Anyone else sees nobody's private topics, so has nobody to pick and is named
    // nobody rather than refused.
    public async Task<IReadOnlyList<HouseholdMemberName>> GetHouseholdPeople()
    {
        if (!Viewer.SeesAll)
        {
            return [];
        }

        var people = await HouseholdViewAsync(
            households.MemberNamesAsync, "people", UnvouchedPeopleError, Context.ConnectionAborted);
        return [.. people.People.Where(p => p.UserId != SignedIn.UserId)];
    }

    // Read state is the store's to work out: the count it would be compared against is one round
    // trip newer than anything the browser holds.
    // How far a topic was read is each person's own (ReadBy), so whoever sees a topic marks it read
    // for themselves, someone who sees every topic reading another person's private one included:
    // its owner's position is left as they left it. Gated on continuing, as it once was, the reader's
    // was never kept and their badge on every other person's topic never cleared.
    public async Task MarkTopicRead(string agentId, long chatId, string topicId)
    {
        var stored = await threadStore.GetTopicAsync(SignedIn.TenantId, agentId, chatId, topicId);
        RequireSees(stored);
        await threadStore.MarkTopicReadAsync(SignedIn.TenantId, agentId, chatId, topicId, SignedIn.UserId);

        // Told to whoever may have it on screen, as a rename is: read on the phone, the desktop kept
        // its unread badge until a reload. Each page reads its own person's position off it (ReadFor).
        if (stored is not null
            && await threadStore.GetTopicAsync(SignedIn.TenantId, agentId, chatId, topicId) is { } read)
        {
            var heardIn = HeardIn(read.SpaceSlug);
            await hubSender.SendToTopicAsync(read, heardIn, "OnTopicChanged",
                new TopicChangedNotification(TopicChangeType.Updated, read.TopicId, read, heardIn));
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
        streamService.Forget(topicId);
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

    // The message and everything after it on the line leave the conversation; on a reply, its whole turn.
    public async Task DeleteFrom(string agentId, string topicId, long chatId, string entryId, string? lastSeen)
    {
        var (topic, key) = await RewritableAsync(agentId, topicId, chatId);
        await RewriteAsync(topic, key, new CutFrom(entryId, lastSeen));
    }

    // An edit's re-run, before the page sends the edited words: the message and what followed it are
    // kept as a version of the slot before it, and the line ends there.
    public async Task PutAsideFrom(string agentId, string topicId, long chatId, string entryId, string? lastSeen)
    {
        var (topic, key) = await RewritableAsync(agentId, topicId, chatId);
        await RewriteAsync(topic, key, new PutAsideFrom(entryId, lastSeen));
    }

    // A message or a reply corrected in place, no turn run; the old line is kept as a version.
    public async Task EditInPlace(string agentId, string topicId, long chatId, string entryId, string text, string? lastSeen)
    {
        var (topic, key) = await RewritableAsync(agentId, topicId, chatId);
        await RewriteAsync(topic, key, new EditInPlace(entryId, text, lastSeen));
    }

    // A reroll, before the page sends the person's message again (SendRerun): the reply after it is kept
    // as a version, and the line ends on the message.
    public async Task PutAsideReply(string agentId, string topicId, long chatId, string userEntryId, string? lastSeen)
    {
        var (topic, key) = await RewritableAsync(agentId, topicId, chatId);
        await RewriteAsync(topic, key, new PutAsideReply(userEntryId, lastSeen));
    }

    // The slot shows another of its versions, by its place in the order a person flips through them.
    public async Task SwitchVersion(string agentId, string topicId, long chatId, string slot, int index, string? lastSeen)
    {
        var (topic, key) = await RewritableAsync(agentId, topicId, chatId);
        await RewriteAsync(topic, key, new SwitchVersion(slot, index, lastSeen));
    }

    // A new topic of the forker's, private, holding the line up to the message forked from (HistoryFork),
    // its files copied into its own conversation. Seeing the topic is enough: a sees-all reader, and a
    // topic another channel goes on showing, are forked like any other.
    public async Task<ForkedTopic> Fork(string agentId, string topicId, long chatId, string entryId)
    {
        var source = await threadStore.GetTopicAsync(SignedIn.TenantId, agentId, chatId, topicId)
                     ?? throw new HubException(NoSuchConversation);
        RequireSees(source);
        var from = new AgentKey(new ConversationIdentity(source.ChatId, source.ThreadId).ConversationId, agentId);
        var fork = HistoryFork.Of(await threadStore.GetMessagesAsync(from.ToString()) ?? [], entryId)
                   ?? throw new HubException(TopicRefusals.Changed);

        var forkId = ConversationIdGenerator.NewTopicId();
        var identity = ConversationIdGenerator.CreateFor(forkId);
        var to = new AgentKey(identity.ConversationId, agentId);
        var topic = new TopicMetadata(
            forkId, identity.ChatId, identity.ThreadId, agentId, $"{source.Name} (fork)", DateTimeOffset.UtcNow, null,
            source.SpaceSlug, OwnerId: SignedIn.UserId, TenantId: SignedIn.TenantId, Shared: false, OwnerName: TurnSender);
        await threadStore.SaveTopicAsync(topic);

        var copied = new List<ChatMessage>();
        foreach (var message in fork.Messages)
        {
            copied.Add(await WithOwnFilesAsync(message, to, source.SpaceSlug));
        }

        if (copied.Count > 0)
        {
            await threadStore.AppendMessagesAsync(to.ToString(), copied);
        }

        await threadStore.MarkTopicReadAsync(SignedIn.TenantId, agentId, identity.ChatId, forkId, SignedIn.UserId);
        return new ForkedTopic(
            await threadStore.GetTopicAsync(SignedIn.TenantId, agentId, identity.ChatId, forkId) ?? topic, fork.ComposerText);
    }

    // A message's files copied into the fork's conversation, when this store holds them all; one another
    // channel keeps (a Telegram photo) is fetched from that channel by its reference, as before. Where
    // the files landed in the sandbox is kept as it is: those paths are the household's sandbox's, not
    // the conversation's, and the copy reads them as the original did.
    private async Task<ChatMessage> WithOwnFilesAsync(ChatMessage message, AgentKey to, string spaceSlug)
    {
        if (message.GetAttachments() is not { Count: > 0 } attachments)
        {
            return message;
        }

        var copies = new List<AttachmentReference>();
        foreach (var attachment in attachments)
        {
            if (await attachmentService.CopyAsync(attachment.Id, to, spaceSlug, Context.ConnectionAborted) is not { } copy)
            {
                return message;
            }

            copies.Add(copy);
        }

        message.SetAttachments(copies);
        message.SetAttachmentChannelId(ChannelIds.SignalR);
        message.SetAttachmentHouseholdId(null);
        return message;
    }

    // A topic this person may write in, over its own conversation, that no other channel goes on
    // showing and no reply is being written to.
    private async Task<(TopicMetadata Topic, string Key)> RewritableAsync(string agentId, string topicId, long chatId)
    {
        var topic = await threadStore.GetTopicAsync(SignedIn.TenantId, agentId, chatId, topicId)
                    ?? throw new HubException(NoSuchConversation);
        RequireMayContinue(topic);
        if (streamService.IsStreaming(topicId))
        {
            throw new HubException(TopicRefusals.ReplyRunning);
        }

        var key = new AgentKey(new ConversationIdentity(topic.ChatId, topic.ThreadId).ConversationId, agentId).ToString();
        if (TopicMirrors.IsMirrored(topic, await threadStore.GetMessagesAsync(key) ?? []))
        {
            throw new HubException(TopicRefusals.Mirrored);
        }

        return (topic, key);
    }

    private async Task RewriteAsync(TopicMetadata topic, string key, HistoryRewrite rewrite)
    {
        switch (await threadStore.RewriteAsync(key, rewrite))
        {
            case RewriteRefused { Reason: RewriteRefusal.TurnInFlight }:
                throw new HubException(TopicRefusals.ReplyRunning);
            case RewriteRefused { Reason: RewriteRefusal.NotRerunnable }:
                throw new HubException(TopicRefusals.NotRerunnable);
            case RewriteRefused:
                throw new HubException(TopicRefusals.Changed);
            case Rewritten rewritten:
                attachmentService.Delete(rewritten.AttachmentsNoLongerHeld());
                var spaceSlug = HeardIn(topic.SpaceSlug);
                await hubSender.SendToTopicAsync(
                    topic, spaceSlug, "OnHistoryChanged", new HistoryChangedNotification(topic.TopicId, spaceSlug));
                break;
        }
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