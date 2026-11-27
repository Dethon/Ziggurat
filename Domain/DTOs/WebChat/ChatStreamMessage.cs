using Domain.DTOs.Channel;

namespace Domain.DTOs.WebChat;

public record ChatStreamMessage
{
    public string? Content { get; init; }
    public string? Reasoning { get; init; }

    // The reply's tool calls and their answers under this message id, each folded by its call id
    // (ToolCallStep.Upsert): a call, then its answer under the same id. A page from before, which read a
    // ToolCalls text here instead, draws no calls.
    public IReadOnlyList<ToolCallStep>? Tools { get; init; }
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

    // The error refuses the send whose wire carries it, as the hub queued it behind a reply the page had not
    // heard start: the message was never taken, so the page that drew it as sent takes it off again and
    // gives its words back. A page from before ignores it and draws the error as it always did.
    public bool RefusedSend { get; init; }
}