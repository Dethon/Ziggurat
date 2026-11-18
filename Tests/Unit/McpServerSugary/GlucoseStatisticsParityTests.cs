using System.Globalization;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Sugary;
using Domain.Tools.Sugary;
using McpServerSugary.Glucose;
using McpServerSugary.Tables;
using Shouldly;

namespace Tests.Unit.McpServerSugary;

// The app shows her statistics from GlucoseStatistics; the agent works them out with the README's
// statement (GlucoseQueries) on the mount's engine. The same readings must give the same figures both
// ways, or Jonas and the Glucose tab tell her two different times in range.
public class GlucoseStatisticsParityTests
{
    private static readonly TimeZoneInfo _madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    // Midnight of 1 October in Madrid, and the two hours after it.
    private static readonly DateTimeOffset _from = DateTimeOffset.Parse("2026-09-30T22:00:00Z", CultureInfo.InvariantCulture);
    private static readonly DateTimeOffset _to = _from + TimeSpan.FromHours(2);
    private const string Since = "TIMESTAMPTZ '2026-10-01 00:00:00'";
    private const string Until = "TIMESTAMPTZ '2026-10-01 02:00:00'";

    // The README's hand-checked log: live minutes, five-minute curve points, a forty-minute gap and an
    // hourly mean, with a reading on each side of the period.
    private static IReadOnlyList<LoggedGlucose> HandChecked() =>
    [
        At(-5, 300, GlucoseSource.Current),
        At(0, 50, GlucoseSource.Current),
        At(1, 60, GlucoseSource.Current),
        .. Enumerable.Range(2, 8).Select(minute => At(minute, 100, GlucoseSource.Current)),
        At(10, 200, GlucoseSource.Graph),
        At(15, 260, GlucoseSource.Graph),
        At(20, 170, GlucoseSource.Graph),
        At(60, 120, GlucoseSource.ImportHourly),
        At(120, 90, GlucoseSource.Current)
    ];

    // Readings at odd seconds and uneven spacing, gaps either side of the cap, values on every band's
    // edge: whatever a real day looks like.
    private static IReadOnlyList<LoggedGlucose> Uneven()
    {
        var random = new Random(7);
        var seconds = 0;
        var readings = new List<LoggedGlucose>();
        while (seconds < 2 * 3600)
        {
            double[] edges = [53, 54, 69, 70, 80, 160, 180, 181, 250, 251];
            var value = random.Next(4) == 0 ? edges[random.Next(edges.Length)] : random.Next(40, 400);
            readings.Add(new LoggedGlucose(_from + TimeSpan.FromSeconds(seconds), value, GlucoseTrend.Unknown,
                random.Next(3) == 0 ? GlucoseSource.Graph : GlucoseSource.Current));
            seconds += random.Next(5) switch
            {
                0 => random.Next(900, 1800),
                1 => 900,
                _ => random.Next(20, 400)
            };
        }

        return readings;
    }

    private static LoggedGlucose At(int minutes, double mgDl, GlucoseSource source) =>
        new(_from + TimeSpan.FromMinutes(minutes), mgDl, GlucoseTrend.Unknown, source);

    public static TheoryData<string> Logs() => new() { "hand-checked", "uneven" };

    [Theory]
    [MemberData(nameof(Logs))]
    public async Task TheAppAndTheReadmesStatement_GiveTheSameFigures(string log)
    {
        var readings = log == "hand-checked" ? HandChecked() : Uneven();

        var app = GlucoseStatistics.Of(
            readings.Select(r => new SensorReading(r.At, r.MgPerDl, r.Trend)), _from, _to, 80, 160);
        var sql = await StatementAsync(readings, GlucoseQueries.Statistics(Since, Until, 80, 160));

        var row = sql.ShouldNotBeNull();
        Same(row["below_54_pct"], app.VeryLow);
        Same(row["below_70_pct"], app.Low);
        Same(row["in_target_pct"], app.InRange);
        Same(row["above_180_pct"], app.High);
        Same(row["above_250_pct"], app.VeryHigh);
        Same(row["coverage_pct"], app.Coverage);
        Same(row["cv_pct"], app.Cv);
        row["mean_mg_dl"].ShouldBe(app.Mean!.Value, 0.05 + 1e-9);
        row["gmi_pct"].ShouldBe(app.Gmi!.Value, 0.05 + 1e-9);
    }

    [Fact]
    public async Task APeriodWithNoReadings_IsNothingToSayBothWays()
    {
        var readings = HandChecked();

        var app = GlucoseStatistics.Of(readings.Select(r => new SensorReading(r.At, r.MgPerDl, r.Trend)),
            _from.AddDays(-30), _to.AddDays(-30), 70, 180);
        var sql = await StatementAsync(readings, GlucoseQueries.Statistics(
            "TIMESTAMPTZ '2026-09-01 00:00:00'", "TIMESTAMPTZ '2026-09-01 02:00:00'", 70, 180));

        app.Mean.ShouldBeNull();
        app.InRange.ShouldBeNull();
        sql.ShouldBeNull();
    }

    // A share the app keeps as a fraction, the statement as a percent to one decimal.
    private static void Same(double sqlPercent, double? appFraction) =>
        sqlPercent.ShouldBe(appFraction.ShouldNotBeNull() * 100, 0.05 + 1e-9);

    private static async Task<Dictionary<string, double>?> StatementAsync(IReadOnlyList<LoggedGlucose> readings, string sql)
    {
        var answer = (await new DuckDbTableEngine().QueryAsync(
                [SugaryTables.Glucose(readings, _madrid)], _madrid, sql, 10, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsQueryResult>.Ok>().Value;
        return answer.Rows.Count == 0
            ? null
            : answer.Columns.Zip(answer.Rows.Single())
                .ToDictionary(pair => pair.First.Name, pair => pair.Second!.GetValue<double>());
    }
}