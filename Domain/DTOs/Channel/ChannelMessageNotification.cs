using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record ChannelMessageNotification
{
    public required string ConversationId { get; init; }
    public required string Sender { get; init; }
    public required string Content { get; init; }
    public string? AgentId { get; init; }
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