using Domain.DTOs.WebChat;
using WebChat.Client.Helpers;
using WebChat.Client.Models;

namespace WebChat.Client.Extensions;

public static class ChatHistoryMessageExtensions
{
    // The one place a history row becomes a bubble. Every reload — first open, reconnect,
    // stream resume — goes through here, so a field the transcript carries cannot be kept by
    // one path and dropped by another.
    public static ChatMessageModel ToChatMessageModel(this ChatHistoryMessage history) =>
        new()
        {
            MessageId = history.MessageId,
            Role = history.Role,
            Content = history.Content,
            SenderId = history.SenderId,
            SenderName = history.SenderName,
            Timestamp = history.Timestamp,
            Attachments = history.Attachments,
            IsError = history.IsError,
            ResponseFormat = history.ResponseFormat,
            EntryId = history.EntryId,
            Versions = history.Versions,
            NoReply = history.NoReply,
            ToolsCalled = history.ToolsCalled,
            // A reply that ran tools and said nothing (cut by a steer) shows them as it did while it
            // streamed, so it is never an empty bubble.
            ToolCalls = string.IsNullOrEmpty(history.Content) && history.ToolsCalled is { Count: > 0 } tools
                ? string.Join(MessageFieldMerger.ToolCallsSeparator, tools)
                : null,
            Model = history.Model,
            IsInterrupted = history.IsInterrupted,
            ContinuesPrevious = history.ContinuesPrevious
        };
}