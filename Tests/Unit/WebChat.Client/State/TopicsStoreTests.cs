using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public class TopicsStoreTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TopicsStore _store;

    public TopicsStoreTests()
    {
        _dispatcher = new Dispatcher();
        _store = new TopicsStore(_dispatcher);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void SelectTopic_WithNull_DeselectsTopic()
    {
        // Arrange
        _dispatcher.Dispatch(new SelectTopic("topic-1"));

        // Act
        _dispatcher.Dispatch(new SelectTopic(null));

        // Assert
        _store.State.SelectedTopicId.ShouldBeNull();
    }

    // The list is held most recently written first, so a topic that has just been created leads
    // it rather than landing at the end.
    [Fact]
    public void AddTopic_PutsTheNewTopicAtTheTopOfTheList()
    {
        // Arrange
        var initialTopics = new List<StoredTopic> { CreateTopic("topic-1", "Topic One") };
        _dispatcher.Dispatch(new TopicsLoaded(initialTopics));

        // Act
        _dispatcher.Dispatch(new AddTopic(CreateTopic("topic-2", "Topic Two", writtenAt: DateTime.UtcNow.AddHours(1))));

        // Assert
        _store.State.Topics.Count.ShouldBe(2);
        _store.State.Topics[0].TopicId.ShouldBe("topic-2");
    }

    [Fact]
    public void UpdateTopic_ReplacesExistingTopic()
    {
        // Arrange
        var topics = new List<StoredTopic> { CreateTopic("topic-1", "Original Name") };
        _dispatcher.Dispatch(new TopicsLoaded(topics));

        // Act
        _dispatcher.Dispatch(new UpdateTopic(CreateTopic("topic-1", "Updated Name")));

        // Assert
        _store.State.Topics.Count.ShouldBe(1);
        _store.State.Topics[0].Name.ShouldBe("Updated Name");
    }

    [Fact]
    public void TopicRemoved_RemovesFromTopicsList()
    {
        // Arrange
        var topics = new List<StoredTopic>
        {
            CreateTopic("topic-1", "Topic One"),
            CreateTopic("topic-2", "Topic Two")
        };
        _dispatcher.Dispatch(new TopicsLoaded(topics));

        // Act
        _dispatcher.Dispatch(new TopicRemoved("topic-1"));

        // Assert
        _store.State.Topics.Count.ShouldBe(1);
        _store.State.Topics[0].TopicId.ShouldBe("topic-2");
    }

    [Fact]
    public void TopicRemoved_ClearsSelectionIfSelectedTopicRemoved()
    {
        // Arrange
        var topics = new List<StoredTopic> { CreateTopic("topic-1", "Topic One") };
        _dispatcher.Dispatch(new TopicsLoaded(topics));
        _dispatcher.Dispatch(new SelectTopic("topic-1"));

        // Act
        _dispatcher.Dispatch(new TopicRemoved("topic-1"));

        // Assert
        _store.State.SelectedTopicId.ShouldBeNull();
    }

    [Fact]
    public void RemoveTopic_IsACommandAndLeavesTheListAlone()
    {
        // The row leaves the sidebar only on TopicRemoved, after the server confirmed
        // the delete — never optimistically on the RemoveTopic command itself.
        var topics = new List<StoredTopic> { CreateTopic("topic-1", "Topic One") };
        _dispatcher.Dispatch(new TopicsLoaded(topics));
        _dispatcher.Dispatch(new SelectTopic("topic-1"));

        _dispatcher.Dispatch(new RemoveTopic("topic-1"));

        _store.State.Topics.Count.ShouldBe(1);
        _store.State.SelectedTopicId.ShouldBe("topic-1");
    }

    [Fact]
    public void TopicsError_SetsErrorAndClearsIsLoading()
    {
        // Arrange
        _dispatcher.Dispatch(new LoadTopics());

        // Act
        _dispatcher.Dispatch(new TopicsError("Something went wrong"));

        // Assert
        _store.State.Error.ShouldBe("Something went wrong");
        _store.State.IsLoading.ShouldBeFalse();
    }

    [Fact]
    public void TopicsLoaded_ClearsError()
    {
        // Arrange
        _dispatcher.Dispatch(new TopicsError("Previous error"));

        // Act
        _dispatcher.Dispatch(new TopicsLoaded([]));

        // Assert
        _store.State.Error.ShouldBeNull();
    }

    [Fact]
    public void LoadTopics_SetsIsLoadingAndClearsError()
    {
        // Arrange
        _dispatcher.Dispatch(new TopicsError("Previous error"));

        // Act
        _dispatcher.Dispatch(new LoadTopics());

        // Assert
        _store.State.IsLoading.ShouldBeTrue();
        _store.State.Error.ShouldBeNull();
    }

    [Fact]
    public void SetAgents_UpdatesAgentsList()
    {
        // Arrange
        var agents = new List<AgentCatalogEntry>
        {
            new("agent-1", "Agent One", null),
            new("agent-2", "Agent Two", "Description")
        };

        // Act
        _dispatcher.Dispatch(new SetAgents(agents));

        // Assert
        _store.State.Agents.Count.ShouldBe(2);
        _store.State.Agents[0].Name.ShouldBe("Agent One");
    }

    [Fact]
    public void SetAgents_WhenSelectedAgentRemoved_ClearsSelectedTopic()
    {
        _dispatcher.Dispatch(new SetAgents([new("a", "A", null), new("b", "B", null)]));
        _dispatcher.Dispatch(new SelectAgent("b"));
        _dispatcher.Dispatch(new TopicsLoaded([CreateTopic("topic-b", "Topic B", "b")]));
        _dispatcher.Dispatch(new SelectTopic("topic-b"));

        _dispatcher.Dispatch(new SetAgents([new("a", "A", null), new("c", "C", null)]));

        _store.State.SelectedAgentId.ShouldBe("a");
        _store.State.SelectedTopicId.ShouldBeNull();
    }

    [Fact]
    public void SetAgents_WhenSelectedAgentStillPresent_KeepsSelectedTopic()
    {
        _dispatcher.Dispatch(new SetAgents([new("a", "A", null), new("b", "B", null)]));
        _dispatcher.Dispatch(new SelectAgent("b"));
        _dispatcher.Dispatch(new TopicsLoaded([CreateTopic("topic-b", "Topic B", "b")]));
        _dispatcher.Dispatch(new SelectTopic("topic-b"));

        _dispatcher.Dispatch(new SetAgents([new("b", "B", null), new("c", "C", null)]));

        _store.State.SelectedTopicId.ShouldBe("topic-b");
    }

    [Fact]
    public void SetAgents_WhenSelectedAgentRemovedAndListEmpty_ClearsSelection()
    {
        _dispatcher.Dispatch(new SetAgents([new("a", "A", null)]));
        _dispatcher.Dispatch(new SelectAgent("a"));

        _dispatcher.Dispatch(new SetAgents([]));

        _store.State.SelectedAgentId.ShouldBeNull();
    }

    [Fact]
    public void FromMetadata_PreservesSpaceSlug()
    {
        // Arrange
        var metadata = new TopicMetadata(
            "topic-1", 123L, 456L, "agent-1", "Test",
            DateTimeOffset.UtcNow, null, "my-space");

        // Act
        var topic = StoredTopic.FromMetadata(metadata);

        // Assert
        topic.SpaceSlug.ShouldBe("my-space");
    }

    [Fact]
    public void ToMetadata_PreservesSpaceSlug()
    {
        // Arrange
        var topic = new StoredTopic
        {
            TopicId = "topic-1", ChatId = 123, ThreadId = 456,
            AgentId = "agent-1", Name = "Test", SpaceSlug = "my-space",
            CreatedAt = DateTime.UtcNow
        };

        // Act
        var metadata = topic.ToMetadata();

        // Assert
        metadata.SpaceSlug.ShouldBe("my-space");
    }

    private static StoredTopic CreateTopic(
        string topicId, string name, string agentId = "agent-1", DateTime? writtenAt = null)
    {
        return new StoredTopic
        {
            TopicId = topicId,
            Name = name,
            AgentId = agentId,
            ChatId = 123,
            ThreadId = 456,
            CreatedAt = DateTime.UtcNow,
            LastMessageAt = writtenAt
        };
    }
}