namespace Domain.DTOs.Sugary;

// Which way her glucose was heading when the sensor read it, as the sensor's own arrow says. A
// reading without an arrow (every point of a sensor's history but its newest) is Unknown.
public enum GlucoseTrend
{
    Unknown,
    FallingFast,
    Falling,
    Steady,
    Rising,
    RisingFast
}

// One glucose value as a sensor reported it: the instant it was measured (UTC), in mg/dL whatever
// unit she reads, with its trend.
public sealed record SensorReading(DateTimeOffset At, double MgPerDl, GlucoseTrend Trend);