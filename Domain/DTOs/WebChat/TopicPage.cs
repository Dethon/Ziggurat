namespace Domain.DTOs.WebChat;

// One fetch of the sidebar: the rows, and where the next fetch starts. A null cursor means the
// range ended, so the client stops asking rather than discovering it by getting nothing back.
public record TopicPage(
    IReadOnlyList<TopicMetadata> Topics,
    string? NextCursor);