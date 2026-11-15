using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;

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

    // The message's name in its stored conversation (ChatHistoryMessage.EntryId): what a rewrite or a
    // fork names it by. None until the turn that wrote it was stored.
    public string? EntryId { get; init; }

    // The message begins a place the conversation branched (ChatHistoryMessage.Versions).
    public MessageVersions? Versions { get; init; }

    // Not a message: a slot with nothing shown, drawn as a line carrying its versions.
    public bool NoReply { get; init; }

    // The tools the turn this reply ends called (ChatHistoryMessage.ToolsCalled).
    public IReadOnlyList<string>? ToolsCalled { get; init; }

    // The model a reply was written by (ChatHistoryMessage.Model).
    public string? Model { get; init; }

    // A reply cut off before it ended (ChatHistoryMessage.IsInterrupted).
    public bool IsInterrupted { get; init; }

    // A person's message as the page that sent it named the send (UserMessageInfo.CorrelationId): the
    // stream's copy of a message is the one shown when the ids match. None for one loaded from history.
    public string? CorrelationId { get; init; }

    // References, never bytes: the browser mints a short-lived download URL when the transcript
    // renders one, so a reload costs the same whether or not files were sent.
    public IReadOnlyList<AttachmentReference>? Attachments { get; init; }

    public bool HasContent =>
        !string.IsNullOrEmpty(Content) ||
        !string.IsNullOrEmpty(ToolCalls) ||
        !string.IsNullOrEmpty(Reasoning) ||
        Attachments is { Count: > 0 };
}