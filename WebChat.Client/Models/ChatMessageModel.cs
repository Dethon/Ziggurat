using Domain.DTOs.Channel;

namespace WebChat.Client.Models;

public record ChatMessageModel
{
    public string Role { get; init; } = "user";
    public string Content { get; init; } = "";
    public string? Reasoning { get; init; }
    public string? ToolCalls { get; init; }
    public bool IsError { get; init; }

    // The structured answer its turn asked for, by name (ChatHistoryMessage.ResponseFormat).
    public string? ResponseFormat { get; init; }

    public string? SenderId { get; init; }

    // The name the message was sent under, for a person the page cannot otherwise name.
    public string? SenderName { get; init; }
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