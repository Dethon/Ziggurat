using Domain.Memory;

namespace Domain.DTOs;

public record MemoryExtractionRequest(
    string UserId,
    string? ThreadStateKey,
    MemoryAnchor Anchor,
    string? ConversationId,
    string? AgentId)
{
    public string? FallbackContent { get; init; }

    // The household of the account the turn acted for: what extracting from it costs is theirs.
    public string? TenantId { get; init; }
}