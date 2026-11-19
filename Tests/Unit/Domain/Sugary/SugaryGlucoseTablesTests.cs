using System.Text;
using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Sugary;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Domain.Tools.Sugary;
using Domain.Tools.Sugary.Vfs;
using DuckDB.NET.Data;
using Infrastructure.Agents;
using McpServerSugary.Tables;
using Shouldly;

namespace Tests.Unit.Domain.Sugary;

// Her glucose on /sugary, on the real engine: a file per month, two tables for query, and the README's
// statistics run as written against a log whose figures were worked out by hand. The readings come at
// uneven intervals — live minutes, five-minute curve points, an hourly mean — so a statistic counted
// by rows gives other figures than one weighted by time, and these are the time-weighted ones.
public class SugaryGlucoseTablesTests
{
    private static readonly TimeZoneInfo _madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    // Midnight of 1 October in Madrid, two hours ahead of UTC.
    private static readonly DateTimeOffset _since = DateTimeOffset.Parse("2026-09-30T22:00:00Z");
    private const string Since = "TIMESTAMPTZ '2026-10-01 00:00:00'";
    private const string Until = "TIMESTAMPTZ '2026-10-01 02:00:00'";

    private static readonly Guid _dinner = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly InMemorySugaryLog _log = new();

    public SugaryGlucoseTablesTests()
    {
        _log.Glucose.AddRange(
        [
            Reading(-5, 300, GlucoseSource.Current),
            Reading(0, 50, GlucoseSource.Current, GlucoseTrend.FallingFast),
            Reading(1, 60, GlucoseSource.Current, GlucoseTrend.Rising),
            .. Enumerable.Range(2, 8).Select(minute => Reading(minute, 100, GlucoseSource.Current, GlucoseTrend.Steady)),
            Reading(10, 200, GlucoseSource.Graph),
            Reading(15, 260, GlucoseSource.Graph),
            // Forty minutes with nothing: the curve point stands for twenty of them.
            Reading(20, 170, GlucoseSource.Graph),
            Reading(60, 120, GlucoseSource.ImportHourly),
            Reading(120, 90, GlucoseSource.Current)
        ]);
        _log.Sensors.Add(new LoggedSensor("SN-1", DateTimeOffset.Parse("2026-09-25T08:00:00Z"), _since));
        _log.Targets = new GlucoseTargets(GlucoseUnit.Mmol, 80, 160);
        _log.Meals.Add(new LoggedMeal(_dinner, _since + TimeSpan.FromMinutes(5), new CarbRange(40, 50, 60), null,
            MealOutcome.High, null,
            [new LoggedItem(0, "pizza", null, 200, 25, new CarbRange(40, 50, 60), ItemSource.Estimate, null, false, null)]));
    }

    private static LoggedGlucose Reading(
        int minutes, double mgDl, GlucoseSource source, GlucoseTrend trend = GlucoseTrend.Unknown) =>
        new(_since + TimeSpan.FromMinutes(minutes), mgDl, trend, source);

    private SugaryFileSystem Mount() => (SugaryFileSystem)new SugaryFileSystem(_log, _madrid, new DuckDbTableEngine(), new ReadSnapshots(TimeProvider.System)).For(InMemorySugaryLog.Her);

    private async Task<JsonNode> Query(string sql)
    {
        var registry = new VirtualFileSystemRegistry();
        registry.Mount(new FileSystemMount(SugaryFileSystem.Name, "/sugary", "her log"), Mount());
        return await new VfsQueryTool(registry).RunAsync("/sugary", sql, VfsQueryTool.DefaultMaxRows);
    }

    private static Dictionary<string, JsonNode?> Row(JsonNode answer)
    {
        var names = answer["columns"]!.AsArray().Select(column => column!["name"]!.GetValue<string>()).ToList();
        var row = answer["rows"]!.AsArray().ShouldHaveSingleItem()!.AsArray();
        return names.Zip(row).ToDictionary(pair => pair.First, pair => pair.Second);
    }

