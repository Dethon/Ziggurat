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

// Where a reading came from: the connection's current reading (about one a minute while her phone
// is in reach), a point of the curve LibreLinkUp keeps (about twelve hours, every five minutes), a
// value imported from Home Assistant's history, or an hourly mean of its long-term statistics — a
// lower resolution the README names.
public enum GlucoseSource
{
    Current,
    Graph,
    Import,
    ImportHourly
}

// How she reads glucose; every value is kept in mg/dL whatever this says.
public enum GlucoseUnit
{
    Mgdl,
    Mmol
}

// One reading of her log as the mount reads it.
public sealed record LoggedGlucose(DateTimeOffset At, double MgPerDl, GlucoseTrend Trend, GlucoseSource Source);

// A sensor she has worn, as LibreLinkUp listed it.
public sealed record LoggedSensor(string Serial, DateTimeOffset ActivatedAt, DateTimeOffset LastSeenAt);

// Her unit and target range (mg/dL whatever the unit) as her link keeps them.
public sealed record GlucoseTargets(GlucoseUnit Unit, int Low, int High);