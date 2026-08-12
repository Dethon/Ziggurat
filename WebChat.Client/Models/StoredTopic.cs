using Domain.DTOs.WebChat;

namespace WebChat.Client.Models;

public class StoredTopic
{
    public string TopicId { get; set; } = "";
    public long ChatId { get; set; }
    public long ThreadId { get; set; }
    public string AgentId { get; set; } = "";
    public string Name { get; set; } = "New Chat";
    public DateTime CreatedAt { get; set; }
    public DateTime? LastMessageAt { get; set; }
    public string SpaceSlug { get; set; } = "default";

    // Both supplied by the server. Unread is the difference, so a badge is drawn without the
    // client holding a single one of the topic's messages.
    public long MessageCount { get; set; }
    public long ReadPosition { get; set; }

    public int UnreadCount => (int)Math.Max(0, MessageCount - ReadPosition);

    // What the row shows under the name. Supplied by the server, so a row is drawn without the
    // client holding that conversation's history.
    public string? LastMessageSnippet { get; set; }

    // True while the name is the stand-in a conversation started by a file was given: the file's
    // own name, because at that moment nothing had been typed. The first message with text takes
    // the name over. Not part of the metadata, so it never survives a reload — by then the
    // conversation has its real name and messages of its own.
    public bool NameFromFile { get; set; }

    // A copy carrying a new read position, so the row clears its badge without waiting on the
    // round trip that makes it true on the server.
    public StoredTopic WithReadPosition(long readPosition) =>
        FromMetadata(ToMetadata() with { ReadPosition = readPosition });

    public static StoredTopic FromMetadata(TopicMetadata metadata)
    {
        return new StoredTopic
        {
            TopicId = metadata.TopicId,
            ChatId = metadata.ChatId,
            ThreadId = metadata.ThreadId,
            AgentId = metadata.AgentId,
            Name = metadata.Name,
            CreatedAt = metadata.CreatedAt.UtcDateTime,
            LastMessageAt = metadata.LastMessageAt?.UtcDateTime,
            SpaceSlug = metadata.SpaceSlug,
            LastMessageSnippet = metadata.LastMessageSnippet,
            MessageCount = metadata.MessageCount,
            ReadPosition = metadata.ReadPosition
        };
    }

    public TopicMetadata ToMetadata()
    {
        return new TopicMetadata(
            TopicId,
            ChatId,
            ThreadId,
            AgentId,
            Name,
            new DateTimeOffset(CreatedAt, TimeSpan.Zero),
            LastMessageAt.HasValue ? new DateTimeOffset(LastMessageAt.Value, TimeSpan.Zero) : null,
            SpaceSlug,
            LastMessageSnippet,
            MessageCount,
            ReadPosition);
    }
}