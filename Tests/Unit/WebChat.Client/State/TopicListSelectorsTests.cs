using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

// What the sidebar says under an empty row area, decided where it can be tested without
// rendering anything.
public class TopicListSelectorsTests
{
    // Every keystroke wipes the list before the debounced fetch answers. That gap is not a
    // result, so it carries no caption at all.
    [Fact]
    public void AnEmptyListStillLoading_ShowsNoLabel()
    {
        var state = TopicsState.Initial with { IsLoading = true, SearchQuery = "abc" };

        TopicListSelectors.EmptyLabel(state).ShouldBeNull();
    }

    [Fact]
    public void AnEmptySearchAnswer_SaysNothingFound()
    {
        var state = TopicsState.Initial with { SearchQuery = "abc" };

        TopicListSelectors.EmptyLabel(state).ShouldBe("Nothing found");
    }

    [Fact]
    public void AnEmptyArchive_SaysNothingArchived()
    {
        var state = TopicsState.Initial with { ShowingArchived = true };

        TopicListSelectors.EmptyLabel(state).ShouldBe("Nothing archived");
    }

    [Fact]
    public void AnEmptyOrdinaryList_SaysNoConversationsYet()
    {
        TopicListSelectors.EmptyLabel(TopicsState.Initial).ShouldBe("No conversations yet");
    }

    [Fact]
    public void ARowForTheSelectedAgent_NeedsNoLabel()
    {
        var state = TopicsState.Initial with
        {
            SelectedAgentId = "agent-1",
            Paging = TopicPaging.FirstPage([Row("agent-1")], null)
        };

        TopicListSelectors.EmptyLabel(state).ShouldBeNull();
    }

    // The row area filters to the selected agent, so a row held for another agent still leaves
    // it empty.
    [Fact]
    public void OnlyAnotherAgentsRows_StillLabelTheEmptyList()
    {
        var state = TopicsState.Initial with
        {
            SelectedAgentId = "agent-1",
            Paging = TopicPaging.FirstPage([Row("agent-2")], null)
        };

        TopicListSelectors.EmptyLabel(state).ShouldBe("No conversations yet");
    }

    private static StoredTopic Row(string agentId) => new()
    {
        TopicId = $"topic-of-{agentId}",
        AgentId = agentId,
        Name = "Topic",
        CreatedAt = new DateTime(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc)
    };
}
