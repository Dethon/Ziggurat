using Domain.DTOs.Channel;

namespace Domain.DTOs.WebChat;

public record ChatStreamMessage
{
    public string? Content { get; init; }
    public string? Reasoning { get; init; }
    public string? ToolCalls { get; init; }
    public bool IsComplete { get; init; }
    public string? Error { get; init; }
    public string? MessageId { get; init; }
    public ToolApprovalRequestMessage? ApprovalRequest { get; init; }
    public long SequenceNumber { get; init; }
    public UserMessageInfo? UserMessage { get; init; }
    public DateTimeOffset? Timestamp { get; init; }

    // Files sent with a user message. References only: the browser mints a download URL when the
    // transcript renders one.
    public IReadOnlyList<AttachmentReference>? Attachments { get; init; }
}