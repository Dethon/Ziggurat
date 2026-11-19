using System.Globalization;
using Domain.DTOs.Sugary;
using Domain.Tools.Sugary.Watches;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Sugary.Watches;

// A glucose watch's triggers judged as a pure function of her readings, her link, her sensor, what was
// judged before and the clock: each fires on entering its condition (after its `for`), not again until
// it left, and never on the first judgement of a trigger, which records a condition already holding as
// fired.
public class GlucoseTriggersTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo _madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    private static GlucoseWatchSpec Watch(string triggers, string conditions = "") =>
        GlucoseWatchSpec.Parse($$"""
            {"name":"w","triggers":[{{triggers}}],"effects":[{"kind":"prompt","prompt":"Tell her"}]{{conditions}}}
            """);

    // Her readings arriving one by one, each judged as a poll would judge it.
    private sealed class Series(GlucoseWatchSpec watch, DateTimeOffset? start = null)
    {
        private readonly List<SensorReading> _readings = [];
        private readonly List<int> _fired = [];

        public FakeTimeProvider Clock { get; } = new(start ?? _start);
        public IReadOnlyList<GlucoseTriggerCondition> States { get; private set; } = [];
        public GlucoseLinkHealth? Link { get; set; }
        public DateTimeOffset? SensorActivatedAt { get; set; }
        public IReadOnlyList<int> Fired => _fired;
        public IReadOnlyList<GlucoseTriggerFire> LastFires { get; private set; } = [];

        public DateTimeOffset Now => Clock.GetUtcNow();

        // A reading measured now, judged on its poll.
        public void Read(double mgPerDl, GlucoseTrend trend = GlucoseTrend.Unknown)
        {
            _readings.Add(new SensorReading(Now, mgPerDl, trend));
            Link ??= new GlucoseLinkHealth(_start - TimeSpan.FromDays(1), Now, null);
            Link = Link with { LastSuccessAt = Now, FailingSince = null };
            Judge(GlucoseOccasion.Poll, _readings[^1]);
        }

        public void Judge(GlucoseOccasion occasion, SensorReading? newest = null)
        {
            var judgement = GlucoseTriggers.Judge(watch, States, new GlucoseTriggerFacts
            {
                Now = Now,
                Zone = _madrid,
                Occasion = occasion,
                Recent = [.. _readings.Where(r => r.At >= Now - GlucoseTriggers.Lookback)],
                Newest = newest ?? _readings.LastOrDefault(),
                Link = Link,
                SensorActivatedAt = SensorActivatedAt
            });
            States = judgement.States;
            LastFires = judgement.Fired;
            _fired.AddRange(judgement.Fired.Select(_ => _readings.Count - 1));
        }

        public void Run(int stepMinutes, string values)
        {
            foreach (var value in values.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                Read(double.Parse(value, CultureInfo.InvariantCulture));
                Clock.Advance(TimeSpan.FromMinutes(stepMinutes));
            }
        }
    }

    private static int[] Indices(string fires) =>
        [.. fires.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(i => int.Parse(i, CultureInfo.InvariantCulture))];

    // Readings a minute apart; the first reading is the trigger's first judgement and never fires.
    [Theory]
    // Crossing below.
    [InlineData("""{"trigger":"glucose","below":70}""", "100 90 69", "2")]
    // Holding below fires once.
    [InlineData("""{"trigger":"glucose","below":70}""", "100 69 65 60 62", "1")]
    // Leaving and crossing again fires again.
    [InlineData("""{"trigger":"glucose","below":70}""", "100 69 75 68", "1 3")]
    // At the level is not below it.
    [InlineData("""{"trigger":"glucose","below":70}""", "100 70 70", "")]
    // Above, crossed twice.
    [InlineData("""{"trigger":"glucose","above":180}""", "150 181 200 179 185", "1 4")]
    // Below for three minutes: entered at 1, held at 4.
    [InlineData("""{"trigger":"glucose","below":70,"for":{"minutes":3}}""", "100 69 68 67 66 65", "4")]
    // A `for` interrupted starts again from the next crossing.
    [InlineData("""{"trigger":"glucose","below":70,"for":{"minutes":3}}""", "100 69 68 71 69 68 67 66", "7")]
    // Left before its `for` ran out: nothing.
    [InlineData("""{"trigger":"glucose","below":70,"for":{"minutes":3}}""", "100 69 68 71 90", "")]
    public void Level_EveryMinute_FiresOnTheCrossing(string trigger, string values, string fires)
    {
        var series = new Series(Watch(trigger));

        series.Run(1, values);

        series.Fired.ShouldBe(Indices(fires));
    }

    // Readings five minutes apart, so a trend's fifteen minutes hold four of them and a forecast's twenty
    // five; fewer than three (four) judge nothing.
    [Theory]
    // -0.6, -1.4, -2.0 (fires), -2.0 (holds), -1.4 (left), ..., -1.4, -2.0 (fires again).
    [InlineData("""{"trigger":"glucose_trend","falling":1.5}""", "100 100 100 100 90 80 70 60 60 60 60 50 40 30", "6 13")]
    [InlineData("""{"trigger":"glucose_trend","rising":1.5}""", "100 100 100 100 110 120 130 140", "6")]
    // Falling steadily enough to be under 70 in twenty minutes at 7, still so at 8 and 9, not at 10;
    // again at 13.
    [InlineData("""{"trigger":"glucose_forecast","below":70,"within":{"minutes":20}}""",
        "120 120 120 120 120 110 100 90 90 90 90 90 80 70", "7 13")]
    [InlineData("""{"trigger":"glucose_forecast","above":200,"within":{"minutes":20}}""",
        "150 150 150 150 150 160 170 180", "7")]
    public void RateAndForecast_EveryFiveMinutes_FireOnTheCrossing(string trigger, string values, string fires)
    {
        var series = new Series(Watch(trigger));

        series.Run(5, values);

        series.Fired.ShouldBe(Indices(fires));
    }

    [Fact]
    public void ATrend_WithTooFewReadings_IsNotJudged()
    {
        var series = new Series(Watch("""{"trigger":"glucose_trend","falling":1}"""));

        series.Run(5, "100 80");

        series.States.ShouldBeEmpty("two readings in fifteen minutes say no rate; nothing is recorded as not holding");
    }

    // Written while she is already low: recorded as holding and fired, so it fires at the next crossing
    // and not now.
    [Fact]
    public void AWatchWrittenWhileItHolds_FiresOnlyAtTheNextCrossing()
    {
        var series = new Series(Watch("""{"trigger":"glucose","below":70}"""));

        series.Run(1, "60 62 65");
        series.Fired.ShouldBeEmpty();
        series.States.ShouldBe([new GlucoseTriggerCondition(0, true, _start, true)]);

        series.Run(1, "80 66");
        series.Fired.ShouldBe([4]);
    }

    // A trend that could not be judged when the watch was written is first judged later, and that
    // judgement still does not fire.
    [Fact]
    public void ATriggersFirstJudgement_NeverFires_EvenWhenItsSiblingsAreJudgedAlready()
    {
        var series = new Series(Watch("""{"trigger":"glucose","below":70},{"trigger":"glucose_trend","falling":1}"""));

        series.Run(5, "100 90 80 70");

        series.Fired.ShouldBeEmpty("the trend's first judgement, at 80, found it falling and fired nothing");
        series.States.Select(s => (s.TriggerIndex, s.Holding, s.FiredThisEpisode))
            .ShouldBe([(0, false, false), (1, true, true)]);
    }

    // Only a newest reading under ten minutes old is judged; an older one leaves every reading trigger
    // as it was.
    [Fact]
    public void AStaleNewestReading_ChangesNothing()
    {
        var series = new Series(Watch("""{"trigger":"glucose","below":70}"""));
        series.Run(1, "100");
        var before = series.States;

        series.Clock.Advance(TimeSpan.FromMinutes(10));
        series.Judge(GlucoseOccasion.Poll, new SensorReading(series.Now - TimeSpan.FromMinutes(10), 50, GlucoseTrend.Unknown));

        series.States.ShouldBe(before);
        series.Fired.ShouldBeEmpty();
    }

    // The clock's pass judges the sensor alone; a reading trigger waits for a poll.
    [Fact]
    public void TheClock_LeavesReadingTriggersAlone()
    {
        var series = new Series(Watch("""{"trigger":"glucose","below":70}"""));
        series.Run(1, "100");
        var before = series.States;

        series.Judge(GlucoseOccasion.Clock, new SensorReading(series.Now, 50, GlucoseTrend.Unknown));

        series.States.ShouldBe(before);
    }

    // A low that started before the window opens fires when the window opens, if it still holds; one
    // inside the window fires at once. Madrid is UTC+1 in November.
    [Fact]
    public void ATimeCondition_GatesTheFire_NotTheEpisode()
    {
        var evening = new DateTimeOffset(2026, 11, 18, 20, 45, 0, TimeSpan.Zero); // 21:45 at home
        var series = new Series(
            Watch("""{"trigger":"glucose","below":70}""", ""","conditions":[{"condition":"time","after":"22:00","before":"07:00"}]"""),
            evening);

        series.Run(5, "100 65 64");
        series.Fired.ShouldBeEmpty("21:55 is outside the window");
        series.States.ShouldBe([new GlucoseTriggerCondition(0, true, evening + TimeSpan.FromMinutes(5), false)]);

        series.Run(5, "63");
        series.Fired.ShouldBe([3], "22:00: inside the window, the episode still holding, not fired yet");

        series.Run(5, "62");
        series.Fired.ShouldBe([3]);
    }

    [Fact]
    public void ATimeCondition_AcrossMidnight_HoldsAfterMidnight()
    {
        var night = new DateTimeOffset(2026, 11, 19, 2, 0, 0, TimeSpan.Zero); // 03:00 at home, a Thursday
        var series = new Series(
            Watch("""{"trigger":"glucose","below":70}""",
                ""","conditions":[{"condition":"time","after":"22:00","before":"07:00","weekday":["thu"]}]"""),
            night);

        series.Run(1, "100 65");

        series.Fired.ShouldBe([1]);
    }

    [Fact]
    public void AWeekdayCondition_OnAnotherDay_HoldsTheFireBack()
    {
        var series = new Series(
            Watch("""{"trigger":"glucose","below":70}""", ""","conditions":[{"condition":"time","weekday":["mon"]}]"""));

        series.Run(1, "100 65 64");

        series.Fired.ShouldBeEmpty("the 18th of November 2026 is a Wednesday");
        series.States.Single().Holding.ShouldBeTrue();
    }

    // "No readings" is LibreLinkUp answering while the sensor says nothing new; a link that gets no
    // answer is "link failing" and never "no readings".
    [Fact]
    public void NoReadings_FiresWhenLibreLinkUpAnswersWithNothingNew_AndNeverOnALinkFailure()
    {
        var series = new Series(Watch("""{"trigger":"sensor","state":"no_readings","for":{"minutes":20}}"""));
        series.Run(1, "100");
        var last = series.Now - TimeSpan.FromMinutes(1);
        series.States.ShouldBe([new GlucoseTriggerCondition(0, false, null, false)]);

        // Answered at +15 min: fifteen minutes without a reading.
        series.Clock.Advance(TimeSpan.FromMinutes(14));
        series.Link = series.Link! with { LastSuccessAt = series.Now };
        series.Judge(GlucoseOccasion.Clock);
        series.LastFires.ShouldBeEmpty();

        // Unreachable since +16 min: whatever the clock, nothing is known about her readings.
        series.Clock.Advance(TimeSpan.FromMinutes(1));
        series.Link = series.Link with { FailingSince = series.Now };
        series.Clock.Advance(TimeSpan.FromMinutes(30));
        series.Judge(GlucoseOccasion.Clock);
        series.LastFires.ShouldBeEmpty("a link failure is not her sensor going silent");
        series.States.Single().Holding.ShouldBeFalse();

        // Answered again, the newest reading still the one from before: silent for 46 minutes.
        series.Link = series.Link with { LastSuccessAt = series.Now, FailingSince = null };
        series.Judge(GlucoseOccasion.Poll);
        series.LastFires.ShouldHaveSingleItem().Since.ShouldBe(last);

        series.Clock.Advance(TimeSpan.FromMinutes(5));
        series.Link = series.Link with { LastSuccessAt = series.Now };
        series.Judge(GlucoseOccasion.Clock);
        series.LastFires.ShouldBeEmpty("still silent: the same episode");

        series.Read(110);
        series.States.Single().Holding.ShouldBeFalse("a reading ends it");
    }

    [Fact]
    public void NoReadings_IsJudgedOnLibreLinkUpsLastAnswer_NotOnTheClock()
    {
        var series = new Series(Watch("""{"trigger":"sensor","state":"no_readings","for":{"minutes":20}}"""));
        series.Run(1, "100");

        // Nobody asked LibreLinkUp for an hour (the server was down): nothing says readings stopped.
        series.Clock.Advance(TimeSpan.FromHours(1));
        series.Judge(GlucoseOccasion.Clock);

        series.LastFires.ShouldBeEmpty();
    }

    [Fact]
    public void LinkFailing_FiresAfterItsFor_FromTheFirstFailure_AndAgainAfterARecovery()
    {
        var series = new Series(Watch("""{"trigger":"sensor","state":"link_failing","for":{"minutes":30}}"""));
        series.Run(1, "100");

        var failingSince = series.Now;
        series.Link = series.Link! with { FailingSince = failingSince };
        series.Clock.Advance(TimeSpan.FromMinutes(29));
        series.Judge(GlucoseOccasion.Clock);
        series.LastFires.ShouldBeEmpty();

        series.Clock.Advance(TimeSpan.FromMinutes(1));
        series.Judge(GlucoseOccasion.Clock);
        var fire = series.LastFires.ShouldHaveSingleItem();
        fire.Since.ShouldBe(failingSince);
        fire.At.ShouldBe(series.Now);

        series.Clock.Advance(TimeSpan.FromMinutes(10));
        series.Judge(GlucoseOccasion.Clock);
        series.LastFires.ShouldBeEmpty("the same outage");

        series.Read(100);
        series.States.Single().Holding.ShouldBeFalse();

        series.Link = series.Link with { FailingSince = series.Now };
        series.Clock.Advance(TimeSpan.FromMinutes(31));
        series.Judge(GlucoseOccasion.Clock);
        series.LastFires.ShouldHaveSingleItem();
    }

    [Fact]
    public void LinkFailing_WithNoLink_IsNotJudged()
    {
        var series = new Series(Watch("""{"trigger":"sensor","state":"link_failing","for":{"minutes":30}}"""));

        series.Judge(GlucoseOccasion.Clock);

        series.States.ShouldBeEmpty();
    }

    [Fact]
    public void Ending_FiresWhenTheSensorsEndIsWithinItsSpan_AndAgainForTheNextSensor()
    {
        var series = new Series(Watch("""{"trigger":"sensor","state":"ending","within":{"hours":24}}"""));
        series.SensorActivatedAt = _start - TimeSpan.FromDays(14) + TimeSpan.FromHours(30);
        series.Judge(GlucoseOccasion.Clock);
        series.States.ShouldBe([new GlucoseTriggerCondition(0, false, null, false)]);

        series.Clock.Advance(TimeSpan.FromHours(6));
        series.Judge(GlucoseOccasion.Clock);
        var fire = series.LastFires.ShouldHaveSingleItem();
        fire.SensorEndsAt.ShouldBe(_start + TimeSpan.FromHours(30));

        series.Clock.Advance(TimeSpan.FromHours(1));
        series.Judge(GlucoseOccasion.Clock);
        series.LastFires.ShouldBeEmpty();

        series.SensorActivatedAt = series.Now;
        series.Judge(GlucoseOccasion.Clock);
        series.States.Single().Holding.ShouldBeFalse("a new sensor has fourteen days");

        series.Clock.Advance(TimeSpan.FromDays(13) + TimeSpan.FromHours(1));
        series.Judge(GlucoseOccasion.Clock);
        series.LastFires.ShouldHaveSingleItem();
    }

    // The report says what fired, at what value, heading where and how fast.
    [Fact]
    public void AFire_ReportsTheTriggerTheReadingTheRateAndTheForecast()
    {
        var series = new Series(Watch("""{"trigger":"glucose_forecast","below":70,"within":{"minutes":20}}"""));
        series.Run(5, "120 120 120 120 120 110 100");

        series.Read(90, GlucoseTrend.Falling);

        var fire = series.LastFires.ShouldHaveSingleItem();
        fire.TriggerIndex.ShouldBe(0);
        fire.Trigger.ShouldBe(new GlucoseForecastTrigger(GlucoseBound.Below, 70, TimeSpan.FromMinutes(20)));
        fire.Newest.ShouldBe(new SensorReading(series.Now, 90, GlucoseTrend.Falling));
        fire.At.ShouldBe(series.Now);
        fire.MgPerDlPerMinute.ShouldNotBeNull().ShouldBe(-1.6, 1e-9);
        fire.ForecastMgPerDl.ShouldNotBeNull().ShouldBe(60, 1e-9);
    }

    // The states answered are exactly one per trigger judged, in order, with nothing left from a list
    // the watch no longer has.
    [Fact]
    public void States_AreOnePerTrigger_InOrder()
    {
        var judgement = GlucoseTriggers.Judge(
            Watch("""{"trigger":"glucose","above":250},{"trigger":"glucose","below":70}"""),
            [new GlucoseTriggerCondition(5, true, _start, true)],
            new GlucoseTriggerFacts
            {
                Now = _start,
                Zone = _madrid,
                Occasion = GlucoseOccasion.Poll,
                Recent = [new SensorReading(_start, 100, GlucoseTrend.Steady)],
                Newest = new SensorReading(_start, 100, GlucoseTrend.Steady)
            });

        judgement.States.Select(s => s.TriggerIndex).ShouldBe([0, 1]);
    }
}