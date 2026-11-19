using Domain.DTOs.Sugary;
using Domain.Tools.Sugary;

namespace McpServerSugary.Store;

// One glucose value of her log, at the instant the sensor measured it (UTC, LibreLinkUp's
// FactoryTimestamp), in mg/dL whatever unit she reads. One per person per instant; and a curve point or
// an imported value is not kept within GlucoseReadingRules.Spacing of another of hers: the current
// readings, the curve and the import all see the same values, and a value counted twice would weigh
// twice in every statistic.
public class GlucoseReading : IPersonal
{
    public string UserId { get; set; } = "";
    public string TenantId { get; set; } = "";

    public DateTimeOffset At { get; set; }
    public double MgPerDl { get; set; }

    // The sensor's arrow, which only the connection's current reading carries; Unknown for every other.
    public GlucoseTrend Trend { get; set; }

    public GlucoseSource Source { get; set; }
}

// A sensor she has worn, as LibreLinkUp listed it: kept as seen, so when one started and when it ends
// (its product type says how long it lasts) can be said after it is gone.
public class GlucoseSensor : IPersonal
{
    public string UserId { get; set; } = "";
    public string TenantId { get; set; } = "";

    public string Serial { get; set; } = "";
    public DateTimeOffset ActivatedAt { get; set; }
    public int? ProductType { get; set; }

    // When LibreLinkUp last listed it: the newest is the one on her now.
    public DateTimeOffset LastSeenAt { get; set; }
}

public static class GlucoseReadingRules
{
    // A curve point (or an imported value) this close to a reading she already has is the same moment
    // seen twice — the curve's point of a minute whose current reading she has — and is not kept.
    // Current readings are each the sensor's own minute and are all kept.
    public static readonly TimeSpan Spacing = TimeSpan.FromMinutes(2.5);

    // The column's words, one spelling for the database and the mount's tables (SugaryTables).
    public static string Word(GlucoseSource source) => SugaryTables.SourceWord(source);

    public static GlucoseSource SourceOf(string word) => SugaryTables.SourceOf(word);
}