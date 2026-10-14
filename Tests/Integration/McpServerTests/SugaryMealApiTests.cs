using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// The standalone app's meal API tests, carried over as behaviour at the Sugary server's HTTP boundary:
// the app saves, edits, lists and deletes a meal exactly as it did against the standalone server.
public class SugaryMealApiTests
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static JsonObject Meal(string eatenAt = "2026-10-11T13:30:00+02:00", double carbs = 42) => new()
    {
        ["eatenAt"] = eatenAt,
        ["notes"] = " lunch out ",
        ["outcome"] = "unknown",
        ["items"] = new JsonArray(new JsonObject
        {
            ["name"] = "rice",
            ["portion"] = "a cup",
            ["grams"] = 150,
            ["carbsPer100g"] = 28,
            ["carbsLow"] = carbs - 10,
            ["carbs"] = carbs,
            ["carbsHigh"] = carbs + 10,
            ["source"] = "estimate"
        })
    };

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonNode>(_json))!;

    [Fact]
    public async Task AMeal_CanBeSavedListedEditedAndDeleted()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var created = await client.PostAsJsonAsync("/api/meals", Meal());
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await ReadAsync(created))["id"]!.GetValue<string>();

        var edit = Meal(carbs: 60);
        edit["outcome"] = "high";
        var updated = await ReadAsync(await client.PutAsJsonAsync($"/api/meals/{id}", edit));
        updated["outcome"]!.GetValue<string>().ShouldBe("high");

        var listed = (await client.GetFromJsonAsync<JsonNode>("/api/meals"))!.AsArray().Single()!;
        listed["notes"]!.GetValue<string>().ShouldBe("lunch out");
        listed["items"]![0]!["carbs"]!.GetValue<double>().ShouldBe(60);
        listed["items"]![0]!["source"]!.GetValue<string>().ShouldBe("estimate");
        listed["items"]![0]!.AsObject().ContainsKey("position").ShouldBeFalse();
        DateTimeOffset.Parse(listed["eatenAt"]!.GetValue<string>(), CultureInfo.InvariantCulture)
            .ShouldBe(DateTimeOffset.Parse("2026-10-11T11:30:00Z", CultureInfo.InvariantCulture));

        (await client.DeleteAsync($"/api/meals/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.GetFromJsonAsync<JsonNode>("/api/meals"))!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task TheFirstEstimate_IsLoggedOnceAndSurvivesEdits()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var meal = Meal(carbs: 30);
        meal["firstEstimate"] = new JsonObject { ["low"] = 40, ["best"] = 55, ["high"] = 70 };
        meal["items"]![0]!["confidence"] = "low";
        meal["items"]![0]!["edited"] = true;
        var id = (await ReadAsync(await client.PostAsJsonAsync("/api/meals", meal)))["id"]!.GetValue<string>();

        var edit = Meal(carbs: 30);
        edit["firstEstimate"] = new JsonObject { ["low"] = 1, ["best"] = 2, ["high"] = 3 };
        await client.PutAsJsonAsync($"/api/meals/{id}", edit);

        var listed = (await client.GetFromJsonAsync<JsonNode>("/api/meals"))!.AsArray().Single()!;
        listed["firstEstimate"]!["best"]!.GetValue<double>().ShouldBe(55);
        listed["items"]![0]!["edited"]!.GetValue<bool>().ShouldBeFalse();

        var created = Meal();
        created["items"]![0]!["confidence"] = "low";
        created["items"]![0]!["edited"] = true;
        var saved = await ReadAsync(await client.PostAsJsonAsync("/api/meals", created));
        saved["firstEstimate"].ShouldBeNull();
        saved["items"]![0]!["confidence"]!.GetValue<string>().ShouldBe("low");
        saved["items"]![0]!["edited"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task AMeal_IsStoredAsPlainRowsAnotherToolCanRead()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var meal = Meal();
        meal["firstEstimate"] = new JsonObject { ["low"] = 30, ["best"] = 40, ["high"] = 55 };
        meal["items"]!.AsArray().Insert(0, new JsonObject
        {
            ["name"] = "bread",
            ["carbsLow"] = 20,
            ["carbs"] = 25,
            ["carbsHigh"] = 30,
            ["source"] = "label"
        });
        var saved = await ReadAsync(await client.PostAsJsonAsync("/api/meals", meal));
        // Saving again must leave one set of rows, in the order the person sees.
        await client.PutAsJsonAsync($"/api/meals/{saved["id"]!.GetValue<string>()}", meal);

        await using var db = new SqliteConnection($"Data Source={server.Settings.DatabasePath}");
        await db.OpenAsync();
        var mealRow = await QueryAsync(db,
            "SELECT EatenAt, Carbs, CarbsLow, CarbsHigh, FirstEstimateBest, Outcome FROM Meals");
        var itemRows = await QueryAsync(db,
            "SELECT Name, Carbs, Source, Confidence FROM MealItems ORDER BY MealId, Position");

        mealRow.ShouldBe(["2026-10-11 11:30:00|67|52|82|40|Unknown"]);
        itemRows.ShouldBe(["bread|25|Label|", "rice|42|Estimate|"]);
        (await ReadAsync(await client.GetAsync("/api/meals")))[0]!["items"]!.AsArray()
            .Select(item => item!["name"]!.GetValue<string>()).ShouldBe(["bread", "rice"]);
    }

    private static async Task<List<string>> QueryAsync(SqliteConnection db, string sql)
    {
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(string.Join('|', Enumerable.Range(0, reader.FieldCount).Select(i => reader.GetValue(i) is DBNull
                ? ""
                : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
        }

        return rows;
    }

    [Fact]
    public async Task Meals_AreListedNewestFirst()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        await client.PostAsJsonAsync("/api/meals", Meal("2026-10-10T09:00:00Z", carbs: 20));
        await client.PostAsJsonAsync("/api/meals", Meal("2026-10-11T09:00:00Z", carbs: 30));

        var carbs = (await client.GetFromJsonAsync<JsonNode>("/api/meals"))!.AsArray()
            .Select(m => m!["items"]![0]!["carbs"]!.GetValue<double>());

        carbs.ShouldBe([30, 20]);
    }

    [Fact]
    public async Task APhoto_IsStoredAndKeptAcrossEdits()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var meal = Meal();
        meal["photo"] = "data:image/jpeg;base64," + Convert.ToBase64String([0xFF, 0xD8, 0xFF]);

        var created = await ReadAsync(await client.PostAsJsonAsync("/api/meals", meal));
        var id = created["id"]!.GetValue<string>();
        created["hasPhoto"]!.GetValue<bool>().ShouldBeTrue();

        await client.PutAsJsonAsync($"/api/meals/{id}", Meal());

        (await client.GetByteArrayAsync($"/api/meals/{id}/photo")).ShouldBe([0xFF, 0xD8, 0xFF]);
    }

    [Fact]
    public async Task AMealWithoutItemsOrWithInconsistentCarbs_IsRejected()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var empty = Meal();
        empty["items"] = new JsonArray();
        var inverted = Meal();
        inverted["items"]![0]!["carbsLow"] = 90;

        (await client.PostAsJsonAsync("/api/meals", empty)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/meals", inverted)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task EditingAMissingMeal_IsNotFound()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var response = await client.PutAsJsonAsync($"/api/meals/{Guid.NewGuid()}", Meal());

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // What she saves in the app is what another agent reads at /sugary, with nothing in between.
    [Fact]
    public async Task AMealSavedInTheApp_IsInTheMountOnTheNextRead()
    {
        await using var server = await SugaryServer.StartAsync("Europe/Madrid");
        var client = await server.SignedInAsync();

        var id = (await ReadAsync(await client.PostAsJsonAsync("/api/meals", Meal())))["id"]!.GetValue<string>();

        (await server.ReadAsync("/meals/2026-10.csv")).ShouldContain($"{id},2026-10-11T13:30:00+02:00,42,32,52,");
        (await server.ReadAsync("/items/2026-10.csv")).ShouldContain($"{id},2026-10-11T13:30:00+02:00,1,rice,a cup,");

        var edit = Meal(carbs: 60);
        edit["outcome"] = "inRange";
        await client.PutAsJsonAsync($"/api/meals/{id}", edit);

        (await server.ReadAsync("/meals/2026-10.csv")).ShouldContain($"{id},2026-10-11T13:30:00+02:00,60,50,70,,,,inRange,");
    }
}