using System.Text;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Sugary;
using Domain.Prompts;
using Domain.Tools.Sugary.Vfs;
using Infrastructure.Agents.Mcp;
using Infrastructure.Utils;
using McpServerSugary.Store;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// Her log as another agent sees it: the /sugary mount, driven over MCP the way the agent's own
// filesystem tools drive it.
public class McpSugaryServerTests
{
    [Fact]
    public async Task TheMount_IsPublishedAtSugary()
    {
        await using var server = await SugaryServer.StartAsync();

        var content = await server.Client.ReadResourceAsync("filesystem://sugary");
        var text = string.Join("", content.Contents.OfType<TextResourceContents>().Select(c => c.Text));

        text.ShouldContain("\"name\":\"sugary\"");
        text.ShouldContain("\"mountPoint\":\"/sugary\"");
    }

    // The words the sugary agent estimates by are this server's, read at the agent's warmup like any
    // server's prompt; the dose rule travels with them because it governs every turn.
    [Fact]
    public async Task TheEstimatingPrompt_IsServed_WithTheRuleThatNoDoseIsEverSuggested()
    {
        await using var server = await SugaryServer.StartAsync();

        var prompt = await server.Client.GetPromptAsync(SugaryPrompt.Name);
        var text = string.Join("", prompt.Messages.Select(m => m.Content).OfType<TextContentBlock>().Select(c => c.Text));

        text.ShouldBe(SugaryPrompt.Prompt);
        text.ShouldContain("Never suggest insulin doses, ratios, corrections or timing.");
    }

    // What the server explains on demand: her tables, where their columns are explained, and that
    // they are asked rather than read.
    [Fact]
    public async Task TheLogSkill_IsServed_PointingAtTheReadmeAndTheQueryTool()
    {
        await using var server = await SugaryServer.StartAsync();

        var skill = await server.Client.ReadResourceAsync(SkillServerResources.BodyAddress(SugarySkill.Name));
        var body = string.Join("", skill.Contents.OfType<TextResourceContents>().Select(c => c.Text));

        body.ShouldContain("/sugary/README.md");
        body.ShouldContain("`domain__filesystem__query`");
        body.ShouldContain("- `foods` —");
        body.ShouldNotContain(".csv");
        body.ShouldNotContain("text_search");
    }

    // "How much did she take for the pizza" is one join, and reading her doses never turns into
    // advising one.
    [Fact]
    public async Task TheLogSkill_SaysWhereHerDosesAre_AndThatTheyAreNeverABasisForAdvice()
    {
        await using var server = await SugaryServer.StartAsync();

        var skill = await server.Client.ReadResourceAsync(SkillServerResources.BodyAddress(SugarySkill.Name));
        var body = string.Join("", skill.Contents.OfType<TextResourceContents>().Select(c => c.Text));

        body.ShouldContain("- `doses` —");
        body.ShouldContain("`doses` joined to `dose_meals` on `dose_id`, then to `meals` on `meal_id`");
        body.ShouldContain("so a dose for two meals counts once");
        body.ShouldContain("never a basis for advice");
        body.ShouldContain("not her ratio applied to its carbs");
        SugarySkill.Description.ShouldContain("insulin");
    }

    // "Did she walk after the paella" is a query beside /ha, and her exercise never turns into advice
    // about carbs or a dose around it.
    [Fact]
    public async Task TheLogSkill_SaysWhereHerExerciseIs_AndThatItIsNeverABasisForAdvice()
    {
        await using var server = await SugaryServer.StartAsync();

        var skill = await server.Client.ReadResourceAsync(SkillServerResources.BodyAddress(SugarySkill.Name));
        var body = string.Join("", skill.Contents.OfType<TextResourceContents>().Select(c => c.Text));

        body.ShouldContain("- `exercise` —");
        body.ShouldContain("empty while it is running");
        body.ShouldContain("no carbs or dose around exercise");
        SugarySkill.Description.ShouldContain("exercise");
    }

