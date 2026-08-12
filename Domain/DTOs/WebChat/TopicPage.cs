namespace Domain.DTOs.WebChat;

// One fetch of the sidebar: the rows, and where the next fetch starts. A null cursor means the
// range ended, so the client stops asking rather than discovering it by getting nothing back.
public record TopicPage(
    IReadOnlyList<TopicMetadata> Topics,
    string? NextCursor,

    // Which of these topics have a reply in flight. Reported with the page because the client
    // used to ask about every topic one at a time, which is the second unbounded fan-out on
    // start-up after history loading.
    IReadOnlyList<string>? LiveTopicIds = null);