using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record ChannelMessageNotification
{
    public required string ConversationId { get; init; }
    public required string Sender { get; init; }
    public required string Content { get; init; }
    public string? AgentId { get; init; }
    // The person an unprompted fire is on behalf of, when its author named one: a schedule's or a
    // watch's `userId`. Null for a message a person sent themselves — the sender is that person.
    public string? UserId { get; init; }
    // Who the turn acts for, as the channel that vouched for them (Principal): stamped by a channel
    // that knows the person's account on their own message (the chat client, from the proxy's session
    // check), and by a server that fires on somebody's behalf from the turn that set it going. Null on
    // a person's own message from a channel that knows no account yet, whose channel and sender are
    // the voucher. The agent believes neither: on a person's own message it keeps only the user id
    // (the account is the one the assertion below names), and on a fire it reads nothing of it (the
    // person is the one the delegation below names, or nobody).
    public Principal? Principal { get; init; }
    // The accounts service's signed word for who a person's own message is from, as the channel got
    // it when it learned the person — the session check's header, the resolution API's answer, the
    // household guest's — and carried here untouched. The agent believes this and not the principal:
    // it verifies it as the turn arrives and refuses a person's turn without one. A fire nobody sent
    // carries none.
    public string? Assertion { get; init; }
    // A fire's word for who it acts for: the delegation the agent signed when a person's turn wrote
    // the schedule, the watch or the login handoff raising it, with what that object does now. The
    // agent checks it as the fire arrives; a fire carrying none, or one that fails, acts for nobody.
    public FireDelegation? Delegation { get; init; }
    public IReadOnlyList<ReplyTarget>? ReplyTo { get; init; }
    public MessageOrigin? Origin { get; init; }
    // Optional originating room for room-aware prompts. Part of the shared channel protocol but
    // currently only populated by the voice channel; other channels leave it null.
    public string? Location { get; init; }
    // Optional originating voice satellite id. Like Location, part of the shared protocol but
    // currently only populated by the voice channel; other channels leave it null.
    public string? SatelliteId { get; init; }
    // Optional description of an alert (alarm/timer) the user dismissed just before speaking, e.g.
    // 'alarm "Take out the trash"'. Voice-only, like Location/SatelliteId; enables LLM-mediated snooze.
    public string? DismissedAlert { get; init; }
    // Optional per-message config override (model, reasoning effort). Part of the shared protocol
    // but currently only populated by the SignalR channel; other channels leave it null.
    public AgentConfigPatch? ConfigPatch { get; init; }
    // Optional structured-output request for this turn's final answer, beside the config patch and
    // like it honoured for this message only. Part of the shared protocol; populated by a channel
    // that parses the reply (Sugary), left null by every other.
    public ResponseFormat? ResponseFormat { get; init; }
    // Optional request that the conversation also be shown on the deployment's review host
    // (WebChat), with this channel kept as the conversation's first target. Asked for by a channel
    // whose conversations are worth reading afterwards (Sugary, and Telegram on every turn); left
    // null by every other.
    public ReviewCopy? ReviewCopy { get; init; }
    // Optional key naming the turn this message opens, for a channel that has to recognise the
    // answer to it later. Voice-only today; every other channel leaves it null and the conversation
    // group mints one.
    public string? TurnKey { get; init; }

    // Files sent with this message, as references rather than bytes. Part of the shared protocol
    // and deliberately transport-neutral, which SignalR and Telegram both populate — one from an
    // upload store of its own, the other from Telegram's own hold on the file (ADR 0022).
    public IReadOnlyList<AttachmentReference>? Attachments { get; init; }

    public DateTimeOffset Timestamp { get; init; }
}