    // "Was she on her period the week her glucose ran high" is a query beside /ha.
    [Fact]
    public async Task TheLogSkill_SaysWhereHerCycleIs_AndThatItsDaysLineUpWithHa()
    {
        await using var server = await SugaryServer.StartAsync();

        var skill = await server.Client.ReadResourceAsync(SkillServerResources.BodyAddress(SugarySkill.Name));
        var body = string.Join("", skill.Contents.OfType<TextResourceContents>().Select(c => c.Text));

        body.ShouldContain("- `periods` —");
        body.ShouldContain("`day_notes`");
        body.ShouldContain("read `/ha` history for those days");
        SugarySkill.Description.ShouldContain("period");
    }

    [Fact]
    public async Task AnEmptyLog_HasItsReadmeItsThreeTablesAndThreeEmptyDirectories()
    {
        await using var server = await SugaryServer.StartAsync();

        (await server.GlobAsync("**")).ShouldBe([
            "/README.md", "/day-notes.parquet", "/dose-meals.parquet", "/doses/", "/exercise/", "/foods.parquet",
            "/items/", "/meals/", "/periods.parquet"
        ]);
        (await server.TableRowsAsync("/dose-meals.parquet")).ShouldBeEmpty();
        (await server.TableRowsAsync("/foods.parquet")).ShouldBeEmpty();
        (await server.TableRowsAsync("/periods.parquet")).ShouldBeEmpty();
        (await server.TableRowsAsync("/day-notes.parquet")).ShouldBeEmpty();
    }

    // A reader that takes carbs for total carbohydrate, or the first estimate for the whole meal,
    // draws the wrong line between a meal and the glucose after it.
    [Fact]
    public async Task TheReadme_ExplainsEveryColumnOfEveryTable()
    {
        await using var server = await SugaryServer.StartAsync();

        var readme = await server.ReadAsync("/README.md");

        string[] columns =
        [
            "meal_id", "eaten_at", "carbs_g", "carbs_low_g", "carbs_high_g", "first_estimate_g",
            "first_estimate_low_g", "first_estimate_high_g", "outcome", "items", "notes", "position", "name",
            "portion", "amount", "carbs_per_100", "source", "confidence", "edited", "food_id", "unit",
            "portion_amount", "portion_carbs_g", "barcode", "dose_id", "taken_at", "kind", "units", "period_id",
            "start_date", "end_date", "days", "cycle_days", "date", "note", "exercise_id", "started_at",
            "ended_at", "minutes"
        ];
        columns.Where(column => !readme.Contains($"`{column}`")).ShouldBeEmpty();
        readme.ShouldContain("fibre excluded");
        readme.ShouldContain("only the items whose `source` is `estimate`");
        readme.ShouldContain("Europe/Madrid");
    }

    // Basal units read as a meal dose, or a dose stamped with its meal's time read as measured, put
    // the wrong insulin beside a glucose curve; and the table is a record, not advice.
    [Fact]
    public async Task TheReadme_ExplainsTheDoses()
    {
        await using var server = await SugaryServer.StartAsync();

        var readme = await server.ReadAsync("/README.md");

        readme.ShouldContain("## doses/<YYYY-MM>.parquet");
        readme.ShouldContain("insulin units");
        readme.ShouldContain("`rapid` — rapid-acting");
        readme.ShouldContain("`basal` — long-acting");
        readme.ShouldContain("## dose-meals.parquet");
        readme.ShouldContain("A dose for no meal has no row");
        readme.ShouldContain("total a day's insulin from `doses` alone");
        readme.ShouldContain("carries the meal's time unless she changed it");
        readme.ShouldContain("never a recommendation");
    }

    // An ongoing period read as one day long, or a forecast read as a day she recorded, puts the
    // wrong week beside a glucose curve.
    [Fact]
    public async Task TheReadme_ExplainsHerCycle()
    {
        await using var server = await SugaryServer.StartAsync();

        var readme = await server.ReadAsync("/README.md");

        readme.ShouldContain("## periods.parquet");
        readme.ShouldContain("## day-notes.parquet");
        readme.ShouldContain("days on the home's calendar");
        readme.ShouldContain("empty while it is still going");
        readme.ShouldContain("forecasts are not in these tables");
    }

