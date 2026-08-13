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

    // The topic gained a message while the page carrying its older copy was on the wire. The
    // push already showed the new snippet and count at the top; the page landing must not put
    // the old ones back.
    [Fact]
    public void AppendPage_ARowThatGainedAMessageDuringTheRoundTrip_KeepsThePushedCopy()
    {
        var pushed = Topic("c", 5);
        pushed.MessageCount = 3;
        pushed.LastMessageSnippet = "the new reply";
        var paging = TopicPaging.FirstPage([Topic("a", 0)], "100").Upsert(pushed);

        var stale = Topic("c", -2);
        stale.MessageCount = 2;
        stale.LastMessageSnippet = "the old reply";
        var landed = paging.AppendPage([stale], "50");

        landed.Topics.Select(t => t.TopicId).ShouldBe(["c", "a"]);
        landed.Topics[0].MessageCount.ShouldBe(3);
        landed.Topics[0].LastMessageSnippet.ShouldBe("the new reply");
    }

    // Two copies written at the same instant tell their freshness apart by how much of the
    // conversation each has seen.
    [Fact]
    public void AppendPage_TwoCopiesOfTheSameMoment_KeepsTheOneThatSawMoreMessages()
    {
        var held = Topic("c", 0);
        held.MessageCount = 3;
        var paging = TopicPaging.FirstPage([held], "100");

        var stale = Topic("c", 0);
        stale.MessageCount = 2;
        var landed = paging.AppendPage([stale], "50");

        landed.Topics.Single().MessageCount.ShouldBe(3);
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