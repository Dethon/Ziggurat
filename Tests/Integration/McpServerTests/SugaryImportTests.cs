using McpServerSugary.Import;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// Her standalone log carried over: the three tables the standalone app downloads, read once by the
// server's import command and then read back through the mount, which is where they have to look
// the same.
public class SugaryImportTests : IDisposable
{
    private readonly string _exports = Path.Combine(Path.GetTempPath(), "sugary-exports", Guid.NewGuid().ToString("N"));

    private static readonly Guid _toastDinner = Guid.Parse("7d1c2f0e-5b4a-4e1f-9a37-0c7b9f6d2a11");
    private static readonly Guid _lunch = Guid.Parse("c2a9e4b1-8f3d-4c6a-b5e2-91d07a3f4e22");
    private static readonly Guid _toast = Guid.Parse("0f6b3d2c-1a9e-4b7d-8c5f-2e4a6b8d0c33");
    private static readonly Guid _bread = Guid.Parse("a4e8c1d7-6b2f-4a9e-b3c5-7d1f0e2a9b44");

    // Exported with tz=Europe/Madrid, as the app's download asks for. One meal is the last day of
    // September in Madrid, and one carries text a spreadsheet would have run.
    private static readonly string _meals =
        "meal_id,eaten_at,carbs_g,carbs_low_g,carbs_high_g,first_estimate_g,first_estimate_low_g,first_estimate_high_g,outcome,items,notes\n"
        + $"{_toastDinner},2026-09-30T23:15:00+02:00,30,20,40.5,,,,unknown,toast,\n"
        + $"{_lunch},2026-10-11T13:30:00+02:00,67,47,88,40,30,55,inRange,bread; +danger,\"ate out, \"\"menú\"\"\"\n";

    private static readonly string _items =
        "meal_id,eaten_at,position,name,portion,amount,carbs_per_100,carbs_g,carbs_low_g,carbs_high_g,source,confidence,edited,food_id\n"
        + $"{_toastDinner},2026-09-30T23:15:00+02:00,1,toast,,,,30,20,40.5,manual,,false,{_toast}\n"
        + $"{_lunch},2026-10-11T13:30:00+02:00,1,bread,a cup,150,28,25,15,35.5,label,,false,{_bread}\n"
        + $"{_lunch},2026-10-11T13:30:00+02:00,2,'+danger,\"a plate, heaped\",150,28,42,32,52.5,estimate,medium,true,\n";

    private static readonly string _foods =
        "food_id,name,carbs_per_100,unit,portion_amount,portion_carbs_g,barcode,source\n"
        + $"{_toast},Mi tostada,,g,28,15,,manual\n"
        + $"{_bread},Pan de molde,46,g,32,,8480000823298,label\n";

    public SugaryImportTests()
    {
        Directory.CreateDirectory(_exports);
        File.WriteAllText(Path.Combine(_exports, "sugary-meals.csv"), _meals);
        File.WriteAllText(Path.Combine(_exports, "sugary-items.csv"), _items);
        File.WriteAllText(Path.Combine(_exports, "sugary-foods.csv"), _foods);
    }

    public void Dispose() => Directory.Delete(_exports, recursive: true);

    // The import, then what the migration into the home household does with what it brought: gives
    // it to her, so the suite reads it back as hers.
    private async Task<(int Exit, string Output)> ImportAsync(SugaryServer server)
    {
        var imported = await ImportOnlyAsync(server);
        await server.GiveUnassignedRowsToAsync(SugaryServer.Her);
        return imported;
    }

    private async Task<(int Exit, string Output)> ImportOnlyAsync(SugaryServer server)
    {
        await using var output = new StringWriter();
        var exit = await SugaryCommand.RunAsync(["import", _exports], server.Settings, output, CancellationToken.None);
        return (exit, output.ToString());
    }

    // The downloads name no person, so what they bring is nobody's until it is given to someone:
    // not her log yet, and not anyone else's either.
    [Fact]
    public async Task AnImportedLog_IsNobodysUntilItIsGiven()
    {
        await using var server = await SugaryServer.StartAsync("Europe/Madrid");

        var (exit, _) = await ImportOnlyAsync(server);

        exit.ShouldBe(0);
        Body(await server.ExportAsync("meals")).ShouldBeEmpty();
        Body(await server.ExportAsync("foods")).ShouldBeEmpty();
        (await server.QueryAsync("select count(*) from meals")).Single()![0]!.GetValue<int>().ShouldBe(0);
        await server.GiveUnassignedRowsToAsync(SugaryServer.Her);
        (await server.ExportAsync("meals")).ShouldBe(_meals);
    }

    // Identifiers, times, numbers, sources and the defused text all come back exactly as she
    // exported them, because the export renders the same columns in the same zone.
    [Fact]
    public async Task AnImportedLog_ReadsBackAsItWasExported()
    {
        await using var server = await SugaryServer.StartAsync("Europe/Madrid");

        var (exit, _) = await ImportAsync(server);

        exit.ShouldBe(0);
        (await server.GlobAsync("**/*.parquet")).ShouldBe([
            "/day-notes.parquet", "/dose-meals.parquet", "/foods.parquet", "/items/2026-09.parquet", "/items/2026-10.parquet",
            "/meals/2026-09.parquet", "/meals/2026-10.parquet", "/periods.parquet"
        ]);
        (await server.ExportAsync("meals")).ShouldBe(_meals);
        (await server.ExportAsync("items")).ShouldBe(_items);
        (await server.ExportAsync("foods")).ShouldBe(_foods);
    }

