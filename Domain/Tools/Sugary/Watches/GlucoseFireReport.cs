using System.Globalization;
using Domain.DTOs.Sugary;

namespace Domain.Tools.Sugary.Watches;

// What a glucose watch's fire says beside the prompt it asks, never in it (the delegation signs the
// prompt): which watch, what fired, the reading it fired on and how fast it is moving or where it is
// heading, when — on the household's clock — and the unit the person reads glucose in, every value in
// mg/dL, which every watch is written in, with mmol/L beside it. A prompt written for later ("tell her
// to eat something") otherwise reads as somebody's request to find a way to tell her, so it says too
// that the reply is what is delivered, as a schedule's fire does.
public static class GlucoseFireReport
{
    private const double MgPerDlPerMmol = 18.016;

    public static string Of(string watchId, GlucoseWatchSpec spec, GlucoseTriggerFire fire, TimeZoneInfo zone, GlucoseUnit unit)
    {
        var lines = new List<string>
        {
            $"This turn is the glucose watch \"{spec.Name}\" ({watchId}), fired by {What(fire.Trigger)}."
        };

        if (fire.Newest is { } reading)
        {
            lines.Add($"The latest reading: {Level(reading.MgPerDl)}, {Trend(reading.Trend)}, at {When(reading.At, zone)}.");
        }

        if (fire.MgPerDlPerMinute is { } rate)
        {
            lines.Add($"Glucose is changing at {Number(rate)} mg/dL a minute.");
        }

        if (fire is { ForecastMgPerDl: { } forecast, Trigger: GlucoseForecastTrigger heading })
        {
            lines.Add($"At that pace it is forecast {Level(forecast)} in {Span(heading.Within)}.");
        }

        if (fire.Since is { } since && fire.Trigger is not SensorTrigger { State: SensorState.NoReadings })
        {
            lines.Add($"It has held since {Clock(since, zone)}.");
        }

        if (fire.SensorEndsAt is { } ends)
        {
            lines.Add($"The sensor ends at {When(ends, zone)}.");
        }

        lines.Add($"It fired at {When(fire.At, zone)}; the person reads glucose in {(unit == GlucoseUnit.Mmol ? "mmol/L" : "mg/dL")}.");
        lines.Add("Your reply is what is delivered, so anything the watch asks you to tell or send is your reply itself.");
        return string.Join("\n", lines);
    }

    private static string What(GlucoseTrigger trigger) => trigger switch
    {
        GlucoseLevelTrigger level =>
            $"glucose {Bound(level.Bound)} {Level(level.MgPerDl)}" + (level.For is { } span ? $" for {Span(span)}" : ""),
        GlucoseTrendTrigger trend =>
            $"glucose {(trend.Direction == GlucoseTrendDirection.Falling ? "falling" : "rising")} at least {Number(trend.MgPerDlPerMinute)} mg/dL a minute",
        GlucoseForecastTrigger forecast =>
            $"glucose forecast to go {Bound(forecast.Bound)} {Level(forecast.MgPerDl)} within {Span(forecast.Within)}",
        SensorTrigger { State: SensorState.NoReadings, For: { } span } => $"no readings for {Span(span)}",
        SensorTrigger { State: SensorState.LinkFailing, For: { } span } => $"the link to LibreLinkUp failing for {Span(span)}",
        SensorTrigger { Within: { } span } => $"the sensor ending within {Span(span)}",
        _ => trigger.Kind
    };

    private static string Bound(GlucoseBound bound) => bound == GlucoseBound.Below ? "below" : "above";

    private static string Level(double mgPerDl) =>
        $"{Number(mgPerDl)} mg/dL ({(mgPerDl / MgPerDlPerMmol).ToString("0.0", CultureInfo.InvariantCulture)} mmol/L)";

    private static string Number(double value) =>
        Math.Round(value, 1).ToString("0.#", CultureInfo.InvariantCulture);

    private static string Trend(GlucoseTrend trend) => trend switch
    {
        GlucoseTrend.FallingFast => "falling fast",
        GlucoseTrend.Falling => "falling",
        GlucoseTrend.Steady => "steady",
        GlucoseTrend.Rising => "rising",
        GlucoseTrend.RisingFast => "rising fast",
        _ => "trend unknown"
    };

    private static string When(DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        return $"{local.ToString("HH:mm 'on' yyyy-MM-dd", CultureInfo.InvariantCulture)} ({zone.Id}, "
               + $"{local.ToString("zzz", CultureInfo.InvariantCulture)})";
    }

    private static string Clock(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string Span(TimeSpan span) => span switch
    {
        { TotalDays: >= 1 } when span.TotalDays % 1 == 0 => Count((int)span.TotalDays, "day"),
        { TotalHours: >= 1 } when span.TotalHours % 1 == 0 => Count((int)span.TotalHours, "hour"),
        { TotalMinutes: >= 1 } when span.TotalMinutes % 1 == 0 => Count((int)span.TotalMinutes, "minute"),
        _ => Count((int)span.TotalSeconds, "second")
    };

    private static string Count(int n, string unit) => n == 1 ? $"1 {unit}" : $"{n} {unit}s";
}