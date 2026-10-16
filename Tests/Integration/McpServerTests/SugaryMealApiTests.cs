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

    // The rapid insulin she took for a meal, noted as she saves it: one save, linked to the meal and
    // stamped with its time, since the app knows no better one.
    [Fact]
    public async Task AMealSavedWithADose_StoresBoth_TheDoseRapidAtTheMealsTimeAndLinked()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var meal = Meal();
        meal["dose"] = new JsonObject { ["units"] = 4.5, ["note"] = " for the rice " };

        var created = await client.PostAsJsonAsync("/api/meals", meal);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var saved = await ReadAsync(created);
        var id = saved["id"]!.GetValue<string>();
        var dose = (await client.GetFromJsonAsync<JsonNode>("/api/doses"))!.AsArray().Single()!;
        dose["units"]!.GetValue<double>().ShouldBe(4.5);
        dose["kind"]!.GetValue<string>().ShouldBe("rapid");
        dose["note"]!.GetValue<string>().ShouldBe("for the rice");
        dose["mealId"]!.GetValue<string>().ShouldBe(id);
        DateTimeOffset.Parse(dose["takenAt"]!.GetValue<string>(), CultureInfo.InvariantCulture)
            .ShouldBe(DateTimeOffset.Parse("2026-10-11T13:30:00+02:00", CultureInfo.InvariantCulture));
        saved["doses"]!.AsArray().Single()!["units"]!.GetValue<double>().ShouldBe(4.5);
    }

    public static TheoryData<double, string?, string> RefusedDoses => new()
    {
        { 0, null, "A dose needs a number of units above 0." },
        { 100.5, null, "A dose can be at most 100 units." },
        { 2.125, null, "Units can have at most two decimals." },
        { 4, new string('x', 201), "A note can be at most 200 characters." }
    };

    // A failure never leaves the meal saved and her dose silently lost.
    [Theory]
    [MemberData(nameof(RefusedDoses))]
    public async Task AMealSavedWithARefusedDose_StoresNeither(double units, string? note, string sentence)
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var meal = Meal();
        meal["dose"] = new JsonObject { ["units"] = units, ["note"] = note };

        var response = await client.PostAsJsonAsync("/api/meals", meal);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadAsync(response))["detail"]!.GetValue<string>().ShouldBe(sentence);
        (await client.GetFromJsonAsync<JsonNode>("/api/meals"))!.AsArray().ShouldBeEmpty();
        (await client.GetFromJsonAsync<JsonNode>("/api/doses"))!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task AMealWithoutADose_SavesAsItAlwaysHas()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var saved = await ReadAsync(await client.PostAsJsonAsync("/api/meals", Meal()));

        saved["doses"]!.AsArray().ShouldBeEmpty();
        (await client.GetFromJsonAsync<JsonNode>("/api/doses"))!.AsArray().ShouldBeEmpty();
    }

    // Saving the whole meal again — an edit, an outcome — is about the meal; its doses change only
    // through their own routes.
    [Fact]
    public async Task AMealUpdate_LeavesItsDosesAsTheyWere()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var meal = Meal();
        meal["dose"] = new JsonObject { ["units"] = 6 };
        var id = (await ReadAsync(await client.PostAsJsonAsync("/api/meals", meal)))["id"]!.GetValue<string>();

        var edit = Meal("2026-10-11T15:00:00+02:00", carbs: 60);
        edit["outcome"] = "high";
        edit["dose"] = new JsonObject { ["units"] = 9 };
        (await client.PutAsJsonAsync($"/api/meals/{id}", edit)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var dose = (await client.GetFromJsonAsync<JsonNode>("/api/doses"))!.AsArray().Single()!;
        dose["units"]!.GetValue<double>().ShouldBe(6);
        dose["mealId"]!.GetValue<string>().ShouldBe(id);
        DateTimeOffset.Parse(dose["takenAt"]!.GetValue<string>(), CultureInfo.InvariantCulture)
            .ShouldBe(DateTimeOffset.Parse("2026-10-11T13:30:00+02:00", CultureInfo.InvariantCulture));
    }

    // The Log shows a meal's insulin from the list it already has: each meal with its doses' ids,
    // units and kinds, however each was linked.
    [Fact]
    public async Task TheMealList_CarriesEachMealsDoses()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var lunch = Meal("2026-10-11T13:30:00+02:00");
        lunch["dose"] = new JsonObject { ["units"] = 5 };
        var lunchId = (await ReadAsync(await client.PostAsJsonAsync("/api/meals", lunch)))["id"]!.GetValue<string>();
        await client.PostAsJsonAsync("/api/meals", Meal("2026-10-11T09:00:00+02:00"));
        var topUp = await ReadAsync(await client.PostAsJsonAsync("/api/doses", new JsonObject
        {
            ["takenAt"] = "2026-10-11T15:00:00+02:00",
            ["units"] = 1.5,
            ["kind"] = "rapid",
            ["mealId"] = lunchId
        }));

        var meals = (await client.GetFromJsonAsync<JsonNode>("/api/meals"))!.AsArray();

        var doses = meals[0]!["doses"]!.AsArray();
        doses.Select(d => d!["units"]!.GetValue<double>()).Order().ShouldBe([1.5, 5]);
        doses.Select(d => d!["kind"]!.GetValue<string>()).ShouldAllBe(kind => kind == "rapid");
        doses.Select(d => d!["id"]!.GetValue<string>()).ShouldContain(topUp["id"]!.GetValue<string>());
        meals[1]!["doses"]!.AsArray().ShouldBeEmpty();
    }

    // Insulin she really took is never erased with a meal she logged wrongly: the dose stays, no
    // longer linked to anything.
    [Fact]
    public async Task DeletingAMeal_KeepsItsDoses_WithTheLinkCleared()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var meal = Meal();
        meal["dose"] = new JsonObject { ["units"] = 7 };
        var id = (await ReadAsync(await client.PostAsJsonAsync("/api/meals", meal)))["id"]!.GetValue<string>();

        (await client.DeleteAsync($"/api/meals/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var dose = (await client.GetFromJsonAsync<JsonNode>("/api/doses"))!.AsArray().Single()!;
        dose["units"]!.GetValue<double>().ShouldBe(7);
        dose["mealId"].ShouldBeNull();
        (await client.GetFromJsonAsync<JsonNode>("/api/meals"))!.AsArray().ShouldBeEmpty();
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