using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

public sealed class TopicRenameEffectTests : IDisposable
{
    private readonly CallRecorder _calls = new();
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;
    private readonly ToastStore _toastStore;
    private readonly FakeTopicService _topicService;
    private readonly RecordingLogger<TopicRenameEffect> _logger = new();
    private readonly TopicRenameEffect _effect;

    public TopicRenameEffectTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _toastStore = new ToastStore(_dispatcher);
        _topicService = new FakeTopicService(_calls);
        _effect = new TopicRenameEffect(_dispatcher, _topicsStore, _topicService, _logger);
    }

    [Fact]
    public async Task HandleRenameTopicAsync_SurroundingWhitespace_SavesTheTrimmedName()
    {
        GivenTopic("topic-1", "Old name");

        await _effect.HandleRenameTopicAsync("topic-1", "  New name  ");

        _topicService.SavedTopics.Single().Name.ShouldBe("New name");
        _topicsStore.State.Topics.Single().Name.ShouldBe("New name");
    }

    // Emptying the field is a person clearing it to retype, not asking for a nameless
    // conversation — the old name stands and nothing travels.
    [Fact]
    public async Task HandleRenameTopicAsync_BlankName_KeepsTheOldNameAndSavesNothing()
    {
        GivenTopic("topic-1", "Old name");

        await _effect.HandleRenameTopicAsync("topic-1", "   ");

        _topicService.SavedTopics.ShouldBeEmpty();
        _topicsStore.State.Topics.Single().Name.ShouldBe("Old name");
    }

    [Fact]
    public async Task HandleRenameTopicAsync_UnchangedName_SavesNothing()
    {
        GivenTopic("topic-1", "Old name");

        await _effect.HandleRenameTopicAsync("topic-1", "Old name");

        _topicService.SavedTopics.ShouldBeEmpty();
    }

    [Fact]
    public async Task HandleRenameTopicAsync_UnknownTopic_SavesNothing()
    {
        GivenTopic("topic-1", "Old name");

        await _effect.HandleRenameTopicAsync("topic-missing", "New name");

        _topicService.SavedTopics.ShouldBeEmpty();
    }

    // The rename only lands once the server took it, the way a delete only removes the row once
    // the server confirmed — a title that reads as saved while nothing was written is a lie the
    // next reload corrects.
    [Fact]
    public async Task HandleRenameTopicAsync_NotLive_KeepsTheOldNameAndShowsTheToast()
    {
        GivenTopic("topic-1", "Old name");
        _topicService.NotLive = true;

        await _effect.HandleRenameTopicAsync("topic-1", "New name");

        _topicsStore.State.Topics.Single().Name.ShouldBe("Old name");
        _toastStore.State.Toasts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task HandleRenameTopicAsync_RenamesOnlyTheNamedTopic()
    {
        _dispatcher.Dispatch(new TopicsLoaded([Topic("topic-1", "One"), Topic("topic-2", "Two")]));

        await _effect.HandleRenameTopicAsync("topic-1", "Renamed");

        _topicsStore.State.Topics.Select(t => t.Name).ShouldBe(["Renamed", "Two"]);
    }

    // The rest of the row is what the server already holds: renaming must not reset when the
    // conversation was created, last spoke, or how far it was read.
    [Fact]
    public async Task HandleRenameTopicAsync_KeepsTheRestOfTheTopic()
    {
        GivenTopic("topic-1", "Old name");

        await _effect.HandleRenameTopicAsync("topic-1", "New name");

        var saved = _topicService.SavedTopics.Single();
        saved.ChatId.ShouldBe(10);
        saved.ThreadId.ShouldBe(20);
        saved.AgentId.ShouldBe("agent-1");
        saved.LastReadMessageId.ShouldBe("m-1");
        saved.SpaceSlug.ShouldBe("default");
    }

    [Fact]
    public async Task Dispatch_RenameTopic_RunsTheSameWork()
    {
        GivenTopic("topic-1", "Old name");

        _dispatcher.Dispatch(new RenameTopic("topic-1", "New name"));

        await TestChat.Eventually(() => _topicsStore.State.Topics.Single().Name == "New name");
        _topicService.SavedTopics.Single().Name.ShouldBe("New name");
    }

    [Fact]
    public async Task Dispatch_RenameTopic_FaultIsLoggedRatherThanDiscarded()
    {
        GivenTopic("topic-1", "Old name");
        _topicService.ThrowOnSaveTopic = new InvalidOperationException("save rejected");

        _dispatcher.Dispatch(new RenameTopic("topic-1", "New name"));

        var entry = await _logger.WaitForEntryAsync();
        entry.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("save rejected");
    }

    [Fact]
    public async Task Disposed_StopsHandlingRenameTopic()
    {
        GivenTopic("topic-1", "Old name");
        _effect.Dispose();

        _dispatcher.Dispatch(new RenameTopic("topic-1", "New name"));

        await Task.Delay(50);
        _topicService.SavedTopics.ShouldBeEmpty();
    }

    private static StoredTopic Topic(string topicId, string name) => new()
    {
        TopicId = topicId,
        ChatId = 10,
        ThreadId = 20,
        AgentId = "agent-1",
        Name = name,
        CreatedAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        LastMessageAt = new DateTime(2026, 1, 2, 4, 5, 6, DateTimeKind.Utc),
        LastReadMessageId = "m-1"
    };

    private void GivenTopic(string topicId, string name)
    {
        _dispatcher.Dispatch(new TopicsLoaded([Topic(topicId, name)]));
        _calls.Reset();
    }

    public void Dispose()
    {
        _topicsStore.Dispose();
        _toastStore.Dispose();
    }
}