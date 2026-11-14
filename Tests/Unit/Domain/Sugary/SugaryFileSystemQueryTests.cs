using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.Sugary;
using Domain.Tools.FileSystem;
using Domain.Tools.Sugary.Vfs;
using Infrastructure.Agents;
using McpServerSugary.Tables;
using Shouldly;

namespace Tests.Unit.Domain.Sugary;

// Her log asked through the agent's own query tool, mounted where a session mounts it, on the real
// engine: what Jonas gets back is what these assert.
public class SugaryFileSystemQueryTests
{
    private static readonly TimeZoneInfo _madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    private static readonly Guid _paella = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid _laterPaella = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid _toast = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly InMemorySugaryLog _log = new();

    public SugaryFileSystemQueryTests()
    {
        _log.Meals.AddRange(
        [
            Meal(_paella, "2026-07-12T12:30:00Z", 80, MealOutcome.InRange, "paella", "salad"),
            Meal(_toast, "2026-08-01T06:30:00Z", 30, MealOutcome.Low, "toast"),
            Meal(_laterPaella, "2026-08-09T12:30:00Z", 70, MealOutcome.High, "paella")
        ]);
        _log.Doses.Add(new LoggedDose(Guid.NewGuid(), DateTimeOffset.Parse("2026-08-09T12:25:00Z"), 7,
            DoseKind.Rapid, [_laterPaella], null));
    }

    private async Task<JsonNode> Query(
        string sql, int maxRows = VfsQueryTool.DefaultMaxRows, string path = "/sugary", TimeSpan? timeout = null)
    {
        var registry = new VirtualFileSystemRegistry();
        registry.Mount(new FileSystemMount(SugaryFileSystem.Name, "/sugary", "her log"),
            new SugaryFileSystem(_log, _madrid, new DuckDbTableEngine(timeout)).For(InMemorySugaryLog.Her));
        return await new VfsQueryTool(registry).RunAsync(path, sql, maxRows);
    }

    private static JsonArray Rows(JsonNode answer) => answer["rows"]!.AsArray();

    private static void ShouldBeRefused(JsonNode answer, string code)
    {
        answer["ok"]!.GetValue<bool>().ShouldBeFalse();
        answer["errorCode"]!.GetValue<string>().ShouldBe(code);
    }

    [Fact]
    public async Task WhenSheLastAteSomething_IsOneQuery()
    {
        var answer = await Query("select meal_id from items where name = 'paella' order by eaten_at desc limit 1");

        answer["path"]!.GetValue<string>().ShouldBe("/sugary");
        answer["rows"]!.AsArray().Single()![0]!.GetValue<string>().ShouldBe(_laterPaella.ToString());
    }

    [Fact]
    public async Task WhatSheTookForAMeal_IsOneJoinThroughItsLinks()
    {
        var answer = await Query(
            "select m.carbs_g, d.units from meals m join dose_meals using (meal_id) join doses d using (dose_id) "
            + "where m.outcome = 'high'");

        var row = Rows(answer).Single()!.AsArray();
        row[0]!.GetValue<double>().ShouldBe(70);
        row[1]!.GetValue<double>().ShouldBe(7);
    }

    [Fact]
    public async Task AnyPathInsideTheMount_SeesTheWholeLog()
    {
        var answer = await Query("select count(*) as meals from meals", path: "/sugary/meals/2026-08.parquet");

        answer["path"]!.GetValue<string>().ShouldBe("/sugary/meals/2026-08.parquet");
        Rows(answer).Single()![0]!.GetValue<int>().ShouldBe(3);
    }

    // Summer in Madrid is two hours ahead of UTC and winter one: the stamp carries the offset of its
    // own day, and the _local sibling is the wall time a person read off the clock.
    [Fact]
    public async Task ATime_IsOnTheHomeClock_AndItsLocalSiblingIsTheWallTime()
    {
        _log.Meals.Add(Meal(Guid.NewGuid(), "2026-01-10T12:00:00Z", 40, MealOutcome.Unknown, "soup"));

        var answer = await Query("select eaten_at, eaten_at_local from meals order by eaten_at");

        var rows = Rows(answer);
        rows[0]![0]!.GetValue<string>().ShouldBe("2026-01-10T13:00:00+01:00");
        rows[0]![1]!.GetValue<string>().ShouldBe("2026-01-10T13:00:00");
        rows[1]![0]!.GetValue<string>().ShouldBe("2026-07-12T14:30:00+02:00");
        rows[1]![1]!.GetValue<string>().ShouldBe("2026-07-12T14:30:00");
    }

