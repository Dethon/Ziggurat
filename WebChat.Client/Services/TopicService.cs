using Domain.DTOs.Household;
using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;

namespace WebChat.Client.Services;

public sealed class TopicService(IChatLiveConnection liveConnection) : ITopicService
{
    public Task<HubResult<TopicPage>> GetTopicPageAsync(
        string agentId,
        string spaceSlug = SpaceConfig.DefaultSlug,
        string? cursor = null,
        bool archived = false,
        TopicFilter? filter = null) =>
        // The page size is the server's to decide, so it is asked for as nothing rather than
        // guessed at here.
        liveConnection.InvokeAsync<TopicPage>(
            "GetTopicPageFiltered", agentId, spaceSlug, cursor, null, archived, filter ?? TopicFilter.Everyone);

    public Task<HubResult<Nothing>> SaveTopicAsync(TopicMetadata topic, bool isNew = false) =>
        liveConnection.InvokeAsync("SaveTopic", topic, isNew);

    public Task<HubResult<Nothing>> SetTopicSharedAsync(string agentId, long chatId, string topicId, bool shared) =>
        liveConnection.InvokeAsync("SetTopicShared", agentId, chatId, topicId, shared);

    public Task<HubResult<TopicPage>> SearchTopicsAsync(
        string agentId,
        string query,
        string spaceSlug = SpaceConfig.DefaultSlug,
        string? cursor = null,
        TopicFilter? filter = null) =>
        liveConnection.InvokeAsync<TopicPage>(
            "SearchTopicsFiltered", agentId, query, spaceSlug, cursor, null, filter ?? TopicFilter.Everyone);

    public Task<HubResult<IReadOnlyList<HouseholdMemberName>>> GetHouseholdPeopleAsync() =>
        liveConnection.InvokeAsync<IReadOnlyList<HouseholdMemberName>>("GetHouseholdPeople");

    public Task<HubResult<Nothing>> MarkTopicReadAsync(string agentId, long chatId, string topicId) =>
        liveConnection.InvokeAsync("MarkTopicRead", agentId, chatId, topicId);

    public Task<HubResult<Nothing>> DeleteTopicAsync(string agentId, string topicId, long chatId, long threadId) =>
        liveConnection.InvokeAsync("DeleteTopic", agentId, topicId, chatId, threadId);

    public Task<HubResult<IReadOnlyList<ChatHistoryMessage>>> GetHistoryAsync(
        string agentId, long chatId, long threadId) =>
        liveConnection.InvokeAsync<IReadOnlyList<ChatHistoryMessage>>("GetHistory", agentId, chatId, threadId);

    public Task<HubResult<Nothing>> DeleteFromAsync(
        string agentId, string topicId, long chatId, string entryId, string? lastSeen) =>
        liveConnection.InvokeAsync("DeleteFrom", agentId, topicId, chatId, entryId, lastSeen);

    public Task<HubResult<Nothing>> PutAsideFromAsync(
        string agentId, string topicId, long chatId, string entryId, string? lastSeen) =>
        liveConnection.InvokeAsync("PutAsideFrom", agentId, topicId, chatId, entryId, lastSeen);

    public Task<HubResult<Nothing>> SwitchVersionAsync(
        string agentId, string topicId, long chatId, string slot, int index, string? lastSeen) =>
        liveConnection.InvokeAsync("SwitchVersion", agentId, topicId, chatId, slot, index, lastSeen);

    public Task<HubResult<Nothing>> JoinSpaceAsync(string spaceSlug) =>
        liveConnection.InvokeAsync("JoinSpace", spaceSlug);

    public Task<HubResult<IReadOnlyList<HouseholdSpace>>> GetSpacesAsync() =>
        liveConnection.InvokeAsync<IReadOnlyList<HouseholdSpace>>("GetSpaces");
}