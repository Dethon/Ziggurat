using JetBrains.Annotations;

namespace Domain.DTOs.WebChat;

// The upload store's wire surface, named once. The browser builds these URLs and the channel
// server maps them, so a route or header spelled separately in each is a route that can drift
// apart silently.
[PublicAPI]
public static class AttachmentEndpointPaths
{
    public const string Attachments = "/api/attachments";

    public const string TicketHeader = "X-Attachment-Ticket";

    public const string TopicQueryParameter = "topicId";

    public const string TicketQueryParameter = "ticket";

    // A conversation id is "<chatId>:<threadId>"; the colon is the only character in it that a
    // path cannot take. Both the upload store and the sandbox landing lay conversations out by
    // this, so they have to agree on the spelling.
    public static string ConversationDirectory(string conversationId) =>
        conversationId.Replace(':', '-');
}