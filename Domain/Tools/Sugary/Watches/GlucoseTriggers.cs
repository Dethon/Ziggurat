using Domain.DTOs.Sugary;

namespace Domain.Tools.Sugary.Watches;

// What a judgement was asked on: a poll of her link, which brought (or failed to bring) a reading, or the
// clock's pass, which brings none and judges only what time alone changes — the link failing on, the
// sensor's end coming nearer.
public enum GlucoseOccasion
{
    Poll,
    Clock
}

// Her link as the poller last left it: when it was made, when LibreLinkUp last answered it, and since when
// it has not (null while it answers).
public sealed record GlucoseLinkHealth(DateTimeOffset LinkedAt, DateTimeOffset? LastSuccessAt, DateTimeOffset? FailingSince);

// Everything a watch's triggers are judged on. Recent and Newest are her own readings, never imported ones:
// an import is history brought in, not her glucose now.
public sealed record GlucoseTriggerFacts
{
    public required DateTimeOffset Now { get; init; }

    // The household's clock, which a watch's time conditions are read on.
    public required TimeZoneInfo Zone { get; init; }

    public required GlucoseOccasion Occasion { get; init; }

    // Her readings of the last GlucoseTriggers.Lookback, oldest first, for the rate and the forecast.
    public IReadOnlyList<SensorReading> Recent { get; init; } = [];

    // The newest reading she has, however old: on a poll, the one LibreLinkUp just gave.
    public SensorReading? Newest { get; init; }

    // Null without a link.
    public GlucoseLinkHealth? Link { get; init; }

    // When the sensor on her now was started; null when none was ever seen.
    public DateTimeOffset? SensorActivatedAt { get; init; }
}

// One trigger's fire: which, judged at what instant (the reading's for a reading trigger, the clock's for
// the sensor's), on which reading, at what rate and forecast where the trigger computed them, since when its
// condition held, and when the sensor ends for `ending`.
public sealed record GlucoseTriggerFire(
    int TriggerIndex,
    GlucoseTrigger Trigger,
    DateTimeOffset At,
    SensorReading? Newest,
    double? MgPerDlPerMinute,
    double? ForecastMgPerDl,
    DateTimeOffset? Since,
    DateTimeOffset? SensorEndsAt);

// The states to keep, one per trigger judged now or before, and what fired.
public sealed record GlucoseJudgement(IReadOnlyList<GlucoseTriggerCondition> States, IReadOnlyList<GlucoseTriggerFire> Fired);

// A glucose watch's triggers judged on her readings, her link, her sensor and the clock, against what was
// judged of each before. A trigger fires on entering its condition — after holding it `for`, for a
// threshold — and not again until the condition has left. A trigger nothing was judged of before is judged
// without firing, its condition recorded as fired if it already holds: a watch written while she is low
// fires at the next crossing, not at once. A trigger that cannot be judged now (no fresh reading, too few
// for a rate, no link, no sensor) keeps whatever was judged before — and, judged never, no state at all —
// since recording "not holding" for it would make the next judgement look like a crossing. Time
// conditions decide whether a fire may happen now, not whether the condition holds: a low that began
// before its window opened fires when the window opens, if the low has not ended.
public static class GlucoseTriggers
{
    // A reading older than this says nothing about her glucose now.
    public static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan TrendWindow = TimeSpan.FromMinutes(15);
    public const int TrendReadings = 3;

    public static readonly TimeSpan ForecastWindow = TimeSpan.FromMinutes(20);
    public const int ForecastReadings = 4;

    // How far back a caller needs her readings: the longest window, from a newest reading as old as Fresh.
    public static readonly TimeSpan Lookback = ForecastWindow + Fresh;

    // A Libre sensor lasts fourteen days.
    public static readonly TimeSpan SensorLifetime = TimeSpan.FromDays(14);

    public static GlucoseJudgement Judge(
        GlucoseWatchSpec watch, IReadOnlyList<GlucoseTriggerCondition> prior, GlucoseTriggerFacts facts)
    {
        var open = (watch.Conditions ?? []).All(condition => IsOpen(condition, TimeZoneInfo.ConvertTime(facts.Now, facts.Zone)));
        var steps = watch.Triggers
            .Select((trigger, index) => Step(index, trigger, prior.FirstOrDefault(p => p.TriggerIndex == index), facts, open))
            .ToList();
        return new GlucoseJudgement(
            [.. steps.Select(step => step.State).OfType<GlucoseTriggerCondition>()],
            [.. steps.Select(step => step.Fire).OfType<GlucoseTriggerFire>()]);
    }

    private sealed record Verdict(
        bool Holds,
        DateTimeOffset At,
        DateTimeOffset? Since,
        double? Rate = null,
        double? Forecast = null,
        DateTimeOffset? EndsAt = null);

