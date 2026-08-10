using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Messages;

namespace Tests.Unit.WebChat.Client.State;

public class MessagesStoreTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly MessagesStore _store;

    public MessagesStoreTests()
    {
        _dispatcher = new Dispatcher();
        _store = new MessagesStore(_dispatcher);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void MessagesLoaded_PopulatesMessagesByTopic()
    {
        // Arrange
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "Hello" },
            new() { Role = "assistant", Content = "Hi there" }
        };

        // Act
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", messages));

        // Assert
        _store.State.MessagesByTopic.TryGetValue("topic-1", out var topicMessages).ShouldBeTrue();
        topicMessages.Count.ShouldBe(2);
        topicMessages[0].Content.ShouldBe("Hello");
        topicMessages[1].Content.ShouldBe("Hi there");
    }

    [Fact]
    public void AddMessage_AppendsToExistingMessages()
    {
        // Arrange
        var initialMessages = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "Hello" }
        };
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", initialMessages));

        // Act
        _dispatcher.Dispatch(new AddMessage("topic-1", new ChatMessageModel { Role = "assistant", Content = "Hi" }));

        // Assert
        var messages = _store.State.MessagesByTopic["topic-1"];
        messages.Count.ShouldBe(2);
        messages[1].Role.ShouldBe("assistant");
        messages[1].Content.ShouldBe("Hi");
    }

    [Fact]
    public void AddMessage_CreatesListForNewTopic()
    {
        // Act
        _dispatcher.Dispatch(new AddMessage("new-topic",
            new ChatMessageModel { Role = "user", Content = "First message" }));

        // Assert
        _store.State.MessagesByTopic.TryGetValue("new-topic", out var messages).ShouldBeTrue();
        messages.Count.ShouldBe(1);
        messages[0].Content.ShouldBe("First message");
    }

    [Fact]
    public void ClearMessages_ClearsAllStateForTopic()
    {
        // Arrange
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "assistant", Content = "Test", MessageId = "msg-1" }
        };
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", messages));
        _store.State.FinalizedMessageIdsByTopic.ContainsKey("topic-1").ShouldBeTrue();
        _store.State.LoadedTopics.Contains("topic-1").ShouldBeTrue();

        // Act
        _dispatcher.Dispatch(new ClearMessages("topic-1"));

        // Assert
        _store.State.MessagesByTopic.ContainsKey("topic-1").ShouldBeFalse();
        _store.State.FinalizedMessageIdsByTopic.ContainsKey("topic-1").ShouldBeFalse();
        _store.State.LoadedTopics.Contains("topic-1").ShouldBeFalse();
    }

    [Fact]
    public void LoadedTopics_TracksLoadedTopicIds()
    {
        // Act
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", []));
        _dispatcher.Dispatch(new MessagesLoaded("topic-2", []));

        // Assert
        _store.State.LoadedTopics.Count.ShouldBe(2);
        _store.State.LoadedTopics.Contains("topic-1").ShouldBeTrue();
        _store.State.LoadedTopics.Contains("topic-2").ShouldBeTrue();
    }

    [Fact]
    public void DifferentTopics_HaveIndependentMessageLists()
    {
        // Arrange
        var topic1Messages = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "Topic 1 message" }
        };
        var topic2Messages = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "Topic 2 message" }
        };

        // Act
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", topic1Messages));
        _dispatcher.Dispatch(new MessagesLoaded("topic-2", topic2Messages));
        _dispatcher.Dispatch(
            new AddMessage("topic-1", new ChatMessageModel { Role = "assistant", Content = "Reply 1" }));

        // Assert
        _store.State.MessagesByTopic["topic-1"].Count.ShouldBe(2);
        _store.State.MessagesByTopic["topic-2"].Count.ShouldBe(1);
    }

    [Fact]
    public void ClearAllMessages_RemovesAllTopicMessages()
    {
        // Arrange
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", [new ChatMessageModel { Content = "msg1" }]));
        _dispatcher.Dispatch(new MessagesLoaded("topic-2", [new ChatMessageModel { Content = "msg2" }]));

        // Act
        _dispatcher.Dispatch(new ClearAllMessages());

        // Assert
        _store.State.MessagesByTopic.ShouldBeEmpty();
        _store.State.LoadedTopics.ShouldBeEmpty();
        _store.State.FinalizedMessageIdsByTopic.ShouldBeEmpty();
    }

    [Fact]
    public void RemoveTrailingErrors_RemovesAllTrailingErrorMessages()
    {
        // Arrange
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "Hello" },
            new() { Role = "assistant", Content = "Hi there" },
            new() { Role = "assistant", Content = "Error 1", IsError = true },
            new() { Role = "assistant", Content = "Error 2", IsError = true }
        };
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", messages));

        // Act
        _dispatcher.Dispatch(new RemoveTrailingErrors("topic-1"));

        // Assert
        var remaining = _store.State.MessagesByTopic["topic-1"];
        remaining.Count.ShouldBe(2);
        remaining[0].Content.ShouldBe("Hello");
        remaining[1].Content.ShouldBe("Hi there");
    }

    [Fact]
    public void RemoveTrailingErrors_PreservesNonTrailingErrors()
    {
        // Arrange
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "assistant", Content = "Earlier error", IsError = true },
            new() { Role = "user", Content = "Hello" },
            new() { Role = "assistant", Content = "Trailing error", IsError = true }
        };
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", messages));

        // Act
        _dispatcher.Dispatch(new RemoveTrailingErrors("topic-1"));

        // Assert
        var remaining = _store.State.MessagesByTopic["topic-1"];
        remaining.Count.ShouldBe(2);
        remaining[0].Content.ShouldBe("Earlier error");
        remaining[1].Content.ShouldBe("Hello");
    }

    [Fact]
    public void RemoveTrailingErrors_NoopWhenNoTrailingErrors()
    {
        // Arrange
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "Hello" },
            new() { Role = "assistant", Content = "Hi there" }
        };
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", messages));

        // Act
        _dispatcher.Dispatch(new RemoveTrailingErrors("topic-1"));

        // Assert
        var remaining = _store.State.MessagesByTopic["topic-1"];
        remaining.Count.ShouldBe(2);
    }

    [Fact]
    public void RemoveTrailingErrors_NoopForNonExistentTopic()
    {
        // Act
        _dispatcher.Dispatch(new RemoveTrailingErrors("non-existent"));

        // Assert
        _store.State.MessagesByTopic.ContainsKey("non-existent").ShouldBeFalse();
    }

    [Fact]
    public void RemoveTrailingErrors_RemovesAllMessagesWhenAllAreErrors()
    {
        // Arrange
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "assistant", Content = "Error 1", IsError = true },
            new() { Role = "assistant", Content = "Error 2", IsError = true }
        };
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", messages));

        // Act
        _dispatcher.Dispatch(new RemoveTrailingErrors("topic-1"));

        // Assert
        var remaining = _store.State.MessagesByTopic["topic-1"];
        remaining.Count.ShouldBe(0);
    }

}