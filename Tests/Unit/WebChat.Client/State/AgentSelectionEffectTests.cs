using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Moq;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Contracts;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Space;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class AgentSelectionEffectTests : IDisposable
{
    private static readonly AgentCatalogEntry _agentOne = new("agent-1", "Agent One", null);
    private static readonly AgentCatalogEntry _agentTwo = new("agent-2", "Agent Two", null);

    private readonly CallRecorder _calls = new();
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly SpaceStore _spaceStore;
    private readonly Mock<IChatSessionService> _sessionService = new();
    private readonly FakeLocalStorageService _localStorage;
    private readonly FakeTopicService _topicService;
    private readonly FakeStreamResumeService _streamResumeService = new();
    private readonly RecordingLogger<AgentSelectionEffect> _logger = new();
    private readonly AgentSelectionEffect _effect;

    public AgentSelectionEffectTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);
        _spaceStore = new SpaceStore(_dispatcher);
        _localStorage = new FakeLocalStorageService(_calls);
        _topicService = new FakeTopicService(_calls);

        _effect = new AgentSelectionEffect(
            _topicsStore,
            _dispatcher,
            _sessionService.Object,
            _localStorage,
            _topicService,
            _streamResumeService,
            _spaceStore,
            _logger);
    }

    [Fact]
    public void SelectAgent_FirstSelection_LoadsNothing()
    {
        _topicService.SeedTopic(TestChat.Topic("topic-1"));

        _dispatcher.Dispatch(new SelectAgent("agent-1"));

        _calls.Calls.ShouldBeEmpty();
        _sessionService.Verify(s => s.ClearSession(), Times.Never);
    }

    [Fact]
    public async Task HandleAgentChangedAsync_SecondSelection_ClearsSessionPersistsAndReloadsTopics()
    {
        _dispatcher.Dispatch(new SelectAgent("agent-1"));
        _topicService.SeedTopic(TestChat.Topic("topic-2", chatId: 11, threadId: 21, agentId: "agent-2"));
        _topicService.SetHistory(11, 21, TestChat.HistoryMessage("m-1", "hello"));

        await _effect.HandleAgentChangedAsync("agent-2");

        _sessionService.Verify(s => s.ClearSession(), Times.Once);
        _localStorage.Values["selectedAgentId"].ShouldBe("agent-2");
        _topicsStore.State.Topics.Single().TopicId.ShouldBe("topic-2");

        // Switching agent costs one page of rows and nothing per conversation.
        _messagesStore.State.MessagesByTopic.ShouldBeEmpty();
        _calls.Calls.ShouldNotContain(call => call.StartsWith("history:"));
    }

    [Fact]
    public async Task HandleAgentChangedAsync_SameAgentAgain_DoesNothing()
    {
        _dispatcher.Dispatch(new SelectAgent("agent-1"));
        _calls.Reset();

        await _effect.HandleAgentChangedAsync("agent-1");

        _calls.Calls.ShouldBeEmpty();
        _sessionService.Verify(s => s.ClearSession(), Times.Never);
    }

    [Fact]
    public async Task SetAgents_DropsTheSelectedAgent_EmptiesTheTopicList()
    {
        _dispatcher.Dispatch(new SetAgents([_agentOne, _agentTwo]));
        _dispatcher.Dispatch(new SelectAgent("agent-1"));
        _dispatcher.Dispatch(new TopicsLoaded([StoredTopicFor("topic-1")]));

        _dispatcher.Dispatch(new SetAgents([]));

        await TestChat.Eventually(() => _topicsStore.State.Topics.Count == 0);
        _topicsStore.State.SelectedAgentId.ShouldBeNull();
        _localStorage.Values["selectedAgentId"].ShouldBe("");
    }

    [Fact]
    public async Task HandleAgentChangedAsync_TopicIsMidStream_KeepsItsLocalMessagesAndResumes()
    {
        _dispatcher.Dispatch(new SelectAgent("agent-1"));
        _topicService.SeedTopic(TestChat.Topic("topic-2", chatId: 11, threadId: 21, agentId: "agent-2"));
        _topicService.SetHistory(11, 21, TestChat.HistoryMessage("m-1", "from the server"));
        _dispatcher.Dispatch(new StreamStarted("topic-2"));
        _calls.Reset();

        await _effect.HandleAgentChangedAsync("agent-2");

        _calls.Calls.ShouldNotContain("history:11:21");
        _messagesStore.State.MessagesByTopic.ShouldNotContainKey("topic-2");
        _streamResumeService.ResumedTopicIds.ShouldBe(["topic-2"]);
    }

    [Fact]
    public async Task Dispatch_SelectAgent_RunsTheSameWork()
    {
        _dispatcher.Dispatch(new SelectAgent("agent-1"));
        _topicService.SeedTopic(TestChat.Topic("topic-2", chatId: 11, threadId: 21, agentId: "agent-2"));

        _dispatcher.Dispatch(new SelectAgent("agent-2"));

        await TestChat.Eventually(() => _topicsStore.State.Topics.Count == 1);
        _topicsStore.State.Topics.Single().TopicId.ShouldBe("topic-2");
        _sessionService.Verify(s => s.ClearSession(), Times.Once);
    }

    [Fact]
    public async Task Dispatch_SelectAgent_FaultIsLoggedRatherThanDiscarded()
    {
        _dispatcher.Dispatch(new SelectAgent("agent-1"));
        _topicService.ThrowOnGetAllTopics = new InvalidOperationException("topic list unavailable");

        _dispatcher.Dispatch(new SelectAgent("agent-2"));

        var entry = await _logger.WaitForEntryAsync();
        entry.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("topic list unavailable");
    }

    private static StoredTopic StoredTopicFor(string topicId) =>
        StoredTopic.FromMetadata(TestChat.Topic(topicId));

    public void Dispose()
    {
        _effect.Dispose();
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
        _spaceStore.Dispose();
    }
}