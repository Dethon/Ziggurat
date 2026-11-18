using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Sugary;

namespace McpServerSugary.Glucose;

// A follower account's credentials. Region is where the account was last found (null: not known yet,
// so the global address is asked and redirects).
public sealed record LibreLinkUpAccount(string Email, string Password, string? Region = null);

// A sensor as LibreLinkUp lists it: its serial, when it was started, and its product type (`pt`),
// which is what says how long it lasts.
public sealed record LibreLinkUpSensor(string Serial, DateTimeOffset ActivatedAt, int? ProductType);

// A person the account follows. Current is the newest reading with its arrow, null before a sensor has
// sent one; Sensor is the one on her now, null when none is.
public sealed record LibreLinkUpConnection(
    string PatientId,
    string FirstName,
    string LastName,
    double? TargetLow,
    double? TargetHigh,
    LibreLinkUpSensor? Sensor,
    SensorReading? Current);

// A connection's last twelve hours or so: the connection with its current reading, the points of the
// curve (every five minutes, oldest first, no arrow), and every sensor LibreLinkUp calls active.
public sealed record LibreLinkUpGraph(
    LibreLinkUpConnection Connection,
    IReadOnlyList<SensorReading> Points,
    IReadOnlyList<LibreLinkUpSensor> Sensors);

// LibreLinkUp's JSON, read tolerantly: a field missing or of another type is absent rather than an
// exception, since the service is not ours and changes without notice.
public static class LibreLinkUpWire
{
    private const string StampFormat = "M/d/yyyy h:mm:ss tt";

    public static GlucoseTrend Trend(int? arrow) => arrow switch
    {
        1 => GlucoseTrend.FallingFast,
        2 => GlucoseTrend.Falling,
        3 => GlucoseTrend.Steady,
        4 => GlucoseTrend.Rising,
        5 => GlucoseTrend.RisingFast,
        _ => GlucoseTrend.Unknown
    };

    // FactoryTimestamp is UTC in the US wording whatever the phone's language. The other stamp,
    // Timestamp, is the phone's wall clock with no offset, and is never read.
    public static DateTimeOffset? Instant(string? stamp) =>
        DateTime.TryParseExact(stamp, StampFormat, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? new DateTimeOffset(at, TimeSpan.Zero)
            : null;

    public static SensorReading? Reading(JsonNode? item) =>
        Instant(Text(item?["FactoryTimestamp"])) is { } at && Number(item?["ValueInMgPerDl"]) is { } mgPerDl
            ? new SensorReading(at, mgPerDl, Trend((int?)Number(item?["TrendArrow"])))
            : null;

    public static LibreLinkUpSensor? Sensor(JsonNode? sensor) =>
        Text(sensor?["sn"]) is { } serial && Number(sensor?["a"]) is { } activated
            ? new LibreLinkUpSensor(serial, DateTimeOffset.FromUnixTimeSeconds((long)activated), (int?)Number(sensor?["pt"]))
            : null;

    public static LibreLinkUpConnection? Connection(JsonNode? connection) =>
        Text(connection?["patientId"]) is { } patientId
            ? new LibreLinkUpConnection(
                patientId,
                Text(connection?["firstName"]) ?? "",
                Text(connection?["lastName"]) ?? "",
                Number(connection?["targetLow"]),
                Number(connection?["targetHigh"]),
                Sensor(connection?["sensor"]),
                Reading(connection?["glucoseMeasurement"]))
            : null;

    public static IReadOnlyList<LibreLinkUpConnection>? Connections(JsonNode? data) =>
        data is JsonArray connections ? [.. connections.Select(Connection).OfType<LibreLinkUpConnection>()] : null;

    public static LibreLinkUpGraph? Graph(JsonNode? data) =>
        Connection(data?["connection"]) is { } connection
            ? new LibreLinkUpGraph(
                connection,
                [.. Items(data?["graphData"]).Select(Reading).OfType<SensorReading>().OrderBy(r => r.At)],
                [.. Items(data?["activeSensors"]).Select(active => Sensor(active?["sensor"])).OfType<LibreLinkUpSensor>()])
            : null;

    internal static string? Text(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String && node.GetValue<string>() is { Length: > 0 } text ? text : null;

    internal static double? Number(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.Number => node.GetValue<double>(),
        JsonValueKind.String when double.TryParse(
            node.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
        _ => null
    };

    private static IEnumerable<JsonNode?> Items(JsonNode? node) => node as JsonArray ?? [];
}