using WebChat.Client.Models;

namespace WebChat.Client.State.Messages;

public sealed record MessagesState
{
    public IReadOnlyDictionary<string, IReadOnlyList<ChatMessageModel>> MessagesByTopic { get; init; }
        = new Dictionary<string, IReadOnlyList<ChatMessageModel>>();

    public IReadOnlySet<string> LoadedTopics { get; init; }
        = new HashSet<string>();

    // Used to prevent duplicate messages when both HandleUserMessage and
    // StreamingService try to add the same message due to race conditions.
    public IReadOnlyDictionary<string, IReadOnlySet<string>> FinalizedMessageIdsByTopic { get; init; }
        = new Dictionary<string, IReadOnlySet<string>>();

    // A message id the topic already has a committed bubble for. Both the chunk loop and the
    // module ask this, so the walk over the set lives here rather than in each of them.
    public bool IsFinalized(string topicId, string? messageId) =>
        messageId is not null &&
        FinalizedMessageIdsByTopic.GetValueOrDefault(topicId)?.Contains(messageId) == true;

    // Bumped by everything that takes a message away or redraws a topic from the history, anywhere: a
    // history read is only put in whole (missed turns and all) when nothing like that happened since it was
    // asked for, or a read from before a delete would put back what the person just removed.
    public long Revision { get; init; }

    public static MessagesState Initial => new();
}