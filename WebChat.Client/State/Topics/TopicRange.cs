using Domain.DTOs.WebChat;
using WebChat.Client.Contracts;

namespace WebChat.Client.State.Topics;

// Which list the sidebar is reading: one agent's ordinary list, its archive, or a search. A
// page fetch is stamped with the range it was issued for, so an answer landing after the list
// changed underneath the round trip can be recognized as another list's and dropped.
public sealed record TopicRange(string AgentId, string SpaceSlug, bool Archived, string SearchQuery)
{
    public static TopicRange? Of(TopicsState state, string spaceSlug) =>
        state.SelectedAgentId is null
            ? null
            : new TopicRange(state.SelectedAgentId, spaceSlug, state.ShowingArchived, state.SearchQuery);

    // Which call a page fetch is depends on which list this range is: a search, the archive, or
    // the ordinary list. Paged the same way whichever it is.
    public Task<HubResult<TopicPage>> FetchPageAsync(ITopicService topicService, string? cursor) =>
        string.IsNullOrWhiteSpace(SearchQuery)
            ? topicService.GetTopicPageAsync(AgentId, SpaceSlug, cursor, Archived)
            : topicService.SearchTopicsAsync(AgentId, SearchQuery, SpaceSlug, cursor);
}
