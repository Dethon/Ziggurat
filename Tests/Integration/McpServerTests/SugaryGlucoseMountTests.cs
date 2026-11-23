using Domain.DTOs.Sugary;
using Domain.Tools.Sugary;
using McpServerSugary.Store;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// Her glucose as the poller keeps it, read over MCP the way the agent's tools read it: the store's
// months, its rows and her link's range, through the server's own SQLite and DuckDB.
public class SugaryGlucoseMountTests
{
    // Midnight of 1 October in Madrid.
    private static readonly DateTimeOffset _since = DateTimeOffset.Parse("2026-09-30T22:00:00Z");

    private static GlucoseReading Reading(
        int minutes, double mgDl, GlucoseSource source, GlucoseTrend trend = GlucoseTrend.Unknown) => new()
        {
            At = _since + TimeSpan.FromMinutes(minutes),
            MgPerDl = mgDl,
            Trend = trend,
            Source = source
        };

    private static async Task<SugaryServer> SeededAsync()
    {
        var server = await SugaryServer.StartAsync();
        await server.SaveAsync(
        [
            Reading(0, 50, GlucoseSource.Current, GlucoseTrend.FallingFast),
            Reading(1, 60, GlucoseSource.Current),
            .. Enumerable.Range(2, 8).Select(minute => Reading(minute, 100, GlucoseSource.Current)),
            Reading(10, 200, GlucoseSource.Graph),
            Reading(15, 260, GlucoseSource.Graph),
            Reading(20, 170, GlucoseSource.Graph),
            Reading(60, 120, GlucoseSource.ImportHourly),
            // 23:30 UTC on 31 August is September in Madrid; July's last minute is still July.
            new GlucoseReading { At = DateTimeOffset.Parse("2026-08-31T23:30:00Z"), MgPerDl = 110, Source = GlucoseSource.Graph },
            new GlucoseReading { At = DateTimeOffset.Parse("2026-07-31T21:59:00Z"), MgPerDl = 110, Source = GlucoseSource.Graph }
        ]);
        await server.SaveAsync(new GlucoseSensor
        {
            Serial = "SN-1",
            ActivatedAt = DateTimeOffset.Parse("2026-09-25T08:00:00Z"),
            ProductType = 4,
            LastSeenAt = _since
        });
        await server.SaveAsync(new GlucoseLink
        {
            Email = "ana@example.com",
            PatientId = "patient-1",
            Unit = GlucoseUnit.Mmol,
            TargetLow = 80,
            TargetHigh = 160,
            LinkedAt = _since
        });
        return server;
    }

    [Fact]
    public async Task EachMonthWithAReading_HasAGlucoseFile_OnTheHomesCalendar()
    {
        await using var server = await SeededAsync();

        (await server.GlobAsync("glucose/*")).ShouldBe(
            ["/glucose/2026-07.parquet", "/glucose/2026-09.parquet", "/glucose/2026-10.parquet"]);
        (await server.TableRowsAsync("/glucose/2026-09.parquet")).ShouldBe([["2026-08-31 23:30:00+00", "2026-09-01 01:30:00", "110.0", "6.1", "unknown", "graph", "110.0"]]);
        (await server.TableRowsAsync("/sensors.parquet")).ShouldBe([
            ["SN-1", "2026-09-25 08:00:00+00", "2026-09-25 10:00:00", "2026-10-09 08:00:00+00", "2026-10-09 10:00:00",
                "2026-09-30 22:00:00+00", "2026-10-01 00:00:00"]
        ]);
    }

    [Fact]
    public async Task TheReadmesStatistics_RunOverTheStore()
    {
        await using var server = await SeededAsync();

        var rows = await server.QueryAsync(GlucoseQueries.Statistics(
            "TIMESTAMPTZ '2026-10-01 00:00:00'", "TIMESTAMPTZ '2026-10-01 02:00:00'", 80, 160));

        var row = rows.ShouldHaveSingleItem()!.AsArray();
        row.Select(cell => cell!.GetValue<double>()).ShouldBe([1.7, 3.3, 80.0, 46.7, 16.7, 8.3, 150.2, 6.9, 31.8, 50.0]);
    }

    [Fact]
    public async Task TheReadme_SaysHerUnitAndRange_AndNoLongerSendsGlucoseToHomeAssistant()
    {
        await using var server = await SeededAsync();

        var readme = await server.ReadAsync("/README.md");

        readme.ShouldContain("They read glucose in mmol/L, and their target range is 80–160 mg/dL (4.4–8.9 mmol/L).");
        readme.ShouldContain(GlucoseQueries.Statistics(GlucoseQueries.LastFourteenDays, GlucoseQueries.Now, 80, 160));
        readme.ShouldNotContain("Home Assistant mount");
    }
}