    [Fact]
    public async Task ADayOnTheHomeClock_IsAPlainFilter()
    {
        // 23:30 UTC on the 31st of July is half past one on the 1st of August in Madrid.
        _log.Meals.Add(Meal(Guid.NewGuid(), "2026-07-31T23:30:00Z", 15, MealOutcome.Unknown, "yoghurt"));

        var answer = await Query(
            "select count(*) from meals where eaten_at_local::date = DATE '2026-08-01'");

        Rows(answer).Single()![0]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public async Task Values_AreWrittenAsTheirTypesSay()
    {
        _log.Periods.Add(new LoggedPeriod(Guid.NewGuid(), new DateOnly(2026, 8, 1), null));

        var period = Rows(await Query("select start_date, end_date, days from periods")).Single()!.AsArray();
        var item = Rows(await Query("select edited, position, carbs_g from items where name = 'toast'")).Single()!.AsArray();

        period[0]!.GetValue<string>().ShouldBe("2026-08-01");
        period[1].ShouldBeNull();
        period[2].ShouldBeNull();
        item[0]!.GetValue<bool>().ShouldBeFalse();
        item[1]!.GetValue<int>().ShouldBe(1);
        item[2]!.GetValue<double>().ShouldBe(30);
    }

    [Fact]
    public async Task AnswersPastTheCap_SayHowManyThereWere()
    {
        var answer = await Query("select name from items", maxRows: 2);

        Rows(answer).Count.ShouldBe(2);
        answer["rowCount"]!.GetValue<int>().ShouldBe(4);
        answer["truncated"]!.GetValue<bool>().ShouldBeTrue();
        answer["suggestion"]!.GetValue<string>().ShouldContain("Narrow");
    }

    [Fact]
    public async Task TheColumns_AreNamedWithTheirTypes()
    {
        var answer = await Query("select name, carbs_g, eaten_at from items limit 1");

        answer["columns"]!.AsArray().Select(c => $"{c!["name"]}:{c["type"]}")
            .ShouldBe(["name:VARCHAR", "carbs_g:DOUBLE", "eaten_at:TIMESTAMPTZ"]);
    }

    [Fact]
    public async Task AStatementTheEngineRejects_SaysWhyAndWhereTheColumnsAre()
    {
        var answer = await Query("select calories from meals");

        ShouldBeRefused(answer, "invalid_argument");
        answer["message"]!.GetValue<string>().ShouldContain("calories");
        answer["hint"]!.GetValue<string>().ShouldContain("/sugary/README.md");
        answer["retryable"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task AStatementPastTheTimeBound_IsStopped()
    {
        var answer = await Query("select count(*) from range(10000000000000) a", timeout: TimeSpan.FromMilliseconds(200));

        ShouldBeRefused(answer, "timeout");
    }

    // The statement is the model's, and a prompt injection can write one: the tables are all it sees.
    // Refused by the lockdown itself, never by an accident of the machine it ran on (no network, no
    // such file), which is what the message is checked for.
    [Theory]
    [InlineData("select * from read_csv('/etc/passwd')")]
    [InlineData("select * from read_text('/etc/passwd')")]
    [InlineData("select * from glob('/etc/*')")]
    [InlineData("install httpfs")]
    [InlineData("load httpfs")]
    [InlineData("set enable_external_access = true")]
    [InlineData("set memory_limit = '64GB'")]
    [InlineData("set temp_directory = '/tmp'")]
    [InlineData("copy meals to '/tmp/meals.csv'")]
    public async Task AStatementReachingPastTheTables_IsRefused(string sql)
    {
        var answer = await Query(sql);

        ShouldBeRefused(answer, "invalid_argument");
        answer["message"]!.GetValue<string>().ShouldMatch("(?i)disabled (by|through) configuration|configuration has been locked|cannot change");
    }

    // The store's own database file is the one file that holds everything: as a database, as bytes.
    [Theory]
    [InlineData("attach '{0}' as store (type sqlite)")]
    [InlineData("select * from read_blob('{0}')")]
    public async Task TheStoresDatabaseFile_IsOutOfReach(string statement)
    {
        var store = Path.Combine(Path.GetTempPath(), $"sugary-{Guid.NewGuid():N}.db");
        await File.WriteAllTextAsync(store, "SQLite format 3");
        try
        {
            var answer = await Query(string.Format(System.Globalization.CultureInfo.InvariantCulture, statement, store));

            ShouldBeRefused(answer, "invalid_argument");
            answer["message"]!.GetValue<string>().ShouldMatch("(?i)disabled (by|through) configuration");
        }
        finally
        {
            File.Delete(store);
        }
    }

    // Over its memory a statement fails rather than writing what it holds to the server's disk.
    [Fact]
    public async Task AStatementHasNowhereToSpill()
    {
        var answer = await Query("select count(*) from (select distinct i::varchar from range(40000000) t(i))");

        ShouldBeRefused(answer, "invalid_argument");
        answer["message"]!.GetValue<string>().ShouldMatch("(?i)\\.tmp.*disabled by configuration|out of memory");
    }

    // A value the answer cannot be written in is the statement's problem, said once: not an internal
    // error a model would try again.
    [Fact]
    public async Task AValueTheAnswerCannotHold_IsTheStatementsError()
    {
        var answer = await Query("select 'infinity'::timestamptz as forever");

        answer["retryable"]?.GetValue<bool>().ShouldBeFalse();
        if (answer["ok"]?.GetValue<bool>() == false)
        {
            answer["errorCode"]!.GetValue<string>().ShouldBe("invalid_argument");
        }
    }

    [Fact]
    public async Task AStatementThatWrites_ChangesNothingTheNextOneSees()
    {
        var inside = await Query("delete from meals; select count(*) from meals");
        var after = await Query("select count(*) from meals");

        Rows(inside).Single()![0]!.GetValue<int>().ShouldBe(0);
        Rows(after).Single()![0]!.GetValue<int>().ShouldBe(3);
    }

    // A parent and its workers can ask at once: each statement has a database of its own, so two
    // loading the same tables never meet, and one's write is never the other's answer.
    [Fact]
    public async Task StatementsAskedAtOnce_EachSeeTheirOwnTables()
    {
        var answers = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => i % 2 == 0
            ? Query("delete from meals; select count(*) from meals")
            : Query("select count(*) from meals")));

        answers.Select((answer, i) => Rows(answer).Single()![0]!.GetValue<int>())
            .ShouldBe([0, 3, 0, 3, 0, 3, 0, 3]);
    }

    [Fact]
    public async Task AMealSavedBetweenTwoQueries_IsInTheSecond()
    {
        var before = await Query("select count(*) from meals");
        _log.Meals.Add(Meal(Guid.NewGuid(), "2026-08-10T19:00:00Z", 55, MealOutcome.Unknown, "pasta"));
        var after = await Query("select count(*) from meals");

        Rows(before).Single()![0]!.GetValue<int>().ShouldBe(3);
        Rows(after).Single()![0]!.GetValue<int>().ShouldBe(4);
    }

    private static LoggedMeal Meal(Guid id, string eatenAt, double carbs, MealOutcome outcome, params string[] foods) =>
        new(id, DateTimeOffset.Parse(eatenAt), new CarbRange(carbs * 0.6, carbs, carbs * 1.6), null, outcome, null,
        [
            .. foods.Select((food, position) => new LoggedItem(position, food, null, null, null,
                new CarbRange(carbs * 0.6 / foods.Length, carbs / foods.Length, carbs * 1.6 / foods.Length),
                ItemSource.Estimate, ItemConfidence.Medium, false, null))
        ]);
}