using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Models;

namespace WebChat.Client.State.Topics;

// Resuming exactly the replies a page said were in flight. Every path that loads a page goes
// through here, so none of them can go back to asking about each topic in turn.
public static class TopicPageStreams
{
    public static void ResumeReported(
        IReadOnlyList<StoredTopic> topics,
        IReadOnlyList<string>? liveTopicIds,
        IStreamResumeService streamResumeService,
        ILogger logger)
    {
        if (liveTopicIds is not { Count: > 0 })
        {
            return;
        }

        var live = liveTopicIds.ToHashSet();

        // Detached on purpose: a resumed stream is long-lived, so awaiting one would mean
        // awaiting the conversation it belongs to.
        topics
            .Where(t => live.Contains(t.TopicId))
            .ToList()
            .ForEach(t => streamResumeService.TryResumeStreamAsync(t).LogFaults(logger, "stream resume"));
    }
}