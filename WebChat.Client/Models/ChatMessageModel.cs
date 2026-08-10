using Domain.DTOs.Channel;

namespace WebChat.Client.Models;

public record ChatMessageModel
{
    public string Role { get; init; } = "user";
    public string Content { get; init; } = "";
    public string? Reasoning { get; init; }
    public string? ToolCalls { get; init; }
    public bool IsError { get; init; }

    public string? SenderId { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public string? MessageId { get; init; }

    // References, never bytes: the browser mints a short-lived download URL when the transcript
    // renders one, so a reload costs the same whether or not files were sent.
    public IReadOnlyList<AttachmentReference>? Attachments { get; init; }

    public bool HasContent =>
        !string.IsNullOrEmpty(Content) ||
        !string.IsNullOrEmpty(ToolCalls) ||
        !string.IsNullOrEmpty(Reasoning) ||
        Attachments is { Count: > 0 };
}