using Domain.DTOs.Channel;
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record ChannelMessage
{
    public required string ConversationId { get; init; }
    public required string Content { get; init; }
    public required string Sender { get; init; }
    public required string ChannelId { get; init; }
    public string? AgentId { get; init; }
    public IReadOnlyList<ReplyTarget>? ReplyTo { get; init; }
    public MessageOrigin? Origin { get; init; }
    public string? Location { get; init; }
    public string? SatelliteId { get; init; }
    public string? DismissedAlert { get; init; }
    public AgentConfigPatch? ConfigPatch { get; init; }

    // The turn this message opens, when the channel had one to mint. A channel that must know the
    // value before the reply comes back — voice, which dispatches a transcript and then has to
    // recognise the answer to it — mints it here; everyone else leaves it null and the conversation
    // group mints one as it builds the turn.
    public string? TurnKey { get; init; }

    // Files sent with this message, as references rather than bytes. Where the bytes rest is the
    // channel's own business — an upload store, or the transport that already keeps them — and
    // they are fetched back only on the way to the model (ADR 0020).
    public IReadOnlyList<AttachmentReference>? Attachments { get; init; }
}