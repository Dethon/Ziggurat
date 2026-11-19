using Domain.DTOs.Sugary;
using Domain.Tools.Sugary.Watches;
using Shouldly;

namespace Tests.Unit.Domain.Sugary.Watches;

// What a glucose watch's fire tells the agent beside the prompt it asks: what fired, on what reading, how
// fast and where it is heading, when — on the household's clock — and in the unit the person reads, with
// mg/dL always there since that is what every watch is written in.
public class GlucoseFireReportTests
{
    private static readonly TimeZoneInfo _madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");
    private static readonly DateTimeOffset _at = new(2026, 11, 18, 21, 14, 0, TimeSpan.Zero);

    private static GlucoseWatchSpec Spec(string trigger) => GlucoseWatchSpec.Parse(
        $$"""{"name":"Low at night","triggers":[{{trigger}}],"effects":[{"kind":"prompt","prompt":"Tell her"}]}""");

    [Fact]
    public void ALevelFire_SaysWhatFired_TheReading_AndWhen_OnTheHouseholdsClock_InHerUnit()
    {
        var spec = Spec("""{"trigger":"glucose","below":70,"for":{"minutes":10}}""");
        var fire = new GlucoseTriggerFire(0, spec.Triggers[0], _at, new SensorReading(_at, 65, GlucoseTrend.FallingFast),
            null, null, _at - TimeSpan.FromMinutes(10), null);

        var report = GlucoseFireReport.Of("low-at-night", spec, fire, _madrid, GlucoseUnit.Mmol);

        report.ShouldContain("glucose watch \"Low at night\" (low-at-night)");
        report.ShouldContain("below 70 mg/dL (3.9 mmol/L) for 10 minutes");
        report.ShouldContain("65 mg/dL (3.6 mmol/L), falling fast");
        report.ShouldContain("22:14 on 2026-11-18 (Europe/Madrid, +01:00)");
        report.ShouldContain("since 22:04");
        report.ShouldContain("reads glucose in mmol/L");
        report.ShouldContain("Your reply is what is delivered");
    }

    [Fact]
    public void ATrendFire_SaysItsRate()
    {
        var spec = Spec("""{"trigger":"glucose_trend","falling":2}""");
        var fire = new GlucoseTriggerFire(0, spec.Triggers[0], _at, new SensorReading(_at, 120, GlucoseTrend.Falling),
            -2.46, null, null, null);

        var report = GlucoseFireReport.Of("drop", spec, fire, TimeZoneInfo.Utc, GlucoseUnit.Mgdl);

        report.ShouldContain("falling at least 2 mg/dL a minute");
        report.ShouldContain("changing at -2.5 mg/dL a minute");
        report.ShouldContain("reads glucose in mg/dL");
    }

    [Fact]
    public void AForecastFire_SaysWhereItIsHeading()
    {
        var spec = Spec("""{"trigger":"glucose_forecast","below":70,"within":{"minutes":20}}""");
        var fire = new GlucoseTriggerFire(0, spec.Triggers[0], _at, new SensorReading(_at, 90, GlucoseTrend.Falling),
            -1.5, 60, null, null);

        var report = GlucoseFireReport.Of("heading-low", spec, fire, TimeZoneInfo.Utc, GlucoseUnit.Mgdl);

        report.ShouldContain("forecast to go below 70 mg/dL (3.9 mmol/L) within 20 minutes");
        report.ShouldContain("forecast 60 mg/dL (3.3 mmol/L) in 20 minutes");
    }

    [Fact]
    public void ASensorFire_SaysWhatTheSensorDid()
    {
        var spec = Spec("""{"trigger":"sensor","state":"ending","within":{"hours":24}}""");
        var ends = _at + TimeSpan.FromHours(20);
        var fire = new GlucoseTriggerFire(0, spec.Triggers[0], _at, null, null, null, null, ends);

        var report = GlucoseFireReport.Of("sensor-ending", spec, fire, TimeZoneInfo.Utc, GlucoseUnit.Mgdl);

        report.ShouldContain("the sensor ending within 1 day");
        report.ShouldContain("The sensor ends at 17:14 on 2026-11-19");
    }

    [Fact]
    public void ANoReadingsFire_SaysWhenTheLastReadingWas()
    {
        var spec = Spec("""{"trigger":"sensor","state":"no_readings","for":{"minutes":30}}""");
        var fire = new GlucoseTriggerFire(0, spec.Triggers[0], _at, new SensorReading(_at - TimeSpan.FromMinutes(40), 110, GlucoseTrend.Steady),
            null, null, _at - TimeSpan.FromMinutes(40), null);

        var report = GlucoseFireReport.Of("silent", spec, fire, TimeZoneInfo.Utc, GlucoseUnit.Mgdl);

        report.ShouldContain("no readings for 30 minutes");
        report.ShouldContain("110 mg/dL (6.1 mmol/L), steady, at 20:34");
    }
}