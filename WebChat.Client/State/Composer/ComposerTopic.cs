using Domain.Conversations;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Space;
using WebChat.Client.State.Topics;

namespace WebChat.Client.State.Composer;

// An upload ticket is scoped to a topic, so attaching needs one. Picking a file into a composer
// with no conversation selected is a person starting a conversation with a file rather than a
// sentence, so the conversation is started here — the same session start the send path does, one
// step earlier.
//
// Its own type because it is the send path's work, not the composer's: leaving it inside the
// attachment effect gave that effect a second reason to change.
public sealed class ComposerTopic(
    IDispatcher dispatcher,
    TopicsStore topicsStore,
    SpaceStore spaceStore,
    IChatSessionService sessionService,
    ITopicService topicService)
{
    // Null when there is nothing to attach to and nothing that could be started: no agent chosen,
    // or a call that could not be made.
    public async Task<string?> EnsureAsync(string? topicId, IReadOnlyList<PickedFile> files)
    {
        if (!string.IsNullOrEmpty(topicId))
        {
            return await StartSessionIfNeededAsync(topicId);
        }

        var state = topicsStore.State;
        if (state.SelectedAgentId is null || files.Count == 0)
        {
            return null;
        }

        var identity = ConversationIdGenerator.Create();
        var topic = new StoredTopic
        {
            TopicId = identity.TopicId,
            ChatId = identity.ChatId,
            ThreadId = identity.ThreadId,
            AgentId = state.SelectedAgentId,
            Name = files[0].FileName,
            NameFromFile = true,
            CreatedAt = DateTime.UtcNow,
            SpaceSlug = spaceStore.State.CurrentSlug
        };

        var started = await sessionService.StartSessionAsync(topic);
        if (!started.IsLive || !started.Value)
        {
            return null;
        }

        dispatcher.Dispatch(new AddTopic(topic));
        dispatcher.Dispatch(new SelectTopic(topic.TopicId));
        dispatcher.Dispatch(new MessagesLoaded(topic.TopicId, []));
        await topicService.SaveTopicAsync(topic.ToMetadata(), isNew: true);
        return topic.TopicId;
    }

    private async Task<string?> StartSessionIfNeededAsync(string topicId)
    {
        if (sessionService.CurrentTopic?.TopicId == topicId)
        {
            return topicId;
        }

        var topic = topicsStore.State.Topics.FirstOrDefault(t => t.TopicId == topicId);
        if (topic is null)
        {
            return null;
        }

        var started = await sessionService.StartSessionAsync(topic);
        return started is { IsLive: true, Value: true } ? topicId : null;
    }
}