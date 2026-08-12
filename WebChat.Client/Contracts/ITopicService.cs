using Domain.DTOs.WebChat;

namespace WebChat.Client.Contracts;

public interface ITopicService
{
    // One page, most recently written first. There is no call that returns every topic: the list
    // is read one way only, so a new caller cannot reintroduce a fetch that grows every week.
    Task<HubResult<TopicPage>> GetTopicPageAsync(
        string agentId, string spaceSlug = SpaceConfig.DefaultSlug, string? cursor = null);
    Task<HubResult<Nothing>> SaveTopicAsync(TopicMetadata topic, bool isNew = false);
    Task<HubResult<Nothing>> DeleteTopicAsync(string agentId, string topicId, long chatId, long threadId);
    Task<HubResult<IReadOnlyList<ChatHistoryMessage>>> GetHistoryAsync(string agentId, long chatId, long threadId);
    Task<HubResult<Nothing>> JoinSpaceAsync(string spaceSlug);
}