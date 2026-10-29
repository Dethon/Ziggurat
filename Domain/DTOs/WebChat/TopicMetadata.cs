using System.Text.Json.Serialization;

namespace Domain.DTOs.WebChat;

public record TopicMetadata(
    string TopicId,
    long ChatId,
    long ThreadId,
    string AgentId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastMessageAt,
    string SpaceSlug = SpaceConfig.DefaultSlug,

    // What the row shows under the name, written on the same path that stamps the last-write
    // time. A row costs no history read to draw.
    string? LastMessageSnippet = null,

    // How many messages the topic holds, and how many its reader has seen. Unread is the
    // difference, so a badge costs no history read either — which is what a stored message id
    // could never give, because finding one in a Redis list means walking the list.
    long MessageCount = 0,
    long ReadPosition = 0,

    // Whose topic this is: the account it belongs to and that account's household. Absent on a
    // voice guest's topic and on one from before topics had owners, and a topic nobody owns is
    // everyone's in its space (TopicViewer).
    string? OwnerId = null,
    string? TenantId = null,

    // The owner's to set and clear, and no one else's: the household sees it in its space too.
    bool Shared = false,

    // The name the owner went by when the topic began, so a shared row can say who started it
    // without asking anyone. A label, never a key.
    string? OwnerName = null)
{
    // Only its owner and whoever sees all of the household's conversations may know it exists.
    [JsonIgnore]
    public bool IsPrivate => OwnerId is not null && !Shared;

    // When the conversation last moved. The one ordering every list of topics uses — the index's
    // sort score, the search index's score and the row's timestamp all read this.
    public DateTimeOffset LastWriteAt => LastMessageAt ?? CreatedAt;
}