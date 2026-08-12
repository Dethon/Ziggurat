using Domain.DTOs.WebChat;

namespace WebChat.Client.Contracts;

public interface ITopicService
{
    // One page, most recently written first. There is no call that returns every topic: the list
    // is read one way only, so a new caller cannot reintroduce a fetch that grows every week.
    Task<HubResult<TopicPage>> GetTopicPageAsync(
        string agentId,
        string spaceSlug = SpaceConfig.DefaultSlug,
        string? cursor = null,
        bool archived = false);
    Task<HubResult<Nothing>> SaveTopicAsync(TopicMetadata topic, bool isNew = false);

    // Searching is a hub call, so it matches conversations that were never loaded and spans the
    // archive as well as the ordinary list.
    Task<HubResult<TopicPage>> SearchTopicsAsync(
        string agentId,
        string query,
        string spaceSlug = SpaceConfig.DefaultSlug,
        string? cursor = null);

    // The server decides what "read" means here, because it is the only side that knows how many
    // messages the topic holds right now.
    Task<HubResult<Nothing>> MarkTopicReadAsync(string agentId, long chatId, string topicId);
    Task<HubResult<Nothing>> DeleteTopicAsync(string agentId, string topicId, long chatId, long threadId);
    Task<HubResult<IReadOnlyList<ChatHistoryMessage>>> GetHistoryAsync(string agentId, long chatId, long threadId);
    Task<HubResult<Nothing>> JoinSpaceAsync(string spaceSlug);
}