    [Fact]
    public async Task ImportingTwice_DuplicatesNothing()
    {
        await using var server = await SugaryServer.StartAsync("Europe/Madrid");

        var first = await ImportAsync(server);
        var second = await ImportAsync(server);

        first.Output.ShouldContain("imported 2 meals (3 items) and 2 foods");
        second.Exit.ShouldBe(0);
        second.Output.ShouldContain("imported 0 meals (0 items) and 0 foods");
        second.Output.ShouldContain("2 meals and 2 foods were already there");
        Body(await server.ExportAsync("meals")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(2);
        Body(await server.ExportAsync("items")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(3);
        Body(await server.ExportAsync("foods")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(2);
    }

    // A table that cannot be read whole is not read at all: half a log is worse than none, because
    // nothing would say which half is missing.
    [Fact]
    public async Task AnUnreadableTable_ImportsNothingAndSaysWhere()
    {
        await using var server = await SugaryServer.StartAsync("Europe/Madrid");
        File.WriteAllText(Path.Combine(_exports, "sugary-items.csv"),
            _items.Replace(",estimate,medium,", ",guess,medium,", StringComparison.Ordinal));

        var (exit, output) = await ImportAsync(server);

        exit.ShouldNotBe(0);
        output.ShouldContain("sugary-items.csv");
        output.ShouldContain("line 4");
        (await server.GlobAsync("meals/*")).ShouldBeEmpty();
        Body(await server.ExportAsync("foods")).ShouldBeEmpty();
    }

    // A meal's total is what its items add up to, as the app computes it on every save: a row that
    // says otherwise is not believed, since the items are what she sees and edits.
    [Fact]
    public async Task AnImportedMealsTotal_IsTheSumOfItsItems()
    {
        await using var server = await SugaryServer.StartAsync("Europe/Madrid");
        File.WriteAllText(Path.Combine(_exports, "sugary-meals.csv"),
            _meals.Replace("2026-10-11T13:30:00+02:00,67,47,88,", "2026-10-11T13:30:00+02:00,99,1,500,",
                StringComparison.Ordinal));

        var (exit, _) = await ImportAsync(server);

        exit.ShouldBe(0);
        (await server.ExportAsync("meals")).ShouldContain($"{_lunch},2026-10-11T13:30:00+02:00,67,47,88,");
    }

    // Numbers a spreadsheet can write but no meal has: each stops the import naming where it is.
    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-5")]
    public async Task ANumberNoMealHas_ImportsNothingAndSaysWhere(string number)
    {
        await using var server = await SugaryServer.StartAsync("Europe/Madrid");
        File.WriteAllText(Path.Combine(_exports, "sugary-items.csv"),
            _items.Replace("150,28,42,32,52.5,", $"150,{number},42,32,52.5,", StringComparison.Ordinal));

        var (exit, output) = await ImportAsync(server);

        exit.ShouldNotBe(0);
        output.ShouldContain("sugary-items.csv line 4");
        output.ShouldContain("carbs_per_100");
        (await server.GlobAsync("meals/*")).ShouldBeEmpty();
        Body(await server.ExportAsync("foods")).ShouldBeEmpty();
    }

    // The rules a meal is saved under in the app hold for one carried over, each refusal naming the
    // line it is on.
    public static TheoryData<string, Func<string, string>, Func<string, string>, string> Unsaveable => new()
    {
        {
            "an item whose range is out of order", meals => meals,
            items => items.Replace(",42,32,52.5,", ",42,50,52.5,", StringComparison.Ordinal),
            "sugary-items.csv line 4"
        },
        {
            "an item over the 2000 g a meal may hold", meals => meals,
            items => items.Replace(",42,32,52.5,", ",42,32,2500,", StringComparison.Ordinal),
            "sugary-items.csv line 4"
        },
        {
            "an item with no name", meals => meals,
            items => items.Replace(",'+danger,", ",,", StringComparison.Ordinal),
            "sugary-items.csv line 4"
        },
        {
            "a first estimate out of order", meals => meals.Replace(",40,30,55,", ",40,50,55,", StringComparison.Ordinal),
            items => items,
            "sugary-meals.csv line 3"
        },
        {
            "a meal with no items", meals => meals,
            items => string.Join('\n', items.Split('\n').Where(line => !line.StartsWith(_toastDinner.ToString()))),
            "sugary-meals.csv line 2"
        }
    };

    [Theory]
    [MemberData(nameof(Unsaveable))]
    public async Task AMealTheAppWouldNotSave_ImportsNothingAndSaysWhere(
        string what, Func<string, string> meals, Func<string, string> items, string where)
    {
        await using var server = await SugaryServer.StartAsync("Europe/Madrid");
        File.WriteAllText(Path.Combine(_exports, "sugary-meals.csv"), meals(_meals));
        File.WriteAllText(Path.Combine(_exports, "sugary-items.csv"), items(_items));

        var (exit, output) = await ImportAsync(server);

        exit.ShouldNotBe(0, what);
        output.ShouldContain(where, customMessage: what);
        (await server.GlobAsync("meals/*")).ShouldBeEmpty(what);
        Body(await server.ExportAsync("foods")).ShouldBeEmpty(what);
    }

    [Fact]
    public async Task ADirectoryWithoutTheExports_IsRefused()
    {
        await using var server = await SugaryServer.StartAsync();
        await using var output = new StringWriter();

        var exit = await SugaryCommand.RunAsync(
            ["import", Path.Combine(_exports, "nowhere")], server.Settings, output, CancellationToken.None);

        exit.ShouldNotBe(0);
        output.ToString().ShouldContain("sugary-meals.csv");
    }

    // A file's rows without its header.
    private static string Body(string csv) => csv[(csv.IndexOf('\n') + 1)..];
}