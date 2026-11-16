using Domain.DTOs.WebChat;
using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace Tests.Unit.WebChat.Client.State;

// A row is held only while the list's filter lists it, however it arrives: news from the hub, a topic
// this page started, a rename or a share the page made itself. A row the filter leaves out would sit
// in another person's list and count on its badge until the next reload took it away.
public sealed class TopicListingTests : IDisposable
{
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topicsStore;

    public TopicListingTests()
    {
        _topicsStore = new TopicsStore(_dispatcher);
        _dispatcher.Dispatch(new SignedIn(new SignedInPerson("olga", "Olga", null, "home", ["member", "sees-all"])));
    }

    private static StoredTopic Topic(string id, string owner) => new()
    {
        TopicId = id, AgentId = "jonas", Name = id, ChatId = 1, ThreadId = 1, OwnerId = owner, TenantId = "home"
    };

    [Fact]
    public void ATopicTheFilterLeavesOut_GetsNoRow_WhetherAddedOrUpdated()
    {
        _dispatcher.Dispatch(new FilterTopics(TopicFilter.Of("fran")));

        _dispatcher.Dispatch(new AddTopic(Topic("olgas-new", "olga")));
        _dispatcher.Dispatch(new UpdateTopic(Topic("olgas-renamed", "olga")));

        _topicsStore.State.Topics.ShouldBeEmpty();
    }

    [Fact]
    public void ARowTheFilterNoLongerLists_IsLetGo_AndAnOpenTopicStaysOpen()
    {
        _dispatcher.Dispatch(new FilterTopics(TopicFilter.Everyone));
        _dispatcher.Dispatch(new TopicsLoaded([Topic("frans", "fran")]));
        _dispatcher.Dispatch(new SelectTopic("frans"));
        _dispatcher.Dispatch(new FilterTopics(TopicFilter.Mine));
        _dispatcher.Dispatch(new TopicsLoaded([]));

        _dispatcher.Dispatch(new UpdateTopic(Topic("frans", "fran")));

        _topicsStore.State.Topics.ShouldBeEmpty();
        _topicsStore.State.SelectedTopicId.ShouldBe("frans");
    }

    [Fact]
    public void ATopicTheFilterLists_GetsItsRow()
    {
        _dispatcher.Dispatch(new FilterTopics(TopicFilter.Of("fran")));

        _dispatcher.Dispatch(new AddTopic(Topic("frans", "fran")));

        _topicsStore.State.Topics.Select(t => t.TopicId).ShouldBe(["frans"]);
    }

    public void Dispose() => _topicsStore.Dispose();
}