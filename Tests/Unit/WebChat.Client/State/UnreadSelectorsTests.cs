using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.State;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

// Rewritten against server-supplied counts. The old rule scanned the loaded history backwards
// for a stored message id and returned the whole list when it was not found — which, once
// history stopped loading up front, would have meant every row reading unread.
public sealed class UnreadSelectorsTests : IDisposable
{
    private readonly Dispatcher _dispatcher = new();
    private readonly TopicsStore _topics;

    public UnreadSelectorsTests()
    {
        _topics = new TopicsStore(_dispatcher);
    }

    public void Dispose() => _topics.Dispose();

    [Fact]
    public void UnreadIsTheDifferenceBetweenWhatIsHeldAndWhatWasRead()
    {
        _dispatcher.Dispatch(new TopicsLoaded([Topic("t1", held: 5, read: 3)]));

        UnreadSelectors.ComputeUnreadCounts(_topics.State)["t1"].ShouldBe(2);
    }

    [Fact]
    public void ATopicNobodyHasOpened_CountsEverythingItHolds()
    {
        _dispatcher.Dispatch(new TopicsLoaded([Topic("t1", held: 4, read: 0)]));

        UnreadSelectors.ComputeUnreadCounts(_topics.State)["t1"].ShouldBe(4);
    }

    [Fact]
    public void AFullyReadTopic_CarriesNoBadge()
    {
        _dispatcher.Dispatch(new TopicsLoaded([Topic("t1", held: 4, read: 4)]));

        UnreadSelectors.ComputeUnreadCounts(_topics.State).ContainsKey("t1").ShouldBeFalse();
    }

    [Fact]
    public void SelectedTopic_IsNeverUnread()
    {
        _dispatcher.Dispatch(new TopicsLoaded([Topic("t1", held: 5, read: 1)]));
        _dispatcher.Dispatch(new SelectTopic("t1"));

        UnreadSelectors.ComputeUnreadCounts(_topics.State).ContainsKey("t1").ShouldBeFalse();
    }

    // A read position ahead of the count is what a client sees between clearing a badge locally
    // and the page that says so coming back.
    [Fact]
    public void AReadPositionAheadOfTheCount_CountsNothing()
    {
        _dispatcher.Dispatch(new TopicsLoaded([Topic("t1", held: 2, read: 5)]));

        UnreadSelectors.ComputeUnreadCounts(_topics.State).ContainsKey("t1").ShouldBeFalse();
    }

    private static StoredTopic Topic(string id, long held, long read) =>
        new() { TopicId = id, AgentId = "a1", Name = id, MessageCount = held, ReadPosition = read };
}