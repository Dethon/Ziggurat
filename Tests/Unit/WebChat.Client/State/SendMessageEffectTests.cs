using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Space;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace Tests.Unit.WebChat.Client.State;

public sealed class SendMessageEffectTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly MessagesStore _messagesStore;
    private readonly TopicsStore _topicsStore;
    private readonly StreamingStore _streamingStore;
    private readonly SpaceStore _spaceStore;
    private readonly UserIdentityStore _userIdentityStore;
    private readonly ToastStore _toastStore;
    private readonly Mock<IChatSessionService> _mockSessionService;
    private readonly Mock<IStreamingService> _mockStreamingService;
    private readonly RecordingLogger<SendMessageEffect> _logger = new();
    private readonly SendMessageEffect _effect;

    public SendMessageEffectTests()
    {
        _dispatcher = new Dispatcher();
        _topicsStore = new TopicsStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _spaceStore = new SpaceStore(_dispatcher);
        _userIdentityStore = new UserIdentityStore(_dispatcher);
        _toastStore = new ToastStore(_dispatcher);
        _mockSessionService = new Mock<IChatSessionService>();
        _mockStreamingService = new Mock<IStreamingService>();

        var pipeline = new MessagePipeline(
            _dispatcher, _messagesStore, new TopicStreams(_dispatcher, _messagesStore),
            NullLogger<MessagePipeline>.Instance);

        _effect = new SendMessageEffect(
            _dispatcher,
            _topicsStore,
            _messagesStore,
            _mockSessionService.Object,
            _mockStreamingService.Object,
            new TopicStreams(_dispatcher, _messagesStore),
            new FakeTopicService(),
            new FakeChatMessagingService(),
            _userIdentityStore,
            pipeline,
            new ComposerStore(_dispatcher),
            _spaceStore,
            _logger);
    }

    [Fact]
    public void RetryLastMessage_NoUserMessages_DoesNotDispatchSendMessage()
    {
        // Arrange
        var messages = new List<ChatMessageModel>
        {
            new() { Role = "assistant", Content = "Error", IsError = true }
        };
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", messages));

        // Act
        _dispatcher.Dispatch(new RetryLastMessage("topic-1"));

        // Assert - errors removed, no user message sent
        var remaining = _messagesStore.State.MessagesByTopic["topic-1"];
        remaining.ShouldBeEmpty();
    }

    [Fact]
    public void RetryLastMessage_InterleavedErrors_PicksLastUserMessage()
    {
        // Arrange
        var topic = new StoredTopic
        { TopicId = "topic-1", AgentId = "agent-1", ChatId = 1, ThreadId = 1, Name = "Test" };
        _dispatcher.Dispatch(new TopicsLoaded([topic]));
        _dispatcher.Dispatch(new SelectTopic("topic-1"));

        _mockSessionService
            .Setup(s => s.CurrentTopic)
            .Returns(topic);

        var messages = new List<ChatMessageModel>
        {
            new() { Role = "user", Content = "First message" },
            new() { Role = "assistant", Content = "Old error", IsError = true },
            new() { Role = "user", Content = "Second message" },
            new() { Role = "assistant", Content = "New error", IsError = true }
        };
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", messages));

        // Act
        _dispatcher.Dispatch(new RetryLastMessage("topic-1"));

        // Assert - only trailing error removed, non-trailing error preserved
        var remaining = _messagesStore.State.MessagesByTopic["topic-1"];
        remaining.ShouldContain(m => m.Content == "Old error");
        remaining.ShouldNotContain(m => m.Content == "New error");

        // Assert - "Second message" was resent in its own place: the retry takes the failed turn's,
        // as the conversation the agent keeps does, so it reads once rather than twice.
        remaining.Count(m => m.Role == "user" && m.Content == "Second message").ShouldBe(1);
        remaining.Select(m => m.Content).Take(3).ShouldBe(["First message", "Old error", "Second message"]);
    }

    // The handler task is abandoned by construction, so a fault inside it — here the topic
    // was removed between the render and the dispatch — must be logged and told to the user
    // rather than vanishing with the task.
    [Fact]
    public async Task Dispatch_SendMessageForARemovedTopic_LogsTheFaultAndRaisesTheToast()
    {
        _dispatcher.Dispatch(new SendMessage("missing-topic", "hello"));

        var entry = await _logger.WaitForEntryAsync();
        entry.Exception.ShouldBeOfType<InvalidOperationException>();
        _toastStore.State.Toasts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Dispatch_SendMessageWhoseSendTaskFaults_LogsTheFaultAndRaisesTheToast()
    {
        var topic = new StoredTopic
        { TopicId = "topic-1", AgentId = "agent-1", ChatId = 1, ThreadId = 1, Name = "Test" };
        _dispatcher.Dispatch(new TopicsLoaded([topic]));
        _mockSessionService.Setup(s => s.CurrentTopic).Returns(topic);
        _mockStreamingService
            .Setup(s => s.SendMessageAsync(It.IsAny<StoredTopic>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(Task.FromException(new InvalidOperationException("send faulted")));

        _dispatcher.Dispatch(new SendMessage("topic-1", "hello"));

        var entry = await _logger.WaitForEntryAsync();
        entry.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("send faulted");
        _toastStore.State.Toasts.Count.ShouldBe(1);
    }

    // A conversation started while the list shows another person's is the starter's own: it is theirs
    // from the start, and the list moves to theirs, once the server holds it, so it is listed there.
    [Fact]
    public async Task ANewConversation_StartedWhileListingAnotherPersons_IsOwnAndMovesTheListToMine()
    {
        _dispatcher.Dispatch(new SignedIn(new SignedInPerson("olga", "Olga", null, "home", ["member", "sees-all"])));
        _dispatcher.Dispatch(new SelectAgent("jonas"));
        _dispatcher.Dispatch(new FilterTopics(TopicFilter.Of("fran")));
        StoredTopic? started = null;
        _mockSessionService.Setup(s => s.StartSessionAsync(It.IsAny<StoredTopic>()))
            .Callback<StoredTopic>(topic => started = topic)
            .ReturnsAsync(HubResult<bool>.Answered(true));
        var chosen = new List<TopicFilter>();
        using var _ = _dispatcher.RegisterHandler<ChooseTopicFilter>(action => chosen.Add(action.Filter));

        _dispatcher.Dispatch(new SendMessage(null, "hello"));

        await Tests.Eventually.Until(() => chosen.Count > 0, "the list to move to the starter's own");
        chosen.ShouldBe([TopicFilter.Mine]);
        (started!.OwnerId, started.TenantId).ShouldBe(("olga", "home"));
    }

    [Fact]
    public async Task ANewConversation_StartedWhileListingTheStartersOwn_LeavesTheListAsItIs()
    {
        _dispatcher.Dispatch(new SignedIn(new SignedInPerson("olga", "Olga", null, "home", ["member", "sees-all"])));
        _dispatcher.Dispatch(new SelectAgent("jonas"));
        _dispatcher.Dispatch(new FilterTopics(TopicFilter.Mine));
        _mockSessionService.Setup(s => s.StartSessionAsync(It.IsAny<StoredTopic>()))
            .ReturnsAsync(HubResult<bool>.Answered(true));
        var chosen = new List<TopicFilter>();
        using var _ = _dispatcher.RegisterHandler<ChooseTopicFilter>(action => chosen.Add(action.Filter));

        _dispatcher.Dispatch(new SendMessage(null, "hello"));

        await Tests.Eventually.Until(() => _topicsStore.State.Topics.Count == 1, "the new conversation's row");
        await Tests.Eventually.Settle();
        chosen.ShouldBeEmpty();
    }

    [Fact]
    public async Task Disposed_StopsHandlingSendMessage()
    {
        var topic = new StoredTopic
        { TopicId = "topic-1", AgentId = "agent-1", ChatId = 1, ThreadId = 1, Name = "Test" };
        _dispatcher.Dispatch(new TopicsLoaded([topic]));
        _mockSessionService.Setup(s => s.CurrentTopic).Returns(topic);
        _effect.Dispose();

        _dispatcher.Dispatch(new SendMessage("topic-1", "hello"));

        await Task.Delay(50);
        _mockStreamingService.Verify(
            s => s.SendMessageAsync(It.IsAny<StoredTopic>(), It.IsAny<string>(), It.IsAny<string?>()),
            Times.Never);
    }

    public void Dispose()
    {
        _effect.Dispose();
        _messagesStore.Dispose();
        _topicsStore.Dispose();
        _streamingStore.Dispose();
        _spaceStore.Dispose();
        _userIdentityStore.Dispose();
        _toastStore.Dispose();
    }
}