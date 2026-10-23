using Domain.DTOs.Sugary;
using McpServerSugary.Store;
using Shouldly;

namespace Tests.Unit.McpServerSugary;

// A pen is replaced the day its days run out: opened on the 1st and lasting 28 days, the 29th. She
// is told the day before and again on the day, once each, and a pen she has already replaced, or
// whose date she has moved since, is told about as it now stands.
public class PenRulesTests
{
    private static readonly DateOnly Opened = new(2026, 10, 1);

    private static Pen Pen(int days = 28, DateOnly? replacedOn = null) => new()
    {
        Id = Guid.NewGuid(), Kind = DoseKind.Rapid, Insulin = "Fiasp", OpenedOn = Opened, Days = days,
        ReplacedOn = replacedOn
    };

    [Fact]
    public void APen_IsReplacedTheDayItsDaysRunOut() =>
        Pen().ReplaceOn.ShouldBe(new DateOnly(2026, 10, 29));

    [Fact]
    public void BeforeTheDayBefore_NothingIsDue() =>
        PenRules.Due(Pen(), new DateOnly(2026, 10, 27)).ShouldBeNull();

    [Fact]
    public void TheDayBefore_TomorrowIsDue() =>
        PenRules.Due(Pen(), new DateOnly(2026, 10, 28)).ShouldBe(PenReminder.Tomorrow);

    [Fact]
    public void OnTheDay_TodayIsDue() =>
        PenRules.Due(Pen(), new DateOnly(2026, 10, 29)).ShouldBe(PenReminder.Today);

    // A server that was down on the day still tells her once it is back.
    [Fact]
    public void AfterTheDay_TodayIsStillDue() =>
        PenRules.Due(Pen(), new DateOnly(2026, 11, 2)).ShouldBe(PenReminder.Today);

    [Fact]
    public void AReminderSent_IsNotDueAgain()
    {
        var pen = Pen();
        pen.Sent(PenReminder.Tomorrow);
        PenRules.Due(pen, new DateOnly(2026, 10, 28)).ShouldBeNull();

        pen.Sent(PenReminder.Today);
        PenRules.Due(pen, new DateOnly(2026, 10, 30)).ShouldBeNull();
    }

    // Sent for the date the pen had then: a pen she says she opened later is told about again.
    [Fact]
    public void AMovedPen_IsRemindedAgainForItsNewDate()
    {
        var pen = Pen();
        pen.Sent(PenReminder.Tomorrow);

        pen.OpenedOn = Opened.AddDays(3);

        PenRules.Due(pen, new DateOnly(2026, 10, 31)).ShouldBe(PenReminder.Tomorrow);
    }

    [Fact]
    public void AReplacedPen_IsNeverDue() =>
        PenRules.Due(Pen(replacedOn: new DateOnly(2026, 10, 20)), new DateOnly(2026, 10, 29)).ShouldBeNull();

    [Theory]
    [InlineData("Fiasp", 28, null)]
    [InlineData(" ", 28, PenRules.NoInsulin)]
    [InlineData(null, 28, PenRules.NoInsulin)]
    [InlineData("Fiasp", 0, PenRules.DaysOutOfRange)]
    [InlineData("Fiasp", 121, PenRules.DaysOutOfRange)]
    [InlineData("Fiasp", null, PenRules.DaysOutOfRange)]
    public void APen_NeedsAnInsulinAndItsDays(string? insulin, int? days, string? problem) =>
        PenRules.Problem(insulin, Opened, days, today: Opened).ShouldBe(problem);

    [Fact]
    public void APen_IsNotOpenedAfterTomorrow()
    {
        PenRules.Problem("Fiasp", Opened.AddDays(1), 28, today: Opened).ShouldBeNull();
        PenRules.Problem("Fiasp", Opened.AddDays(2), 28, today: Opened).ShouldBe(PenRules.OpenedInFuture);
    }

    [Fact]
    public void APen_NeedsTheDayItWasOpened() =>
        PenRules.Problem("Fiasp", null, 28, today: Opened).ShouldBe(PenRules.NoOpenedOn);

    [Fact]
    public void AnInsulinName_IsKeptShort() =>
        PenRules.Problem(new string('x', PenRules.MaxInsulin + 1), Opened, 28, today: Opened)
            .ShouldBe(PenRules.InsulinTooLong);
}