using Domain.DTOs;
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record RequestApprovalParams
{
    public required string ConversationId { get; init; }
    public required ApprovalMode Mode { get; init; }
    public required IReadOnlyList<ToolApprovalRequest> Requests { get; init; }

    // The question's own id wherever it is shown, so a settle_approval can name it here; a channel
    // asked without one mints its own, and nothing can then be settled on it.
    public string? ApprovalId { get; init; }
}

// The question offered to its asker on a channel other than the one the turn asked on. Always an
// account: a turn acting for nobody has nobody to find elsewhere and is never offered.
[PublicAPI]
public record OfferApprovalParams
{
    public required string ApprovalId { get; init; }

    // Where the turn asked, so the channel can name the conversation the question is about.
    public required string ConversationId { get; init; }

    public required Account Asker { get; init; }

    // The agent the turn ran on, which on Telegram says which bot speaks to the person.
    public string? AgentId { get; init; }

    public required IReadOnlyList<ToolApprovalRequest> Requests { get; init; }
}

[PublicAPI]
public record SettleApprovalParams
{
    public required string ApprovalId { get; init; }

    // The answer given elsewhere: a channel that asked the question still lets through what it was
    // asked about, exactly as if it had been answered there.
    public required ToolApprovalResult Result { get; init; }
}