using System.Text;
using Domain.DTOs.Sugary;
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

    [Fact]
    public async Task AnEmptyLog_HasItsReadmeFoodsAndTwoEmptyDirectories()
    {
        await using var server = await SugaryServer.StartAsync();

        (await server.GlobAsync("**")).ShouldBe(["/README.md", "/foods.csv", "/items/", "/meals/"]);
        (await server.ReadAsync("/foods.csv"))
            .ShouldBe("food_id,name,carbs_per_100,unit,portion_amount,portion_carbs_g,barcode,source\n");
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
            "portion_amount", "portion_carbs_g", "barcode"
        ];
        columns.Where(column => !readme.Contains($"`{column}`")).ShouldBeEmpty();
        readme.ShouldContain("fibre excluded");
        readme.ShouldContain("only the items whose `source` is `estimate`");
        readme.ShouldContain("Europe/Madrid");
    }

    // The only way to change her log is through Sugary itself, so the mount offers nothing that
    // writes, moves or deletes.
    [Fact]
    public async Task TheMount_OffersReadingAndNothingThatWrites()
    {
        await using var server = await SugaryServer.StartAsync();

        var tools = await server.Client.ListToolsAsync();

        tools.Select(tool => tool.Name).Where(name => name.StartsWith("fs_", StringComparison.Ordinal))
            .ShouldBe(["fs_read", "fs_info", "fs_glob", "fs_search"], ignoreOrder: true);
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

    private static string[] Lines(string csv) => csv.TrimEnd('\n').Split('\n');

    // The standalone app's export, carried over line for line: one file per month, each meal in the
    // home's clock with its offset.
    [Fact]
    public async Task Meals_AreOneRowEachInAFilePerMonthInTheHomesClock()
    {
        await using var server = await SugaryServer.StartAsync("Europe/Madrid");
        var lunch = Meal("2026-10-11T11:30:00Z", "ate out, \"menú\"", Item("bread", 25, ItemSource.Label), Item("rice", 42));
        var breakfast = Meal("2026-03-01T07:00:00Z", null, Item("toast", 30));
        await server.SaveAsync(lunch, breakfast);

        (await server.GlobAsync("meals/*")).ShouldBe(["/meals/2026-03.csv", "/meals/2026-10.csv"]);
        Lines(await server.ReadAsync("/meals/2026-03.csv")).ShouldBe([
            "meal_id,eaten_at,carbs_g,carbs_low_g,carbs_high_g,first_estimate_g,first_estimate_low_g,first_estimate_high_g,outcome,items,notes",
            $"{breakfast.Id},2026-03-01T08:00:00+01:00,30,20,40.5,40,30,55,high,toast,"
        ]);
        Lines(await server.ReadAsync("/meals/2026-10.csv")).ShouldBe([
            "meal_id,eaten_at,carbs_g,carbs_low_g,carbs_high_g,first_estimate_g,first_estimate_low_g,first_estimate_high_g,outcome,items,notes",
            $"{lunch.Id},2026-10-11T13:30:00+02:00,67,47,88,40,30,55,high,bread; rice,\"ate out, \"\"menú\"\"\""
        ]);
    }

    [Fact]
    public async Task Items_AreOneRowEachInTheOrderOfTheMeal()
    {
        await using var server = await SugaryServer.StartAsync("Europe/Madrid");
        var lunch = Meal("2026-10-11T11:30:00Z", null, Item("bread", 25, ItemSource.Label), Item("rice", 42));
        await server.SaveAsync(lunch);

        Lines(await server.ReadAsync("/items/2026-10.csv")).ShouldBe([
            "meal_id,eaten_at,position,name,portion,amount,carbs_per_100,carbs_g,carbs_low_g,carbs_high_g,source,confidence,edited,food_id",
            $"{lunch.Id},2026-10-11T13:30:00+02:00,1,bread,a cup,150,28,25,15,35.5,label,,false,",
            $"{lunch.Id},2026-10-11T13:30:00+02:00,2,rice,a cup,150,28,42,32,52.5,estimate,medium,false,"
        ]);
    }

    [Fact]
    public async Task AMonthWithNoMeals_HasNoFile()
    {
        await using var server = await SugaryServer.StartAsync();
        await server.SaveAsync(
            Meal("2026-03-10T12:00:00Z", null, Item("toast", 30)),
            Meal("2026-05-10T12:00:00Z", null, Item("toast", 30)));

        (await server.GlobAsync("**/*.csv")).ShouldBe([
            "/foods.csv", "/items/2026-03.csv", "/items/2026-05.csv", "/meals/2026-03.csv", "/meals/2026-05.csv"
        ]);
        var read = await server.CallAsync("fs_read", new Dictionary<string, object?> { ["path"] = "/meals/2026-04.csv" });
        read["errorCode"]!.GetValue<string>().ShouldBe("not_found");
        var info = await server.CallAsync("fs_info", new Dictionary<string, object?> { ["path"] = "/items/2026-04.csv" });
        info["exists"]!.GetValue<bool>().ShouldBeFalse();
    }

    // The month a meal is filed under is the one its eaten_at shows, so a late dinner on the last
    // night of March in Madrid is in April's file once the clocks have gone forward.
    [Fact]
    public async Task AMeal_IsFiledUnderTheMonthOnTheHomesCalendar()
    {
        await using var server = await SugaryServer.StartAsync("Europe/Madrid");
        await server.SaveAsync(Meal("2026-03-31T22:30:00Z", null, Item("cena", 30)));

        (await server.GlobAsync("meals/*")).ShouldBe(["/meals/2026-04.csv"]);
        (await server.ReadAsync("/meals/2026-04.csv")).ShouldContain(",2026-04-01T00:30:00+02:00,");
    }

    [Fact]
    public async Task EveryTime_CarriesTheOffsetOfTheZoneTheServerWasGiven()
    {
        await using var server = await SugaryServer.StartAsync("UTC");
        await server.SaveAsync(Meal("2026-10-11T11:30:00Z", null, Item("rice", 42)));

        (await server.ReadAsync("/meals/2026-10.csv")).ShouldContain(",2026-10-11T11:30:00+00:00,");
        (await server.ReadAsync("/items/2026-10.csv")).ShouldContain(",2026-10-11T11:30:00+00:00,");
    }

    [Fact]
    public async Task TextThatASpreadsheetWouldRunAsAFormula_IsWrittenAsText()
    {
        await using var server = await SugaryServer.StartAsync();
        await server.SaveAsync(Meal("2026-10-11T12:00:00Z", "=HYPERLINK(\"http://x\")", Item("+danger", 10)));

        Lines(await server.ReadAsync("/meals/2026-10.csv"))[1].ShouldEndWith(",'+danger,\"'=HYPERLINK(\"\"http://x\"\")\"");
        Lines(await server.ReadAsync("/items/2026-10.csv"))[1].Split(',')[3].ShouldBe("'+danger");
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

        Lines(await server.ReadAsync("/foods.csv")).ShouldBe([
            "food_id,name,carbs_per_100,unit,portion_amount,portion_carbs_g,barcode,source",
            $"{food.Id},Pan de molde,46,g,32,,8480000823298,label"
        ]);
    }

    // What a spreadsheet or pandas opens without being told anything: no byte-order mark in front
    // of the header, and the size the mount reports is the UTF-8 bytes of exactly that text.
    [Fact]
    public async Task ATable_IsUtf8WithoutAByteOrderMark()
    {
        await using var server = await SugaryServer.StartAsync();
        await server.SaveAsync(Meal("2026-10-11T12:00:00Z", "menú del día", Item("ñoquis", 40)));

        var text = await server.ReadAsync("/meals/2026-10.csv");
        var info = await server.CallAsync("fs_info", new Dictionary<string, object?> { ["path"] = "/meals/2026-10.csv" });

        text.ShouldStartWith("meal_id,");
        info["size"]!.GetValue<long>().ShouldBe(new UTF8Encoding(false).GetByteCount(text));
    }

    // The database is the only store: a meal saved a moment ago is in the very next read, and no
    // file was written anywhere to carry it.
    [Fact]
    public async Task AChangeInTheDatabase_ShowsOnTheNextRead_WithNothingWrittenToDisk()
    {
        await using var server = await SugaryServer.StartAsync();
        await server.SaveAsync(Meal("2026-10-01T12:00:00Z", null, Item("toast", 30)));
        (await server.ReadAsync("/meals/2026-10.csv")).ShouldNotContain("paella");

        await server.SaveAsync(Meal("2026-10-11T12:00:00Z", null, Item("paella", 70)));

        (await server.ReadAsync("/meals/2026-10.csv")).ShouldContain("paella");
        (await server.ReadAsync("/items/2026-10.csv")).ShouldContain("paella");
        // The database file and SQLite's write-ahead log beside it, and nothing else.
        Directory.EnumerateFileSystemEntries(server.Directory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .ShouldAllBe(name => name == "sugary.db" || name == "sugary.db-wal" || name == "sugary.db-shm");
    }

    // "When did she last eat paella" is one search.
    [Fact]
    public async Task AFood_IsFoundByNameAcrossEveryMonth()
    {
        await using var server = await SugaryServer.StartAsync();
        await server.SaveAsync(
            Meal("2026-09-20T12:00:00Z", null, Item("paella", 70)),
            Meal("2026-10-11T12:00:00Z", null, Item("toast", 30)),
            Meal("2026-10-12T12:00:00Z", null, Item("Paella de marisco", 80)));

        var everywhere = await server.CallAsync("fs_search", new Dictionary<string, object?>
        {
            ["query"] = "paella",
            ["outputMode"] = "filesOnly"
        });
        var inItems = await server.CallAsync("fs_search", new Dictionary<string, object?>
        {
            ["query"] = "paella",
            ["directoryPath"] = "/items"
        });

        Files(everywhere).ShouldBe(
            ["/items/2026-09.csv", "/items/2026-10.csv", "/meals/2026-09.csv", "/meals/2026-10.csv"], ignoreOrder: true);
        Files(inItems).ShouldBe(["/items/2026-09.csv", "/items/2026-10.csv"], ignoreOrder: true);
        inItems["totalMatches"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public async Task TheTwoTableDirectories_AreDirectories()
    {
        await using var server = await SugaryServer.StartAsync();

        foreach (var path in new[] { "/meals", "/items" })
        {
            var info = await server.CallAsync("fs_info", new Dictionary<string, object?> { ["path"] = path });
            info["isDirectory"]!.GetValue<bool>().ShouldBeTrue(path);
        }
    }

    private static IReadOnlyList<string> Files(System.Text.Json.Nodes.JsonNode search) =>
        search["results"]!.AsArray().Select(result => result!["file"]!.GetValue<string>()).ToList();
}