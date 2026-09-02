using Domain.DTOs.WebChat;
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
            Timestamp = history.Timestamp,
            Attachments = history.Attachments
        };
}