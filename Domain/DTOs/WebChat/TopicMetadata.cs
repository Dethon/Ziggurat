namespace Domain.DTOs.WebChat;

public record TopicMetadata(
    string TopicId,
    long ChatId,
    long ThreadId,
    string AgentId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastMessageAt,
    string SpaceSlug = "default",

    // What the row shows under the name, written on the same path that stamps the last-write
    // time. A row costs no history read to draw.
    string? LastMessageSnippet = null,

    // How many messages the topic holds, and how many its reader has seen. Unread is the
    // difference, so a badge costs no history read either — which is what a stored message id
    // could never give, because finding one in a Redis list means walking the list.
    long MessageCount = 0,
    long ReadPosition = 0);