    private static (GlucoseTriggerCondition? State, GlucoseTriggerFire? Fire) Step(
        int index, GlucoseTrigger trigger, GlucoseTriggerCondition? before, GlucoseTriggerFacts facts, bool open)
    {
        if (Of(trigger, facts) is not { } verdict)
        {
            return (before, null);
        }

        if (!verdict.Holds)
        {
            return (new GlucoseTriggerCondition(index, false, null, false), null);
        }

        var since = before is { Holding: true } ? before.HoldingSince : verdict.Since;
        if (before is null)
        {
            return (new GlucoseTriggerCondition(index, true, since, true), null);
        }

        var heldLongEnough = trigger is not GlucoseLevelTrigger { For: { } span } || verdict.At - since >= span;
        if (before is { Holding: true, FiredThisEpisode: true } || !heldLongEnough || !open)
        {
            return (new GlucoseTriggerCondition(index, true, since, before is { Holding: true, FiredThisEpisode: true }), null);
        }

        return (new GlucoseTriggerCondition(index, true, since, true),
            new GlucoseTriggerFire(index, trigger, verdict.At, facts.Newest, verdict.Rate, verdict.Forecast, since, verdict.EndsAt));
    }

    // Whether the trigger's condition holds now, or null when nothing can be said.
    private static Verdict? Of(GlucoseTrigger trigger, GlucoseTriggerFacts facts) => trigger switch
    {
        SensorTrigger sensor => OfSensor(sensor, facts),
        _ when facts.Occasion != GlucoseOccasion.Poll => null,
        _ when facts.Newest is not { } newest || facts.Now - newest.At >= Fresh => null,
        GlucoseLevelTrigger level => new Verdict(Beyond(level.Bound, facts.Newest.MgPerDl, level.MgPerDl), facts.Newest.At, facts.Newest.At),
        GlucoseTrendTrigger trend => Fit(facts, TrendWindow, TrendReadings) is { } fit
            ? new Verdict(
                trend.Direction == GlucoseTrendDirection.Falling
                    ? fit.Slope <= -trend.MgPerDlPerMinute
                    : fit.Slope >= trend.MgPerDlPerMinute,
                facts.Newest.At, facts.Newest.At, fit.Slope)
            : null,
        GlucoseForecastTrigger forecast => Fit(facts, ForecastWindow, ForecastReadings) is { } fit
            ? new Verdict(
                Beyond(forecast.Bound, fit.At(forecast.Within), forecast.MgPerDl),
                facts.Newest.At, facts.Newest.At, fit.Slope, fit.At(forecast.Within))
            : null,
        _ => null
    };

    private static Verdict? OfSensor(SensorTrigger sensor, GlucoseTriggerFacts facts) => sensor.State switch
    {
        // LibreLinkUp answered, and what it had was older than `for` at that answer: judged on the answer,
        // not the clock, so a server that asked nobody for an hour does not call her sensor silent. A link
        // failing says nothing about her readings either way.
        SensorState.NoReadings => facts.Link is { FailingSince: null, LastSuccessAt: { } answered } link
            ? Silent(answered, facts.Newest?.At ?? link.LinkedAt, sensor.For!.Value, facts.Now)
            : null,
        SensorState.LinkFailing => facts.Link is { } failing
            ? new Verdict(failing.FailingSince is { } from && facts.Now - from >= sensor.For!.Value, facts.Now, failing.FailingSince)
            : null,
        _ => facts.SensorActivatedAt is { } activated
            ? Ending(activated + SensorLifetime, sensor.Within!.Value, facts.Now)
            : null
    };

    private static Verdict Silent(DateTimeOffset answered, DateTimeOffset last, TimeSpan span, DateTimeOffset now) =>
        new(answered - last >= span, now, last);

    private static Verdict Ending(DateTimeOffset ends, TimeSpan within, DateTimeOffset now) =>
        new(ends - now <= within, now, ends - within, EndsAt: ends);

    private static bool Beyond(GlucoseBound bound, double value, double level) =>
        bound == GlucoseBound.Below ? value < level : value > level;

    // The least-squares line through her readings of the window ending at the newest, in mg/dL a minute.
    private sealed record Line(double Slope, double AtNewest)
    {
        public double At(TimeSpan ahead) => AtNewest + Slope * ahead.TotalMinutes;
    }

    private static Line? Fit(GlucoseTriggerFacts facts, TimeSpan window, int atLeast)
    {
        var newest = facts.Newest!;
        var points = facts.Recent.Append(newest)
            .DistinctBy(r => r.At)
            .Where(r => r.At >= newest.At - window && r.At <= newest.At)
            .Select(r => (X: (r.At - newest.At).TotalMinutes, Y: r.MgPerDl))
            .ToList();
        if (points.Count < atLeast)
        {
            return null;
        }

        var meanX = points.Average(p => p.X);
        var meanY = points.Average(p => p.Y);
        var spread = points.Sum(p => (p.X - meanX) * (p.X - meanX));
        if (spread <= 0)
        {
            return null;
        }

        var slope = points.Sum(p => (p.X - meanX) * (p.Y - meanY)) / spread;
        return new Line(slope, meanY + slope * (0 - meanX));
    }

    // Home Assistant's time condition: after inclusive, before exclusive, an after later than the before
    // spanning midnight, and the weekday the local day's.
    private static bool IsOpen(GlucoseTimeCondition condition, DateTimeOffset local)
    {
        var time = TimeOnly.FromTimeSpan(local.TimeOfDay);
        var inHours = (condition.After, condition.Before) switch
        {
            ({ } after, { } before) when after <= before => time >= after && time < before,
            ({ } after, { } before) => time >= after || time < before,
            ({ } after, null) => time >= after,
            (null, { } before) => time < before,
            _ => true
        };
        return inHours && (condition.Weekdays is not { } days || days.Contains(local.DayOfWeek));
    }
}