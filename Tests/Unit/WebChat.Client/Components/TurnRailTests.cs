using Domain.DTOs.Channel;
using Shouldly;
using WebChat.Client.Components.Chat;
using WebChat.Client.Models;

namespace Tests.Unit.WebChat.Client.Components;

// The rail beside the conversation: one strip per turn, a turn being a message to the agent and
// everything up to the next one, so a long reply keeps its strip and a jump lands on the question.
public sealed class TurnRailTests
{
    private static ChatMessageModel Asks(string text) => new() { Role = "user", Content = text, SenderId = "ana" };

    private static ChatMessageModel Says(string text) => new() { Role = "assistant", Content = text };

    [Fact]
    public void EachMessageToTheAgent_StartsATurn_AnsweredByTheLastReplyBeforeTheNext()
    {
        var items = TurnRail.Items([
            Asks("first"), Says("thinking"), Says("one"),
            Asks("second"), Says("two")
        ]);

        items.ShouldBe([
            new TurnRailItem(0, "first", "one"),
            new TurnRailItem(3, "second", "two")
        ]);
    }

    [Fact]
    public void ATurnAServiceStarted_IsATurn_AskedAsTheLineThePageShows()
    {
        var fire = new ChatMessageModel { Role = "user", Content = "Check the plants", SenderId = ServiceSenders.Scheduler };

        TurnRail.Items([fire, Says("watered")])
            .ShouldBe([new TurnRailItem(0, "A scheduled task ran", "watered")]);
    }

    [Fact]
    public void RowsBeforeTheFirstAsk_AndRepliesWithNoWords_AreNoAnswer()
    {
        var items = TurnRail.Items([
            Says("welcome"),
            Asks("hi"), new ChatMessageModel { Role = "assistant", NoReply = true },
            Asks("again"), new ChatMessageModel { Role = "assistant", Content = "Model failed", IsError = true }
        ]);

        items.ShouldBe([
            new TurnRailItem(1, "hi", null),
            new TurnRailItem(3, "again", null)
        ]);
    }

    [Fact]
    public void APreview_IsTheWordsTheMarkdownShows_OnOneLine()
    {
        TurnRail.Preview("# Plan\n\n- **buy** [milk](https://shop.example)\n- `eggs`")
            .ShouldBe("Plan buy milk eggs");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void APreviewOfNoWords_IsNone(string? markdown) =>
        TurnRail.Preview(markdown).ShouldBeNull();

    [Fact]
    public void APreview_ReadsOnlyTheStartOfALongMessage()
    {
        var preview = TurnRail.Preview(new string('a', 10_000));

        preview.ShouldNotBeNull();
        preview.Length.ShouldBeLessThanOrEqualTo(TurnRail.PreviewSource);
    }

    [Fact]
    public void ARailTallEnoughForEveryTurn_DrawsAStripEach()
    {
        TurnRail.MarkCount(itemCount: 10, railHeight: 72).ShouldBe(10);
        TurnRail.MarkCount(itemCount: 10, railHeight: 0).ShouldBe(10);
    }

    [Fact]
    public void ARailTooShortForEveryTurn_LetsNeighboursShareAStrip()
    {
        TurnRail.MarkCount(itemCount: 200, railHeight: 100).ShouldBe(21);
        TurnRail.MarkCount(itemCount: 200, railHeight: 1).ShouldBe(2);
    }

    [Fact]
    public void ATurnsStrip_IsTheOneNearestItsPlaceOnTheRail()
    {
        TurnRail.MarkOf(itemIndex: 3, itemCount: 5, markCount: 5).ShouldBe(3);
        TurnRail.MarkOf(itemIndex: 0, itemCount: 200, markCount: 21).ShouldBe(0);
        TurnRail.MarkOf(itemIndex: 199, itemCount: 200, markCount: 21).ShouldBe(20);
        TurnRail.MarkOf(itemIndex: 100, itemCount: 200, markCount: 21).ShouldBe(10);
    }

    [Theory]
    [InlineData(0, 5, 0)]
    [InlineData(4, 5, 100)]
    [InlineData(1, 5, 25)]
    [InlineData(0, 1, 0)]
    public void AStripSits_AtItsShareOfTheRail(int index, int count, double percent) =>
        TurnRail.TopPercent(index, count).ShouldBe(percent);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-20, 0)]
    [InlineData(80, 4)]
    [InlineData(500, 4)]
    [InlineData(29, 1)]
    [InlineData(31, 2)]
    public void ThePointer_PicksTheNearestTurn(double offsetY, int index) =>
        TurnRail.IndexAt(offsetY, railHeight: 80, itemCount: 5).ShouldBe(index);

    [Fact]
    public void ARailNotYetMeasured_PicksNoTurn() =>
        TurnRail.IndexAt(10, railHeight: 0, itemCount: 5).ShouldBeNull();

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(1, 0.667)]
    [InlineData(2, 0.417)]
    [InlineData(3, 0.333)]
    [InlineData(null, 0.333)]
    public void TheStripsBesideThePointer_Swell(int? distance, double scale) =>
        TurnRail.Swell(distance).ShouldBe(scale);
}