    [Fact]
    public async Task Periods_AreOneRowEach_OldestFirst_WithTheirLengthAndTheirCycle()
    {
        await using var server = await SugaryServer.StartAsync();
        var september = Period("2026-09-09", "2026-09-14");
        var october = Period("2026-10-08", null);
        var august = Period("2026-08-11", "2026-08-15");
        await server.SaveAsync(september, october, august);

        (await server.TableRowsAsync("/periods.parquet")).ShouldBe([
            [august.Id.ToString(), "2026-08-11", "2026-08-15", "5", "29"],
            [september.Id.ToString(), "2026-09-09", "2026-09-14", "6", "29"],
            [october.Id.ToString(), "2026-10-08", null, null, null]
        ]);
    }

    // A table holds her words as she wrote them: the apostrophe that keeps a spreadsheet from running
    // a formula belongs to the CSV export, not to the data.
    [Fact]
    public async Task DayNotes_AreOneRowEach_OldestFirst_AsSheWroteThem()
    {
        await using var server = await SugaryServer.StartAsync();
        await server.SaveAsync(
            new DayNote { Day = new DateOnly(2026, 10, 6), Text = "craving chocolate, all afternoon" },
            new DayNote { Day = new DateOnly(2026, 10, 2), Text = "=cramps" });

        (await server.TableRowsAsync("/day-notes.parquet")).ShouldBe([
            ["2026-10-02", "=cramps"],
            ["2026-10-06", "craving chocolate, all afternoon"]
        ]);
    }

    // "What did she write the week of her period" is one query over the notes' days.
    [Fact]
    public async Task DayNotes_AreFoundByQuery()
    {
        await using var server = await SugaryServer.StartAsync();
        await server.SaveAsync(
            new DayNote { Day = new DateOnly(2026, 10, 6), Text = "craving chocolate" },
            new DayNote { Day = new DateOnly(2026, 9, 1), Text = "fine" });

        var rows = await server.QueryAsync("select note from day_notes where date between '2026-10-01' and '2026-10-31'");

        rows.Select(row => row![0]!.GetValue<string>()).ShouldBe(["craving chocolate"]);
    }

    // The only way to change her log is through Sugary itself, so the mount offers nothing that
    // writes, moves or deletes; it is read as Parquet and asked with query.
    [Fact]
    public async Task TheMount_OffersReadingAndNothingThatWrites()
    {
        await using var server = await SugaryServer.StartAsync();

        var tools = await server.Client.ListToolsAsync();

        tools.Select(tool => tool.Name).Where(name => name.StartsWith("fs_", StringComparison.Ordinal))
            .ShouldBe(["fs_read", "fs_info", "fs_glob", "fs_query", "fs_blob_read"], ignoreOrder: true);
    }

    // The server holds the row cap to its bounds itself, whoever is calling.
    [Fact]
    public async Task AQueryAskingForNoRows_StillGetsOne()
    {
        await using var server = await SugaryServer.StartAsync();
        await server.SaveAsync(Meal("2026-10-11T12:00:00Z", null, Item("toast", 30)));

        var answer = await server.CallAsync("fs_query", new Dictionary<string, object?>
        {
            ["path"] = "/",
            ["sql"] = "select name from items",
            ["maxRows"] = 0
        });

        answer["rows"]!.AsArray().Count.ShouldBe(1);
        answer["truncated"]!.GetValue<bool>().ShouldBeFalse();
    }

    // The agent's own proxy for a remote mount, asking the real server: what Jonas's query tool
    // reaches when it asks /sugary.
    [Fact]
    public async Task AQuery_ReachesTheServerThroughTheAgentsProxy()
    {
        await using var server = await SugaryServer.StartAsync("Europe/Madrid");
        var paella = Meal("2026-08-09T12:30:00Z", null, Item("paella", 70));
        await server.SaveAsync(paella, Meal("2026-07-12T12:30:00Z", null, Item("paella", 80)));
        await server.SaveAsync(Dose("2026-08-09T12:25:00Z", 7, DoseKind.Rapid, paella.Id));
        var backend = new McpFileSystemBackend(server.Client, SugaryFileSystem.Name, advertisedOperations: null);

        var answer = (await backend.QueryAsync("", """
            select m.eaten_at, d.units from meals m join dose_meals using (meal_id) join doses d using (dose_id)
            """, 10, CancellationToken.None)).ShouldBeOfType<FsResult<FsQueryResult>.Ok>().Value;

        answer.RowCount.ShouldBe(1);
        answer.Rows[0][0]!.GetValue<string>().ShouldBe("2026-08-09T14:30:00+02:00");
        answer.Rows[0][1]!.GetValue<double>().ShouldBe(7);
    }

