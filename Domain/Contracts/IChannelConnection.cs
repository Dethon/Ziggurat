using Domain.DTOs;
using Domain.DTOs.Channel;
using JetBrains.Annotations;

namespace Domain.Contracts;

[PublicAPI]
public interface IChannelConnection : IToolApprovalHandler
{
    string ChannelId { get; }

    // Attach-only channels (declared per endpoint in config) cannot own a conversation:
    // their create_conversation hands back the id it is given instead of persisting a
    // topic, so delivery fan-out must never let them anchor the shared conversation id.
    bool AttachOnly { get; }

    // The review host (declared per endpoint in config, WebChat in the shipped one): where a
    // conversation goes when the channel that sent its message asks for a review copy. It attaches
    // to that conversation as a later target of the fan-out, never as the first (ADR 0046).
    bool HostsReviewCopies { get; }

    IAsyncEnumerable<ChannelMessage> Messages { get; }

    // The record rather than its fields spread out: it is already the wire shape every channel
    // server deserializes on the far side, so a new field is one edit here instead of a new
    // position in five argument lists.
    Task SendReplyAsync(SendReplyParams reply, CancellationToken ct);

    // An attachment's bytes, by naming its reference. Where they rest is never mounted, so this is
    // the only way in (ADR 0021). Null is "not connected", and also "the file could not be had":
    // both hydrate to a placeholder naming the file rather than failing the turn.
    Task<byte[]?> FetchAttachmentAsync(string attachmentId, CancellationToken ct);

    Task<string?> CreateConversationAsync(
        string agentId,
        string topicName,
        string sender,
        string? initialPrompt,
        string? address,
        string? existingConversationId,
        CancellationToken ct);
}