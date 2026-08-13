namespace WebChat.Client.State.Topics;

public static class TopicListSelectors
{
    // The caption under an empty row area, or null when there is nothing to say: rows are there
    // to draw, or the list's answer is still on the wire — an empty list while loading is not a
    // result, and captioning it "Nothing found" would report a search that has not answered yet.
    public static string? EmptyLabel(TopicsState state) =>
        state.IsLoading || state.Topics.Any(t => t.AgentId == state.SelectedAgentId)
            ? null
            : !string.IsNullOrWhiteSpace(state.SearchQuery) ? "Nothing found"
            : state.ShowingArchived ? "Nothing archived"
            : "No conversations yet";
}
