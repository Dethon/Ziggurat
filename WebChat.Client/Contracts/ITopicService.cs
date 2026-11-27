using Domain.DTOs.Household;
using Domain.DTOs.WebChat;

namespace WebChat.Client.Contracts;

public interface ITopicService
{
    // One page, most recently written first. There is no call that returns every topic: the list
    // is read one way only, so a new caller cannot reintroduce a fetch that grows every week.
    // The filter narrows the page to the list the person chose; none is everything they see.
    Task<HubResult<TopicPage>> GetTopicPageAsync(
        string agentId,
        string spaceSlug = SpaceConfig.DefaultSlug,
        string? cursor = null,
        bool archived = false,
        TopicFilter? filter = null);
    Task<HubResult<Nothing>> SaveTopicAsync(TopicMetadata topic, bool isNew = false);

    // The owner's alone to ask: the server refuses anyone else, whatever the client shows.
    Task<HubResult<Nothing>> SetTopicSharedAsync(string agentId, long chatId, string topicId, bool shared);

    // Searching is a hub call, so it matches conversations that were never loaded and spans the
    // archive as well as the ordinary list.
    Task<HubResult<TopicPage>> SearchTopicsAsync(
        string agentId,
        string query,
        string spaceSlug = SpaceConfig.DefaultSlug,
        string? cursor = null,
        TopicFilter? filter = null);

    // Whose topics someone who sees all may narrow their list to; nobody for anyone else.
    Task<HubResult<IReadOnlyList<HouseholdMemberName>>> GetHouseholdPeopleAsync();

    // The server decides what "read" means here, because it is the only side that knows how many
    // messages the topic holds right now.
    Task<HubResult<Nothing>> MarkTopicReadAsync(string agentId, long chatId, string topicId);
    Task<HubResult<Nothing>> DeleteTopicAsync(string agentId, string topicId, long chatId, long threadId);
    Task<HubResult<IReadOnlyList<ChatHistoryMessage>>> GetHistoryAsync(string agentId, long chatId, long threadId);
    Task<HubResult<Nothing>> JoinSpaceAsync(string spaceSlug);

    // Rewrites of a conversation (ChatHub.DeleteFrom and its siblings). lastSeen is the entry id of the
    // last stored message the page shows, so a line that moved on is refused rather than cut blind.
    Task<HubResult<Nothing>> DeleteFromAsync(string agentId, string topicId, long chatId, string entryId, string? lastSeen);

    Task<HubResult<Nothing>> PutAsideFromAsync(string agentId, string topicId, long chatId, string entryId, string? lastSeen);

    Task<HubResult<Nothing>> EditInPlaceAsync(
        string agentId, string topicId, long chatId, string entryId, string text, string? lastSeen);

    Task<HubResult<Nothing>> PutAsideReplyAsync(string agentId, string topicId, long chatId, string userEntryId, string? lastSeen);

    Task<HubResult<ForkedTopic>> ForkAsync(string agentId, string topicId, long chatId, string entryId);

    Task<HubResult<Nothing>> SwitchVersionAsync(
        string agentId, string topicId, long chatId, string slot, int index, string? lastSeen);

    // The signed-in person's household's spaces, in the order it puts them: the hub's to say, from
    // the household's configuration, since this page's host knows nobody.
    Task<HubResult<IReadOnlyList<HouseholdSpace>>> GetSpacesAsync();

    // Every place the space switcher offers: the household's spaces (an app space with its app's link)
    // and the person's own apps no space shares.
    Task<HubResult<IReadOnlyList<SwitcherEntry>>> GetSpacesAndAppsAsync();
}