    private static MealItem Item(string name, double carbs, ItemSource source = ItemSource.Estimate, int position = 0) =>
        new(name, "a cup", 150, 28, carbs - 10, carbs, carbs + 10.5, source,
            source == ItemSource.Estimate ? ItemConfidence.Medium : null)
        {
            Position = position
        };

    private static Meal Meal(string eatenAt, string? notes, params MealItem[] items) => new()
    {
        Id = Guid.NewGuid(),
        EatenAt = DateTimeOffset.Parse(eatenAt),
        Notes = notes,
        Outcome = MealOutcome.High,
        FirstEstimate = new Interval(30, 40, 55),
        Items = items.Select((item, i) => item with { Position = i }).ToList(),
        CarbsLow = items.Sum(i => i.CarbsLow),
        Carbs = items.Sum(i => i.Carbs),
        CarbsHigh = items.Sum(i => i.CarbsHigh)
    };

    private static Period Period(string start, string? end) => new()
    {
        Id = Guid.NewGuid(),
        Start = DateOnly.Parse(start, System.Globalization.CultureInfo.InvariantCulture),
        End = end is null ? null : DateOnly.Parse(end, System.Globalization.CultureInfo.InvariantCulture)
    };

    private static ExerciseSession Session(
        string startedAt, string? endedAt, ExerciseKind kind, string? name = null, string? note = null) => new()
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Name = name,
            StartedAt = DateTimeOffset.Parse(startedAt),
            EndedAt = endedAt is null ? null : DateTimeOffset.Parse(endedAt),
            Note = note
        };

    private static Dose Dose(string takenAt, double units, DoseKind kind, Guid? mealId = null, string? note = null) =>
        Dose(takenAt, units, kind, mealId is { } meal ? [meal] : [], note);

    private static Dose Dose(string takenAt, double units, DoseKind kind, List<Guid> mealIds, string? note = null) => new()
    {
        Id = Guid.NewGuid(),
        TakenAt = DateTimeOffset.Parse(takenAt),
        Units = units,
        Kind = kind,
        MealIds = mealIds,
        Note = note
    };

    // One file per month on the home's calendar, each meal in it with its time on the home's clock.
    [Fact]
    public async Task Meals_AreOneRowEachInAFilePerMonthInTheHomesClock()
    {
        await using var server = await SugaryServer.StartAsync("Europe/Madrid");
        var lunch = Meal("2026-10-11T11:30:00Z", "ate out, \"menú\"", Item("bread", 25, ItemSource.Label), Item("rice", 42));
        var breakfast = Meal("2026-03-01T07:00:00Z", null, Item("toast", 30));
        await server.SaveAsync(lunch, breakfast);

        (await server.GlobAsync("meals/*")).ShouldBe(["/meals/2026-03.parquet", "/meals/2026-10.parquet"]);
        (await server.TableRowsAsync("/meals/2026-03.parquet")).ShouldBe([
            [breakfast.Id.ToString(), "2026-03-01 07:00:00+00", "2026-03-01 08:00:00", "30.0", "20.0", "40.5", "40.0",
                "30.0", "55.0", "high", "toast", null]
        ]);
        (await server.TableRowsAsync("/meals/2026-10.parquet")).ShouldBe([
            [lunch.Id.ToString(), "2026-10-11 11:30:00+00", "2026-10-11 13:30:00", "67.0", "47.0", "88.0", "40.0",
                "30.0", "55.0", "high", "bread; rice", "ate out, \"menú\""]
        ]);
    }

    [Fact]
    public async Task Items_AreOneRowEachInTheOrderOfTheMeal()
    {
        await using var server = await SugaryServer.StartAsync("Europe/Madrid");
        var lunch = Meal("2026-10-11T11:30:00Z", null, Item("bread", 25, ItemSource.Label), Item("rice", 42));
        await server.SaveAsync(lunch);

        (await server.TableRowsAsync("/items/2026-10.parquet")).ShouldBe([
            [lunch.Id.ToString(), "2026-10-11 11:30:00+00", "2026-10-11 13:30:00", "1", "bread", "a cup", "150.0",
                "28.0", "25.0", "15.0", "35.5", "label", null, "false", null],
            [lunch.Id.ToString(), "2026-10-11 11:30:00+00", "2026-10-11 13:30:00", "2", "rice", "a cup", "150.0",
                "28.0", "42.0", "32.0", "52.5", "estimate", "medium", "false", null]
        ]);
    }

    [Fact]
    public async Task AMonthWithNoMeals_HasNoFile()
    {
        await using var server = await SugaryServer.StartAsync();
        await server.SaveAsync(
            Meal("2026-03-10T12:00:00Z", null, Item("toast", 30)),
            Meal("2026-05-10T12:00:00Z", null, Item("toast", 30)));

        (await server.GlobAsync("**/*.parquet")).ShouldBe([
            "/day-notes.parquet", "/dose-meals.parquet", "/foods.parquet", "/items/2026-03.parquet", "/items/2026-05.parquet",
            "/meals/2026-03.parquet", "/meals/2026-05.parquet", "/periods.parquet"
        ]);
        var read = await server.CallAsync("fs_read", new Dictionary<string, object?> { ["path"] = "/meals/2026-04.parquet" });
        read["errorCode"]!.GetValue<string>().ShouldBe("not_found");
        var info = await server.CallAsync("fs_info", new Dictionary<string, object?> { ["path"] = "/items/2026-04.parquet" });
        info["exists"]!.GetValue<bool>().ShouldBeFalse();
    }

    // The calendar's first and last months name a file like any other, and no meal is in them.
    [Theory]
    [InlineData("/meals/9999-12.parquet")]
    [InlineData("/items/9999-12.parquet")]
    [InlineData("/meals/0001-01.parquet")]
    [InlineData("/items/0001-01.parquet")]
    public async Task TheCalendarsEdgeMonths_HaveNoFile(string path)
    {
        await using var server = await SugaryServer.StartAsync();
        await server.SaveAsync(Meal("2026-03-10T12:00:00Z", null, Item("toast", 30)));

        var read = await server.CallAsync("fs_read", new Dictionary<string, object?> { ["path"] = path });
        read["errorCode"]!.GetValue<string>().ShouldBe("not_found");
        var info = await server.CallAsync("fs_info", new Dictionary<string, object?> { ["path"] = path });
        info["exists"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public async Task AMeal_IsFiledUnderTheMonthOnTheHomesCalendar()
    {
        await using var server = await SugaryServer.StartAsync("Europe/Madrid");
        await server.SaveAsync(Meal("2026-03-31T22:30:00Z", null, Item("cena", 30)));

        (await server.GlobAsync("meals/*")).ShouldBe(["/meals/2026-04.parquet"]);
        (await server.TableRowsAsync("/meals/2026-04.parquet")).Single()[2].ShouldBe("2026-04-01 00:30:00");
    }

    [Fact]
    public async Task EveryTime_CarriesTheOffsetOfTheZoneTheServerWasGiven()
    {
        await using var server = await SugaryServer.StartAsync("UTC");
        await server.SaveAsync(Meal("2026-10-11T11:30:00Z", null, Item("rice", 42)));

        var row = (await server.QueryAsync("select m.eaten_at, i.eaten_at from meals m join items i using (meal_id)"))
            .Single()!.AsArray();
        row[0]!.GetValue<string>().ShouldBe("2026-10-11T11:30:00+00:00");
        row[1]!.GetValue<string>().ShouldBe("2026-10-11T11:30:00+00:00");
    }

    [Fact]
    public async Task Foods_AreOneRowEach()
    {
        await using var server = await SugaryServer.StartAsync();
        var food = new Food
        {
            Id = Guid.NewGuid(),
            Name = "Pan de molde",
            CarbsPer100g = 46,
            Unit = "g",
            PortionGrams = 32,
            Barcode = "8480000823298",
            Source = ItemSource.Label,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await server.SaveAsync(food);

        (await server.TableRowsAsync("/foods.parquet")).ShouldBe([
            [food.Id.ToString(), "Pan de molde", "46.0", "g", "32.0", null, "8480000823298", "label"]
        ]);
    }

    // What any reader of Parquet opens without being told anything, and a size a reader can seek by:
    // the footer it looks for is at the end the size names.
    [Fact]
    public async Task ATable_IsParquet_AndItsSizeIsItsBytes()
    {
        await using var server = await SugaryServer.StartAsync();
        await server.SaveAsync(Meal("2026-10-11T12:00:00Z", "menú del día", Item("ñoquis", 40)));

        var chunk = await server.CallAsync("fs_blob_read", new Dictionary<string, object?> { ["path"] = "/meals/2026-10.parquet" });
        var info = await server.CallAsync("fs_info", new Dictionary<string, object?> { ["path"] = "/meals/2026-10.parquet" });
        var bytes = Convert.FromBase64String(chunk["contentBase64"]!.GetValue<string>());

        Encoding.ASCII.GetString(bytes[..4]).ShouldBe("PAR1");
        Encoding.ASCII.GetString(bytes[^4..]).ShouldBe("PAR1");
        info["size"]!.GetValue<long>().ShouldBe(bytes.Length);
        (await server.TableRowsAsync("/meals/2026-10.parquet")).Single()[11].ShouldBe("menú del día");
    }

    // A table is asked, not paged through: reading one as text names the two ways that work.
    [Fact]
    public async Task ReadingATableAsText_NamesQueryAndTheSandbox()
    {
        await using var server = await SugaryServer.StartAsync();
        await server.SaveAsync(Meal("2026-10-11T12:00:00Z", null, Item("toast", 30)));

        var read = await server.CallAsync("fs_read", new Dictionary<string, object?> { ["path"] = "/meals/2026-10.parquet" });

        read["errorCode"]!.GetValue<string>().ShouldBe("unsupported_operation");
        read["hint"]!.GetValue<string>().ShouldContain("query");
    }

    // The database is the only store: a meal saved a moment ago is in the very next read, and no
    // file was written anywhere to carry it.
    [Fact]
    public async Task AChangeInTheDatabase_ShowsOnTheNextRead_WithNothingWrittenToDisk()
    {
        await using var server = await SugaryServer.StartAsync();
        await server.SaveAsync(Meal("2026-10-01T12:00:00Z", null, Item("toast", 30)));
        (await server.TableRowsAsync("/items/2026-10.parquet")).ShouldNotContain(row => row[4] == "paella");

        await server.SaveAsync(Meal("2026-10-11T12:00:00Z", null, Item("paella", 70)));

        (await server.TableRowsAsync("/items/2026-10.parquet")).ShouldContain(row => row[4] == "paella");
        (await server.QueryAsync("select count(*) from items where name = 'paella'")).Single()![0]!.GetValue<int>()
            .ShouldBe(1);
        // The database file and SQLite's write-ahead log beside it, and nothing else — apart from the
        // app's cookie keys, which the host writes on start and no read touches.
        var keys = Path.Combine(server.Directory, "keys");
        Directory.EnumerateFileSystemEntries(server.Directory, "*", SearchOption.AllDirectories)
            .Where(entry => entry != keys && !entry.StartsWith(keys + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ShouldAllBe(name => name == "sugary.db" || name == "sugary.db-wal" || name == "sugary.db-shm");
    }

    // "When did she last eat paella" is one query across every month.
    [Fact]
    public async Task AFood_IsFoundByNameAcrossEveryMonth()
    {
        await using var server = await SugaryServer.StartAsync();
        await server.SaveAsync(
            Meal("2026-09-20T12:00:00Z", null, Item("paella", 70)),
            Meal("2026-10-11T12:00:00Z", null, Item("toast", 30)),
            Meal("2026-10-12T12:00:00Z", null, Item("Paella de marisco", 80)));

        var rows = await server.QueryAsync(
            "select eaten_at_local::date::varchar from items where name ilike '%paella%' order by eaten_at");

        rows.Select(row => row![0]!.GetValue<string>()).ShouldBe(["2026-09-20", "2026-10-12"]);
    }

    // Her doses, one file per month they were taken in on the home's calendar, whether or not she
    // logged a meal that month: a month of basal alone is still a month of insulin.
    [Fact]
    public async Task Doses_AreOneRowEachInAFilePerMonth_EvenInAMonthWithNoMeals()
    {
        await using var server = await SugaryServer.StartAsync("Europe/Madrid");
        var lunch = Meal("2026-10-11T11:30:00Z", null, Item("rice", 42));
        await server.SaveAsync(lunch);
        var bolus = Dose("2026-10-11T11:30:00Z", 4.5, DoseKind.Rapid, lunch.Id, "pre-bolus, late");
        var correction = Dose("2026-10-11T16:00:00Z", 1.25, DoseKind.Rapid);
        // The last night of August in UTC is the first of September at home.
        var basal = Dose("2026-08-31T22:30:00Z", 14, DoseKind.Basal);
        await server.SaveAsync(correction, bolus, basal);

        (await server.GlobAsync("doses/*")).ShouldBe(["/doses/2026-09.parquet", "/doses/2026-10.parquet"]);
        (await server.GlobAsync("meals/*")).ShouldBe(["/meals/2026-10.parquet"]);
        (await server.TableRowsAsync("/doses/2026-09.parquet")).ShouldBe([
            [basal.Id.ToString(), "2026-08-31 22:30:00+00", "2026-09-01 00:30:00", "basal", "14.0", null]
        ]);
        (await server.TableRowsAsync("/doses/2026-10.parquet")).ShouldBe([
            [bolus.Id.ToString(), "2026-10-11 11:30:00+00", "2026-10-11 13:30:00", "rapid", "4.5", "pre-bolus, late"],
            [correction.Id.ToString(), "2026-10-11 16:00:00+00", "2026-10-11 18:00:00", "rapid", "1.25", null]
        ]);
        (await server.TableRowsAsync("/dose-meals.parquet")).ShouldBe([[bolus.Id.ToString(), lunch.Id.ToString()]]);
    }

    [Fact]
    public async Task AMonthWithNoDoses_HasNoDoseFile()
    {
        await using var server = await SugaryServer.StartAsync();
        await server.SaveAsync(Meal("2026-10-11T12:00:00Z", null, Item("toast", 30)));
        await server.SaveAsync(Dose("2026-09-10T21:00:00Z", 14, DoseKind.Basal));

        (await server.GlobAsync("doses/*")).ShouldBe(["/doses/2026-09.parquet"]);
        var read = await server.CallAsync("fs_read", new Dictionary<string, object?> { ["path"] = "/doses/2026-10.parquet" });
        read["errorCode"]!.GetValue<string>().ShouldBe("not_found");
        var info = await server.CallAsync("fs_info", new Dictionary<string, object?> { ["path"] = "/doses/2026-10.parquet" });
        info["exists"]!.GetValue<bool>().ShouldBeFalse();
        var exists = await server.CallAsync("fs_info", new Dictionary<string, object?> { ["path"] = "/doses/2026-09.parquet" });
        exists["exists"]!.GetValue<bool>().ShouldBeTrue();
    }

    // Her exercise, one file per month a session started in on the home's calendar, its minutes
    // worked out from its times and a running session's end and minutes left empty.
    [Fact]
    public async Task Exercise_IsOneRowEachInAFilePerMonth_WithItsMinutes()
    {
        await using var server = await SugaryServer.StartAsync("Europe/Madrid");
        var walk = Session("2026-10-11T08:00:00Z", "2026-10-11T08:44:31Z", ExerciseKind.Walk);
        var ride = Session("2026-10-11T16:00:00Z", "2026-10-11T17:30:00Z", ExerciseKind.Other, "bici", "con Ana, cuesta");
        var gym = Session("2026-10-12T17:00:00Z", null, ExerciseKind.Gym);
        // The last evening of August in UTC is the first of September at home.
        var late = Session("2026-08-31T22:15:00Z", "2026-08-31T22:45:00Z", ExerciseKind.Walk);
        await server.SaveAsync(gym, ride, walk, late);

        (await server.GlobAsync("exercise/*")).ShouldBe(["/exercise/2026-09.parquet", "/exercise/2026-10.parquet"]);
        (await server.TableRowsAsync("/exercise/2026-09.parquet")).ShouldBe([
            [late.Id.ToString(), "2026-08-31 22:15:00+00", "2026-09-01 00:15:00", "2026-08-31 22:45:00+00",
                "2026-09-01 00:45:00", "30", "walk", null, null]
        ]);
        (await server.TableRowsAsync("/exercise/2026-10.parquet")).Select(row => (row[0], row[5], row[6], row[7], row[8]))
            .ShouldBe([
                (walk.Id.ToString(), "45", "walk", null, null),
                (ride.Id.ToString(), "90", "other", "bici", "con Ana, cuesta"),
                (gym.Id.ToString(), null, "gym", null, null)
            ]);
    }

    // "When did she last ride her bike" is one query across the months of exercise.
    [Fact]
    public async Task HerSessions_AreFoundByName()
    {
        await using var server = await SugaryServer.StartAsync();
        await server.SaveAsync(
            Session("2026-09-11T08:00:00Z", "2026-09-11T09:00:00Z", ExerciseKind.Other, "bici"),
            Session("2026-10-11T08:00:00Z", "2026-10-11T09:00:00Z", ExerciseKind.Other, "bici"),
            Session("2026-10-12T08:00:00Z", "2026-10-12T09:00:00Z", ExerciseKind.Walk));

        var rows = await server.QueryAsync("select max(started_at_local)::date::varchar from exercise where name = 'bici'");

        rows.Single()![0]!.GetValue<string>().ShouldBe("2026-10-11");
    }

    // A running session read as zero minutes, or the table read as advice, puts the wrong thing beside
    // a glucose curve.
    [Fact]
    public async Task TheReadme_ExplainsTheExercise()
    {
        await using var server = await SugaryServer.StartAsync();

        var readme = await server.ReadAsync("/README.md");

        readme.ShouldContain("## exercise/<YYYY-MM>.parquet");
        readme.ShouldContain("`walk`");
        readme.ShouldContain("`gym`");
        readme.ShouldContain("`other`");
        readme.ShouldContain("empty while it is still running");
        readme.ShouldContain("never a basis for suggesting carbs or insulin");
    }

    // "How much did she take for the pizza" is one join of the meal and its doses.
    [Fact]
    public async Task AMealsDoses_AreFoundByItsMealId()
    {
        await using var server = await SugaryServer.StartAsync();
        var pizza = Meal("2026-10-11T19:00:00Z", null, Item("pizza", 90));
        await server.SaveAsync(pizza);
        await server.SaveAsync(
            Dose("2026-10-11T19:00:00Z", 8, DoseKind.Rapid, pizza.Id),
            Dose("2026-10-11T21:00:00Z", 2, DoseKind.Rapid, pizza.Id, "top-up"),
            Dose("2026-10-11T22:00:00Z", 14, DoseKind.Basal));

        var rows = await server.QueryAsync(
            "select count(*), sum(units) from doses join dose_meals using (dose_id) join meals using (meal_id) "
            + "where meals.items = 'pizza'");

        rows.Single()![0]!.GetValue<int>().ShouldBe(2);
        rows.Single()![1]!.GetValue<double>().ShouldBe(10);
    }

    // One bolus for the pizza and the tiramisu after it is each meal's, and still one dose: a day's
    // units count it once.
    [Fact]
    public async Task ADoseForTwoMeals_IsFoundFromEach_AndCountedOnce()
    {
        await using var server = await SugaryServer.StartAsync();
        var pizza = Meal("2026-10-11T19:00:00Z", null, Item("pizza", 90));
        var tiramisu = Meal("2026-10-11T19:40:00Z", null, Item("tiramisu", 35));
        await server.SaveAsync(pizza, tiramisu);
        await server.SaveAsync(Dose("2026-10-11T19:00:00Z", 11, DoseKind.Rapid, [pizza.Id, tiramisu.Id]));

        var perMeal = await server.QueryAsync(
            "select meals.items, sum(units) from doses join dose_meals using (dose_id) join meals using (meal_id) "
            + "group by meals.items order by meals.items");
        var day = await server.QueryAsync("select sum(units) from doses");

        perMeal.Select(row => $"{row![0]!.GetValue<string>()}:{row[1]!.GetValue<double>()}").ShouldBe(["pizza:11", "tiramisu:11"]);
        day.Single()![0]!.GetValue<double>().ShouldBe(11);
    }

    [Fact]
    public async Task TheTableDirectories_AreDirectories()
    {
        await using var server = await SugaryServer.StartAsync();

        foreach (var path in new[] { "/meals", "/items", "/doses" })
        {
            var info = await server.CallAsync("fs_info", new Dictionary<string, object?> { ["path"] = path });
            info["isDirectory"]!.GetValue<bool>().ShouldBeTrue(path);
        }
    }
}