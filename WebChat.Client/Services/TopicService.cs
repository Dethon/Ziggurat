using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class TopicService(IChatLiveConnection liveConnection) : ITopicService
{
    public Task<HubResult<TopicPage>> GetTopicPageAsync(
        string agentId,
        string spaceSlug = SpaceConfig.DefaultSlug,
        string? cursor = null,
        bool archived = false) =>
        // The page size is the server's to decide, so it is asked for as nothing rather than
        // guessed at here.
        liveConnection.InvokeAsync<TopicPage>("GetTopicPage", agentId, spaceSlug, cursor, null, archived);

    public Task<HubResult<Nothing>> SaveTopicAsync(TopicMetadata topic, bool isNew = false) =>
        liveConnection.InvokeAsync("SaveTopic", topic, isNew);

    public Task<HubResult<TopicPage>> SearchTopicsAsync(
        string agentId,
        string query,
        string spaceSlug = SpaceConfig.DefaultSlug,
        string? cursor = null) =>
        liveConnection.InvokeAsync<TopicPage>("SearchTopics", agentId, query, spaceSlug, cursor, null);

    public Task<HubResult<Nothing>> MarkTopicReadAsync(string agentId, long chatId, string topicId) =>
        liveConnection.InvokeAsync("MarkTopicRead", agentId, chatId, topicId);

    public Task<HubResult<Nothing>> DeleteTopicAsync(string agentId, string topicId, long chatId, long threadId) =>
        liveConnection.InvokeAsync("DeleteTopic", agentId, topicId, chatId, threadId);

    public Task<HubResult<IReadOnlyList<ChatHistoryMessage>>> GetHistoryAsync(
        string agentId, long chatId, long threadId) =>
        liveConnection.InvokeAsync<IReadOnlyList<ChatHistoryMessage>>("GetHistory", agentId, chatId, threadId);

    public Task<HubResult<Nothing>> JoinSpaceAsync(string spaceSlug) =>
        liveConnection.InvokeAsync("JoinSpace", spaceSlug);
}