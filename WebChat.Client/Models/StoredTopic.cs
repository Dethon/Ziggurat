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
    public string SpaceSlug { get; set; } = SpaceConfig.DefaultSlug;

    // When the conversation last moved: what a row's timestamp shows and what the list is
    // ordered by, the same reading the server's index sorts on.
    public DateTime LastWriteAt => LastMessageAt ?? CreatedAt;

    // Both supplied by the server. Unread is the difference, so a badge is drawn without the
    // client holding a single one of the topic's messages.
    public long MessageCount { get; set; }
    public long ReadPosition { get; set; }

    public int UnreadCount => (int)Math.Max(0, MessageCount - ReadPosition);

    // What the row shows under the name. Supplied by the server, so a row is drawn without the
    // client holding that conversation's history.
    public string? LastMessageSnippet { get; set; }

    // Whose the topic is and whether the household sees it, as the server holds them: the share
    // toggle shows on the owner's own topics, and a shared row of someone else's says who began it.
    public string? OwnerId { get; set; }
    public string? TenantId { get; set; }
    public bool Shared { get; set; }
    public string? OwnerName { get; set; }

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
            ReadPosition = metadata.ReadPosition,
            OwnerId = metadata.OwnerId,
            TenantId = metadata.TenantId,
            Shared = metadata.Shared,
            OwnerName = metadata.OwnerName
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
            ReadPosition,
            OwnerId,
            TenantId,
            Shared,
            OwnerName);
    }
}