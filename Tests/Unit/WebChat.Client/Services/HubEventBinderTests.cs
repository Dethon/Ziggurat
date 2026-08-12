using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Services;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.Hub;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.Services;

public sealed class HubEventBinderTests : IDisposable
{
    private static readonly string[] _serverPushes =
    [
        "OnTopicChanged",
        "OnStreamStarted",
        "OnApprovalResolved",
        "OnUserMessage",
        "OnAgentsUpdated"
    ];

    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly MessagesStore _messagesStore;
    private readonly StreamingStore _streamingStore;
    private readonly HubEventBinder _binder;

    public HubEventBinderTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _messagesStore = new MessagesStore(_dispatcher);
        _streamingStore = new StreamingStore(_dispatcher);

        var topicStreams = new TopicStreams(_dispatcher, _messagesStore);
        var pipeline = new MessagePipeline(
            _dispatcher, _messagesStore, topicStreams, NullLogger<MessagePipeline>.Instance);
        var hubEventDispatcher = new HubEventDispatcher(
            _dispatcher, _topicsStore, topicStreams, pipeline);

        _binder = new HubEventBinder(hubEventDispatcher);
    }

    [Fact]
    public void Bind_RegistersEveryServerPushAgainstTheConnectionItWasGiven()
    {
        var connection = new FakeHubConnection();
        var other = new FakeHubConnection();

        _binder.Bind(connection);

        connection.BoundWireNames.ShouldBe(_serverPushes, ignoreOrder: true);
        other.BoundWireNames.ShouldBeEmpty();
    }

    [Fact]
    public void Unbind_ReleasesEveryRegistration()
    {
        var connection = new FakeHubConnection();
        _binder.Bind(connection);

        _binder.Unbind();

        connection.BoundWireNames.ShouldBeEmpty();
    }

    [Fact]
    public void Bind_ThenPush_ReachesTheStore()
    {
        var connection = new FakeHubConnection();
        _binder.Bind(connection);

        connection.Raise("OnTopicChanged", TopicCreated("topic-1"));

        _topicsStore.State.Topics.ShouldContain(topic => topic.TopicId == "topic-1");
    }

    [Fact]
    public void Bind_AfterAnEarlierBind_BindsAgainRatherThanSilentlyDoingNothing()
    {
        var first = new FakeHubConnection();
        var second = new FakeHubConnection();
        _binder.Bind(first);

        _binder.Bind(second);

        second.BoundWireNames.ShouldBe(_serverPushes, ignoreOrder: true);
    }

    [Fact]
    public void Bind_AgentsUpdatedPush_ReachesTheStore()
    {
        var connection = new FakeHubConnection();
        _binder.Bind(connection);

        connection.Raise<IReadOnlyList<AgentCatalogEntry>>(
            "OnAgentsUpdated", [new AgentCatalogEntry("agent-1", "Agent One", null)]);

        _topicsStore.State.Agents.ShouldContain(agent => agent.Id == "agent-1");
    }

    private static TopicChangedNotification TopicCreated(string topicId) =>
        new(TopicChangeType.Created, topicId, TestChat.Topic(topicId));

    public void Dispose()
    {
        _topicsStore.Dispose();
        _messagesStore.Dispose();
        _streamingStore.Dispose();
    }
}