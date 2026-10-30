using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

// The share toggle on a person's own topic: it asks the server, and the row says what the server
// took, the way a rename does.
public sealed class TopicShareEffectTests : IDisposable
{
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly ToastStore _toastStore;
    private readonly FakeTopicService _topicService = new();
    private readonly RecordingLogger<TopicShareEffect> _logger = new();
    private readonly TopicShareEffect _effect;

    public TopicShareEffectTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _toastStore = new ToastStore(_dispatcher);
        _effect = new TopicShareEffect(_dispatcher, _topicsStore, _topicService, _logger);
    }

    [Fact]
    public async Task SharingATopic_AsksTheServerAndTheRowSaysShared()
    {
        GivenTopic(shared: false);

        await _effect.HandleShareTopicAsync("topic-1", shared: true);

        _topicService.SharedChanges.ShouldBe([("agent-1", 10L, "topic-1", true)]);
        _topicsStore.State.Topics.Single().Shared.ShouldBeTrue();
    }

    [Fact]
    public async Task UnsharingATopic_AsksTheServerAndTheRowSaysPrivate()
    {
        GivenTopic(shared: true);

        await _effect.HandleShareTopicAsync("topic-1", shared: false);

        _topicService.SharedChanges.ShouldBe([("agent-1", 10L, "topic-1", false)]);
        _topicsStore.State.Topics.Single().Shared.ShouldBeFalse();
    }

    // A row that reads as shared while the server never heard is a privacy promise nobody kept.
    [Fact]
    public async Task ASharingTheServerNeverHeard_LeavesTheRowAsItWasAndSaysSo()
    {
        GivenTopic(shared: false);
        _topicService.NotLive = true;

        await _effect.HandleShareTopicAsync("topic-1", shared: true);

        _topicsStore.State.Topics.Single().Shared.ShouldBeFalse();
        _toastStore.State.Toasts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AskingForWhatTheTopicAlreadyIs_SendsNothing()
    {
        GivenTopic(shared: true);

        await _effect.HandleShareTopicAsync("topic-1", shared: true);

        _topicService.SharedChanges.ShouldBeEmpty();
    }

    [Fact]
    public async Task Dispatch_ShareTopic_RunsTheSameWork()
    {
        GivenTopic(shared: false);

        _dispatcher.Dispatch(new ShareTopic("topic-1", true));

        await TestChat.Eventually(() => _topicsStore.State.Topics.Single().Shared);
    }

    private void GivenTopic(bool shared) => _dispatcher.Dispatch(new TopicsLoaded([new StoredTopic
    {
        TopicId = "topic-1",
        ChatId = 10,
        ThreadId = 20,
        AgentId = "agent-1",
        Name = "Plum jam",
        CreatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        OwnerId = "fran",
        TenantId = "home",
        Shared = shared,
        OwnerName = "Fran"
    }]));

    public void Dispose()
    {
        _effect.Dispose();
        _topicsStore.Dispose();
        _toastStore.Dispose();
    }
}