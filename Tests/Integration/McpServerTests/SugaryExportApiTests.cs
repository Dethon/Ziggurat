using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// The three downloads in the app's settings: the standalone app's export tests carried over as
// behaviour at the Sugary server's HTTP boundary. Each time is in the clock the app asks for (the
// phone's), or UTC when it asks for none, as the standalone app wrote them.
public class SugaryExportApiTests
{
    private static JsonObject Item(string name, double carbs, string source = "estimate") => new()
    {
        ["name"] = name,
        ["portion"] = "a cup",
        ["grams"] = 150,
        ["carbsPer100g"] = 28,
        ["carbsLow"] = carbs - 10,
        ["carbs"] = carbs,
        ["carbsHigh"] = carbs + 10.5,
        ["source"] = source,
        ["confidence"] = source == "estimate" ? "medium" : null,
        ["edited"] = false
    };

    private static JsonObject Meal(string eatenAt, string? notes, params JsonObject[] items) => new()
    {
        ["eatenAt"] = eatenAt,
        ["notes"] = notes,
        ["outcome"] = "high",
        ["firstEstimate"] = new JsonObject { ["low"] = 30, ["best"] = 40, ["high"] = 55 },
        ["items"] = new JsonArray([.. items])
    };

    private static async Task<string[]> LinesAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Take(3).ShouldNotBe([0xEF, 0xBB, 0xBF]);
        return System.Text.Encoding.UTF8.GetString(bytes).TrimEnd('\n').Split('\n');
    }

    private static async Task<string> SaveAsync(HttpClient client, JsonObject meal) =>
        (await (await client.PostAsJsonAsync("/api/meals", meal)).Content.ReadFromJsonAsync<JsonNode>())!
        ["id"]!.GetValue<string>();

    [Fact]
    public async Task Meals_ExportAsOneRowEachInTheClockThatWasAskedFor()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var lunch = await SaveAsync(client,
            Meal("2026-10-11T11:30:00Z", "ate out, \"menú\"", Item("bread", 25, "label"), Item("rice", 42)));
        var breakfast = await SaveAsync(client, Meal("2026-03-01T07:00:00Z", null, Item("toast", 30)));

        var lines = await LinesAsync(client, "/api/export/meals.csv?tz=Europe/Madrid");

        lines.ShouldBe([
            "meal_id,eaten_at,carbs_g,carbs_low_g,carbs_high_g,first_estimate_g,first_estimate_low_g,first_estimate_high_g,outcome,items,notes",
            $"{breakfast},2026-03-01T08:00:00+01:00,30,20,40.5,40,30,55,high,toast,",
            $"{lunch},2026-10-11T13:30:00+02:00,67,47,88,40,30,55,high,bread; rice,\"ate out, \"\"menú\"\"\""
        ]);
    }

    [Fact]
    public async Task Items_ExportAsOneRowEachInTheOrderOfTheMeal()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var lunch = await SaveAsync(client,
            Meal("2026-10-11T11:30:00Z", null, Item("bread", 25, "label"), Item("rice", 42)));

        var lines = await LinesAsync(client, "/api/export/items.csv");

        lines.ShouldBe([
            "meal_id,eaten_at,position,name,portion,amount,carbs_per_100,carbs_g,carbs_low_g,carbs_high_g,source,confidence,edited,food_id",
            $"{lunch},2026-10-11T11:30:00+00:00,1,bread,a cup,150,28,25,15,35.5,label,,false,",
            $"{lunch},2026-10-11T11:30:00+00:00,2,rice,a cup,150,28,42,32,52.5,estimate,medium,false,"
        ]);
    }

    [Fact]
    public async Task AnExport_CanBeLimitedToAPeriod()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        await SaveAsync(client, Meal("2026-10-09T12:00:00Z", null, Item("old", 10)));
        await SaveAsync(client, Meal("2026-10-11T12:00:00Z", null, Item("inside", 20)));
        await SaveAsync(client, Meal("2026-10-13T12:00:00Z", null, Item("new", 30)));

        var lines = await LinesAsync(client, "/api/export/items.csv?from=2026-10-10T00:00:00Z&to=2026-10-12T00:00:00Z");

        lines.Skip(1).Select(line => line.Split(',')[3]).ShouldBe(["inside"]);
    }

    [Fact]
    public async Task TextThatASpreadsheetWouldRunAsAFormula_IsExportedAsText()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        await SaveAsync(client, Meal("2026-10-11T12:00:00Z", "=HYPERLINK(\"http://x\")", Item("+danger", 10)));

        var meals = await LinesAsync(client, "/api/export/meals.csv");
        var items = await LinesAsync(client, "/api/export/items.csv");

        meals[1].ShouldEndWith(",'+danger,\"'=HYPERLINK(\"\"http://x\"\")\"");
        items[1].Split(',')[3].ShouldBe("'+danger");
    }

    [Fact]
    public async Task Foods_ExportToo()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var food = new JsonObject
        {
            ["name"] = "Pan de molde",
            ["carbsPer100g"] = 46,
            ["unit"] = "g",
            ["portionGrams"] = 32,
            ["barcode"] = "8480000823298",
            ["source"] = "label"
        };
        var id = (await (await client.PostAsJsonAsync("/api/foods", food)).Content
            .ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<string>();

        var lines = await LinesAsync(client, "/api/export/foods.csv");

        lines[0].ShouldBe("food_id,name,carbs_per_100,unit,portion_amount,portion_carbs_g,barcode,source");
        lines[1].ShouldBe($"{id},Pan de molde,46,g,32,,8480000823298,label");
    }

    private static async Task<string> SaveDoseAsync(
        HttpClient client, string takenAt, double units, string kind, string? note = null) =>
        (await (await client.PostAsJsonAsync("/api/doses", new JsonObject
        {
            ["takenAt"] = takenAt,
            ["units"] = units,
            ["kind"] = kind,
            ["note"] = note
        })).Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<string>();

    [Fact]
    public async Task Doses_ExportAsOneRowEachInTheClockThatWasAskedFor()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var basal = await SaveDoseAsync(client, "2026-03-01T21:00:00Z", 14, "basal");
        var correction = await SaveDoseAsync(client, "2026-10-11T16:00:00Z", 1.5, "rapid", "=1+1");

        var madrid = await LinesAsync(client, "/api/export/doses.csv?tz=Europe/Madrid");
        var utc = await LinesAsync(client, "/api/export/doses.csv");

        madrid.ShouldBe([
            "dose_id,taken_at,kind,units,note",
            $"{basal},2026-03-01T22:00:00+01:00,basal,14,",
            $"{correction},2026-10-11T18:00:00+02:00,rapid,1.5,'=1+1"
        ]);
        utc[1].ShouldBe($"{basal},2026-03-01T21:00:00+00:00,basal,14,");
    }

    // The meals each dose was for leave with the doses, a row per link, so one bolus for two meals is
    // still one row of doses.csv.
    [Fact]
    public async Task TheMealsADoseWasFor_ExportAsARowPerLink()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var lunch = await SaveAsync(client, Meal("2026-10-11T11:30:00Z", null, Item("rice", 42)));
        var dessert = await SaveAsync(client, Meal("2026-10-11T11:50:00Z", null, Item("flan", 30)));
        var bolus = (await (await client.PostAsJsonAsync("/api/doses", new JsonObject
        {
            ["takenAt"] = "2026-10-11T11:30:00Z",
            ["units"] = 6,
            ["kind"] = "rapid",
            ["mealIds"] = new JsonArray(lunch, dessert)
        })).Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<string>();
        await SaveDoseAsync(client, "2026-10-11T21:00:00Z", 14, "basal");

        var lines = await LinesAsync(client, "/api/export/dose-meals.csv");

        lines.ShouldBe(["dose_id,meal_id", $"{bolus},{lunch}", $"{bolus},{dessert}"]);
        (await LinesAsync(client, "/api/export/doses.csv")).Length.ShouldBe(3);
    }

    [Fact]
    public async Task ADoseExport_CanBeLimitedToAPeriod()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        await SaveDoseAsync(client, "2026-10-09T12:00:00Z", 1, "rapid");
        var inside = await SaveDoseAsync(client, "2026-10-11T12:00:00Z", 2, "rapid");
        await SaveDoseAsync(client, "2026-10-13T12:00:00Z", 3, "rapid");

        var lines = await LinesAsync(client, "/api/export/doses.csv?from=2026-10-10T00:00:00Z&to=2026-10-12T00:00:00Z");

        lines.Skip(1).Select(line => line.Split(',')[0]).ShouldBe([inside]);
    }

    [Fact]
    public async Task Exercise_ExportsAsOneRowEachInTheClockThatWasAskedFor()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var created = await client.PostAsJsonAsync("/api/exercise", new JsonObject
        {
            ["kind"] = "gym",
            ["startedAt"] = "2026-03-01T18:00:00Z",
            ["endedAt"] = "2026-03-01T19:05:00Z"
        });
        var id = (await created.Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<string>();

        var madrid = await LinesAsync(client, "/api/export/exercise.csv?tz=Europe/Madrid");
        var outside = await LinesAsync(client, "/api/export/exercise.csv?from=2026-03-02T00:00:00Z");

        madrid.ShouldBe([
            "exercise_id,started_at,ended_at,minutes,kind,name,note",
            $"{id},2026-03-01T19:00:00+01:00,2026-03-01T20:05:00+01:00,65,gym,,"
        ]);
        outside.ShouldBe(["exercise_id,started_at,ended_at,minutes,kind,name,note"]);
    }

    // The names the import reads them back under, so a download can be carried to another server;
    // doses, which the import does not read, by the same pattern.
    [Theory]
    [InlineData("meals")]
    [InlineData("items")]
    [InlineData("foods")]
    [InlineData("doses")]
    [InlineData("exercise")]
    [InlineData("periods")]
    [InlineData("day-notes")]
    public async Task ADownload_IsNamedAsTheImportExpectsIt(string table)
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var response = await client.GetAsync($"/api/export/{table}.csv");

        response.Content.Headers.ContentDisposition!.FileName.ShouldBe($"sugary-{table}.csv");
    }

    // Days have no clock, so her cycle downloads as the mount renders it, whatever zone is named.
    [Fact]
    public async Task HerCycle_ExportsAsTheMountHoldsIt()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        await client.PostAsJsonAsync("/api/periods", new JsonObject { ["start"] = "2026-09-09", ["end"] = "2026-09-14" });
        await client.PutAsJsonAsync("/api/day-notes/2026-09-10", new JsonObject { ["text"] = "cramps" });

        var periods = await LinesAsync(client, "/api/export/periods.csv?tz=Europe/Madrid");
        var notes = await LinesAsync(client, "/api/export/day-notes.csv");

        var held = (await server.TableRowsAsync("/periods.parquet")).Single();
        periods[1].ShouldBe($"{held[0]},2026-09-09,2026-09-14,6,");
        held[1..].ShouldBe(["2026-09-09", "2026-09-14", "6", null]);
        notes.ShouldBe(["date,note", "2026-09-10,cramps"]);
    }

    [Fact]
    public async Task AnUnknownClock_IsRefused()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        (await client.GetAsync("/api/export/meals.csv?tz=Mars/Olympus")).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/export/doses.csv?tz=Mars/Olympus")).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
    }
}