using Shouldly;
using WebChat.Client.Models;
using WebChat.Client.State.Topics;

namespace Tests.Unit.WebChat.Client.State;

// The sidebar's paging rules, tested where they live rather than through a rendered component.
public class TopicPagingTests
{
    private static readonly DateTime _base = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AppendPage_AddsTheRowsBelowWhatIsAlreadyHeld()
    {
        var paging = TopicPaging.FirstPage([Topic("a", 0), Topic("b", -1)], "100")
            .AppendPage([Topic("c", -2)], "50");

        paging.Topics.Select(t => t.TopicId).ShouldBe(["a", "b", "c"]);
        paging.Cursor.ShouldBe("50");
        paging.HasMore.ShouldBeTrue();
    }

    [Fact]
    public void AppendPage_APageWithNoCursor_EndsTheRange()
    {
        var paging = TopicPaging.FirstPage([Topic("a", 0)], nextCursor: null);

        paging.HasMore.ShouldBeFalse();
    }

    [Fact]
    public void HasMore_BeforeAnyPage_IsTrueSoTheFirstFetchHappens()
    {
        TopicPaging.Empty.HasMore.ShouldBeTrue();
    }

    // The row is already on screen further down the list. Without deduplication the push would
    // add a second one.
    [Fact]
    public void Upsert_ATopicAlreadyHeldThatGainedAMessage_MovesToTheTopAndIsNotDuplicated()
    {
        var paging = TopicPaging.FirstPage([Topic("a", 0), Topic("b", -1), Topic("c", -2)], "50");

        var bumped = paging.Upsert(Topic("c", 5));

        bumped.Topics.Select(t => t.TopicId).ShouldBe(["c", "a", "b"]);
    }

    // The row the cursor will now never reach: it was below the cursor, so paging backwards
    // will never fetch it again.
    [Fact]
    public void Upsert_ATopicThatWasNeverPagedTo_ArrivesAtTheTop()
    {
        var paging = TopicPaging.FirstPage([Topic("a", 0)], "50");

        var pushed = paging.Upsert(Topic("z", 5));

        pushed.Topics.Select(t => t.TopicId).ShouldBe(["z", "a"]);
    }

    [Fact]
    public void FirstPage_AfterAnInterruption_ReplacesWhatWasHeldAndItsCursor()
    {
        var paging = TopicPaging.FirstPage([Topic("a", 0), Topic("b", -1)], "50")
            .AppendPage([Topic("c", -2)], "10");

        var caughtUp = TopicPaging.FirstPage([Topic("b", 3)], "80");

        caughtUp.Topics.Select(t => t.TopicId).ShouldBe(["b"]);
        caughtUp.Cursor.ShouldBe("80");
        paging.Topics.Count.ShouldBe(3);
    }

    [Fact]
    public void Insert_ATopicAlreadyHeld_ChangesNothing()
    {
        var paging = TopicPaging.FirstPage([Topic("a", 0)], "50");

        paging.Insert(Topic("a", 9)).Topics.Single().LastMessageAt.ShouldBe(_base);
    }

    [Fact]
    public void Remove_DropsTheRow()
    {
        var paging = TopicPaging.FirstPage([Topic("a", 0), Topic("b", -1)], "50");

        paging.Remove("a").Topics.Select(t => t.TopicId).ShouldBe(["b"]);
    }

    private static StoredTopic Topic(string id, int hoursFromBase) => new()
    {
        TopicId = id,
        AgentId = "jack",
        Name = id,
        CreatedAt = _base,
        LastMessageAt = _base.AddHours(hoursFromBase)
    };
}