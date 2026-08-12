namespace Domain.DTOs.WebChat;

public record TopicMetadata(
    string TopicId,
    long ChatId,
    long ThreadId,
    string AgentId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastMessageAt,
    string? LastReadMessageId = null,
    string SpaceSlug = "default",

    // What the row shows under the name, written on the same path that stamps the last-write
    // time. A row costs no history read to draw.
    string? LastMessageSnippet = null);