    // A reading at 23:30 UTC on the last day of September is October's in Madrid.
    [Fact]
    public async Task EachMonthWithAReading_HasAGlucoseFile_OnTheHomesCalendar()
    {
        _log.Glucose.Add(new LoggedGlucose(DateTimeOffset.Parse("2026-08-31T23:30:00Z"), 110, GlucoseTrend.Unknown, GlucoseSource.Graph));

        var glob = (await Mount().GlobAsync("/glucose", "*.parquet", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;

        glob.Entries.ShouldBe(["/glucose/2026-09.parquet", "/glucose/2026-10.parquet"]);
    }

    [Fact]
    public async Task TheReadmesStatistics_AnswerTheFiguresWorkedOutByHand()
    {
        var answer = await Query(GlucoseQueries.Statistics(Since, Until, 80, 160));

        // Covered: ten live minutes, two curve points of five, one of twenty (its gap is forty) and an
        // hourly mean of twenty (the hour to the period's end) — 60 of the period's 120 minutes. The
        // readings before and after the period are not in it. Counted by rows, 50 would be 1 of 14.
        // Below 54: 1 of 60; below 70: 2; 70–180: 8 + 20 + 20 = 48; her 80–160: 8 + 20 = 28; above
        // 180: 5 + 5 = 10; above 250: 5. Mean (50 + 60 + 800 + 1000 + 1300 + 3400 + 2400) / 60 = 150.17.
        var row = Row(answer);
        row["below_54_pct"]!.GetValue<double>().ShouldBe(1.7);
        row["below_70_pct"]!.GetValue<double>().ShouldBe(3.3);
        row["in_70_180_pct"]!.GetValue<double>().ShouldBe(80.0);
        row["in_target_pct"]!.GetValue<double>().ShouldBe(46.7);
        row["above_180_pct"]!.GetValue<double>().ShouldBe(16.7);
        row["above_250_pct"]!.GetValue<double>().ShouldBe(8.3);
        row["mean_mg_dl"]!.GetValue<double>().ShouldBe(150.2);
        row["gmi_pct"]!.GetValue<double>().ShouldBe(6.9);
        row["cv_pct"]!.GetValue<double>().ShouldBe(31.8);
        row["coverage_pct"]!.GetValue<double>().ShouldBe(50.0);
    }

    [Fact]
    public async Task APeriodWithNoReadings_AnswersNoRow()
    {
        var answer = await Query(GlucoseQueries.Statistics(
            "TIMESTAMPTZ '2026-06-01 00:00:00'", "TIMESTAMPTZ '2026-06-02 00:00:00'", 70, 180));

        answer["rows"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task AMeal_IsJoinedToTheGlucoseOfTheHoursAfterIt()
    {
        var row = Row(await Query(GlucoseQueries.AfterMeals));

        row["meal_id"]!.GetValue<string>().ShouldBe(_dinner.ToString());
        row["first_mg_dl"]!.GetValue<double>().ShouldBe(100);
        row["peak_mg_dl"]!.GetValue<double>().ShouldBe(260);
        row["peak_at"]!.GetValue<string>().ShouldBe("2026-10-01T00:15:00+02:00");
        row["last_mg_dl"]!.GetValue<double>().ShouldBe(90);
    }

    // A year of readings is half a million rows, so a statement about the other tables alone does not load
    // them; any other statement does — one that names glucose, and every way of listing what is there,
    // since a listing that left glucose out would say she has no readings.
    [Theory]
    [InlineData("select count(*) from meals", false, false)]
    [InlineData("select d.units from doses d join dose_meals using (dose_id) join meals using (meal_id)", false, false)]
    [InlineData("select insulin from pens where replaced_on is null", false, false)]
    [InlineData("select p.sex, w.weight_kg from profile p, weights w, heights h", false, false)]
    [InlineData("select count(*) from GLUCOSE", true, false)]
    [InlineData("select count(*) from meals m join glucose g on g.read_at > m.eaten_at", true, false)]
    [InlineData("show tables", true, true)]
    [InlineData("show all tables", true, true)]
    [InlineData("describe", true, true)]
    [InlineData("select table_name from information_schema.tables", true, true)]
    [InlineData("select name from sqlite_master", true, true)]
    [InlineData("select tablename from pg_catalog.pg_tables", true, true)]
    [InlineData("select table_name from duckdb_tables()", true, true)]
    [InlineData("select 42", true, false)]
    public async Task TheReadings_AreLeftOutOnlyOfAStatementThatNamesOnlyOtherTables(string sql, bool loaded, bool lists)
    {
        var answer = await Query(sql);

        answer["ok"]?.GetValue<bool>().ShouldNotBe(false, answer.ToJsonString());
        (_log.GlucoseReads > 0).ShouldBe(loaded);
        if (lists)
        {
            answer["rows"]!.AsArray().SelectMany(row => row!.AsArray()).Select(cell => cell?.ToJsonString() ?? "")
                .ShouldContain(cell => cell.Contains("glucose", StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task TheReadme_GivesTheStatementsTheTestsRun_WithTheirOwnRange()
    {
        var readme = await ReadmeAsync();

        readme.ShouldContain(GlucoseQueries.Statistics(GlucoseQueries.LastFourteenDays, GlucoseQueries.Now, 80, 160));
        readme.ShouldContain(GlucoseQueries.AfterMeals);
        readme.ShouldContain("They read glucose in mmol/L, and their target range is 80–160 mg/dL (4.4–8.9 mmol/L).");
        readme.ShouldNotContain("Home Assistant mount");
    }

    [Fact]
    public async Task TheReadmeOfALogNeverLinked_SaysSo_AndTakesTheConsensusRange()
    {
        _log.Targets = null;

        var readme = await ReadmeAsync();

        readme.ShouldContain("They have not linked LibreLinkUp");
        readme.ShouldContain(GlucoseQueries.Statistics(GlucoseQueries.LastFourteenDays, GlucoseQueries.Now, 70, 180));
    }

    [Fact]
    public async Task TheReadme_ExplainsEveryGlucoseColumn_AndTheCoverageRule()
    {
        var readme = await ReadmeAsync();

        string[] columns =
        [
            "read_at", "mg_dl", "mmol_l", "trend", "source", "serial", "activated_at", "ends_at", "last_seen_at",
            "falling_fast", "rising_fast", "unknown", "current", "graph", "import", "import-hourly"
        ];
        columns.Where(column => !readme.Contains($"`{column}`")).ShouldBeEmpty();
        readme.ShouldContain("## glucose/<YYYY-MM>.parquet");
        readme.ShouldContain("## sensors.parquet");
        readme.ShouldContain("Under 70 %");
        readme.ShouldContain("36 % or less");
        readme.ShouldContain("3.31 + 0.02392");
    }

    // What a command in the sandbox opens through the bridge: the month's readings, mmol/L beside mg/dL
    // and the arrow in words.
    [Fact]
    public async Task AMonthsGlucoseFile_IsParquetAnyReaderOpens()
    {
        var rows = await ReadParquetAsync(await BytesAsync("/glucose/2026-10.parquet"),
            "select read_at_local::varchar, mg_dl, mmol_l, trend, source from t order by read_at limit 3");
        var sensors = await ReadParquetAsync(await BytesAsync("/sensors.parquet"),
            "select serial, activated_at_local::varchar, ends_at_local::varchar from t");

        rows.ShouldBe([
            ["2026-10-01 00:00:00", 50d, 2.8d, "falling_fast", "current"],
            ["2026-10-01 00:01:00", 60d, 3.3d, "rising", "current"],
            ["2026-10-01 00:02:00", 100d, 5.6d, "steady", "current"]
        ]);
        sensors.ShouldBe([["SN-1", "2026-09-25 10:00:00", "2026-10-09 10:00:00"]]);
    }

    private async Task<string> ReadmeAsync() =>
        Encoding.UTF8.GetString(await BytesAsync("/README.md"));

    private async Task<byte[]> BytesAsync(string path)
    {
        using var bytes = new MemoryStream();
        await foreach (var chunk in Mount().ReadChunksAsync(path, CancellationToken.None))
        {
            bytes.Write(chunk.Span);
        }

        return bytes.ToArray();
    }

    private static async Task<List<object?[]>> ReadParquetAsync(byte[] bytes, string select)
    {
        var file = Path.Combine(Path.GetTempPath(), $"sugary-{Guid.NewGuid():N}.parquet");
        await File.WriteAllBytesAsync(file, bytes);
        try
        {
            await using var connection = new DuckDBConnection("DataSource=:memory:");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"create view t as select * from read_parquet('{file}'); {select}";
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<object?[]>();
            while (await reader.ReadAsync())
            {
                rows.Add([.. Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i))]);
            }

            return rows;
        }
        finally
        {
            File.Delete(file);
        }
    }
}