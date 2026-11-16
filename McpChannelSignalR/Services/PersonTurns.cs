using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Domain.Security;
using Mcp.Hosting;
using McpChannelSignalR.Internal;

namespace McpChannelSignalR.Services;

// Who sent a person's turn, as the hub knew them when it took the message: enough to send it and vouch
// for it with no hub call in hand, so a message held until the running turn ends leaves exactly as a
// live send would have. The session is the one the connection's assertion named, never the account,
// so a session signed out while the message waited sends nothing.
public sealed record SenderSnapshot(
    string TopicId,
    string? SessionId,
    Principal Principal,
    string DisplayName,
    ConversationIdentity Conversation,
    string AgentId,
    string SpaceSlug)
{
    public string UserId => Principal.UserId;

    // A chat-client turn acts for the account itself, never the name, which only labels it.
    public static SenderSnapshot Of(ForwardedAccount account, string displayName, string topicId, ChannelSession session) =>
        new(topicId, account.SessionId, account.PrincipalOn(ChannelIds.SignalR), displayName, session.Identity,
            session.AgentId, session.SpaceSlug ?? SpaceConfig.DefaultSlug);
}

// What a person's turn says, apart from who says it.
public sealed record TurnMessage(
    string Content,
    string? CorrelationId,
    AgentConfigPatch? ConfigPatch,
    IReadOnlyList<AttachmentReference>? Attachments,
    string? RerunOf = null,
    bool Continuation = false);

// A sender the accounts service vouched for just now, with the assertion their turn carries.
public sealed record VouchedSender(SenderSnapshot Sender, ResolvedAccount Account);

// The person a turn may carry, or what the browser is told instead.
public abstract record Vouching;

public sealed record Vouched(ResolvedAccount Account) : Vouching;

public sealed record NotVouched(string Refusal) : Vouching;

// A person's turn sent from a snapshot of its sender: vouched for, written on the topic's stream and
// emitted to the agent, each refusal ending the turn on the stream as a reply would. In steps, because
// the hub tells the topic's other pages a stream started between writing the message and emitting it.
public sealed class PersonTurns(
    StreamService streamService,
    ChannelNotificationEmitter notificationEmitter,
    ISessionAccounts sessions,
    ILogger logger)
{
    // Null once refused, the refusal having ended the turn on the stream.
    public async Task<VouchedSender?> VouchAsync(SenderSnapshot sender, CancellationToken ct)
    {
        switch (await VouchingAsync(sender.UserId, sender.SessionId, ct))
        {
            case Vouched vouched:
                return new VouchedSender(sender, vouched.Account);
            case NotVouched refused:
                await RefuseAsync(sender, refused.Refusal);
                break;
        }

        return null;
    }

    // The turn ended on the stream with what the browser is told instead of an answer.
    public Task RefuseAsync(SenderSnapshot sender, string refusal)
    {
        logger.LogWarning(
            "No assertion for conversation {ConversationId} (topic {TopicId}): {Refusal}; answering instead of sending a turn",
            sender.Conversation.ConversationId, sender.TopicId, refusal);
        return EndTurnAsync(sender, refusal);
    }

    // The accounts service's word for a person, asked afresh for every turn, which the turn carries to
    // the agent: the connection's own assertion was checked as it arrived and lives ten minutes, while
    // a chat client stays connected for hours. Asked for the session that assertion named, never for
    // the account by its id, so a session revoked since starts no turn (TopicRefusals.SignedOut); one the
    // service could not answer now — unreachable — is TopicRefusals.Unvouched. Either way no turn leaves.
    public async Task<Vouching> VouchingAsync(string userId, string? sessionId, CancellationToken ct)
    {
        if (sessionId is null)
        {
            return new NotVouched(TopicRefusals.SignedOut);
        }

        try
        {
            return await sessions.SessionAsync(userId, sessionId, ct) is { } vouched
                ? new Vouched(vouched)
                : new NotVouched(TopicRefusals.SignedOut);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "The accounts service could not vouch for account {UserId}; its turn is not sent", userId);
            return new NotVouched(TopicRefusals.Unvouched);
        }
    }

    // The person's message, for the topic's other pages; Continue's ask is nobody's message to draw.
    public Task WriteMessageAsync(SenderSnapshot sender, TurnMessage message) =>
        message.Continuation
            ? Task.CompletedTask
            : streamService.WriteMessageAsync(sender.TopicId, MessageOf(sender, message));

    public static ChatStreamMessage MessageOf(SenderSnapshot sender, TurnMessage message) => new()
    {
        Content = message.Content,
        UserMessage = new UserMessageInfo(sender.UserId, DateTimeOffset.UtcNow, sender.DisplayName, message.CorrelationId),
        Attachments = message.Attachments
    };

    // False when nobody was listening: no reply is coming, so the turn has been ended on the stream in
    // error rather than left holding the topic open — every page on it sees the same end, and it
    // releases the pending prompt the send added.
    public async Task<bool> EmitAsync(VouchedSender vouched, TurnMessage message, CancellationToken ct)
    {
        var delivered = await SendAsync(vouched, message, steer: false, ct, turnKey: null);
        if (!delivered)
        {
            await EndUnheardAsync(vouched.Sender);
        }

        return delivered;
    }

    // A steer, sent to come in at the running turn's next tool boundary. Nobody listening ends nothing
    // here: the turn on the stream is still the running one, which the caller decides about. It goes
    // under its queued id as its turn's key, which the agent answers every reply of that turn under, so
    // the stream can tell the steer's answer (a refusal as it arrived) from the running turn's.
    public Task<bool> SteerAsync(VouchedSender vouched, QueuedMessage steer, CancellationToken ct) =>
        SendAsync(vouched, steer.Message, steer: true, ct, turnKey: steer.Id);

    // A turn sent to nobody: no reply is coming, so it is ended on the stream in error rather than left
    // holding the topic open.
    public Task EndUnheardAsync(SenderSnapshot sender)
    {
        logger.LogWarning(
            "Nothing is polling for conversation {ConversationId} (topic {TopicId}); " +
            "answering not live instead of streaming a reply that cannot arrive",
            sender.Conversation.ConversationId, sender.TopicId);
        return EndTurnAsync(sender, TopicRefusals.NotLive);
    }

    private Task<bool> SendAsync(
        VouchedSender vouched, TurnMessage message, bool steer, CancellationToken ct, string? turnKey)
    {
        var sender = vouched.Sender;
        return notificationEmitter.EmitAsync(
            new ChannelMessageNotification
            {
                ConversationId = sender.Conversation.ConversationId,
                Sender = sender.DisplayName,
                Principal = sender.Principal,
                Assertion = vouched.Account.Assertion,
                Content = message.Content,
                AgentId = sender.AgentId,
                ConfigPatch = message.ConfigPatch,
                RerunOf = message.RerunOf,
                Continuation = message.Continuation,
                Steer = steer,
                TurnKey = turnKey,
                Attachments = message.Attachments,
                Timestamp = DateTimeOffset.UtcNow
            },
            ct);
    }

    private Task EndTurnAsync(SenderSnapshot sender, string error) =>
        streamService.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = sender.Conversation.ConversationId,
            Content = error,
            ContentType = ReplyContentType.Error,
            IsComplete = true
        });
}