using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// Her own foods, managed from the app: the standalone app's food tests carried over as behaviour at
// the Sugary server's HTTP boundary.
public class SugaryFoodApiTests
{
    private static JsonObject Bread() => new()
    {
        ["name"] = " Pan de molde ",
        ["carbsPer100g"] = 46,
        ["unit"] = "g",
        ["portionGrams"] = 32,
        ["portionCarbs"] = null,
        ["barcode"] = "8480000823298",
        ["source"] = "label"
    };

    private static JsonObject Toast() => new()
    {
        ["name"] = "Tostada del desayuno",
        ["carbsPer100g"] = null,
        ["unit"] = "g",
        ["portionGrams"] = null,
        ["portionCarbs"] = 28,
        ["barcode"] = null,
        ["source"] = "manual"
    };

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonNode>())!;

    private static JsonObject MealWith(string foodId, string eatenAt) => new()
    {
        ["eatenAt"] = eatenAt,
        ["outcome"] = "unknown",
        ["items"] = new JsonArray(new JsonObject
        {
            ["name"] = "Pan de molde",
            ["grams"] = 64,
            ["carbsPer100g"] = 46,
            ["carbsLow"] = 29.4,
            ["carbs"] = 29.4,
            ["carbsHigh"] = 29.4,
            ["source"] = "label",
            ["foodId"] = foodId
        })
    };

    [Fact]
    public async Task AFood_CanBeSavedChangedAndRemoved()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var created = await client.PostAsJsonAsync("/api/foods", Bread());
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var food = await ReadAsync(created);
        food["name"]!.GetValue<string>().ShouldBe("Pan de molde");
        food["portionGrams"]!.GetValue<double>().ShouldBe(32);
        food["uses"]!.GetValue<int>().ShouldBe(0);
        var id = food["id"]!.GetValue<string>();

        var changed = Bread();
        changed["portionGrams"] = 64;
        (await ReadAsync(await client.PutAsJsonAsync($"/api/foods/{id}", changed)))["portionGrams"]!
            .GetValue<double>().ShouldBe(64);
        (await ReadAsync(await client.GetAsync("/api/foods"))).AsArray().Count.ShouldBe(1);

        (await client.DeleteAsync($"/api/foods/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ReadAsync(await client.GetAsync("/api/foods"))).AsArray().ShouldBeEmpty();
        (await client.PutAsJsonAsync($"/api/foods/{id}", changed)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AFood_MayHaveOnlyTheCarbsOfItsUsualPortion()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var food = await ReadAsync(await client.PostAsJsonAsync("/api/foods", Toast()));

        food["carbsPer100g"].ShouldBeNull();
        food["portionCarbs"]!.GetValue<double>().ShouldBe(28);
    }

    [Theory]
    [InlineData("name", " ")]
    [InlineData("carbsPer100g", 140)]
    [InlineData("portionGrams", 0)]
    [InlineData("unit", "oz")]
    [InlineData("barcode", "12ab")]
    [InlineData("source", "estimate")]
    public async Task AFoodThatMakesNoSense_IsRejected(string field, object value)
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var food = Bread();
        food[field] = JsonValue.Create(value);

        (await client.PostAsJsonAsync("/api/foods", food)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AFood_NeedsEitherADensityOrThePortionsCarbs()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var food = Toast();
        food["portionCarbs"] = null;

        (await client.PostAsJsonAsync("/api/foods", food)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Foods_AreListedWithTheOnesEatenMostRecentlyFirst()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var bread = (await ReadAsync(await client.PostAsJsonAsync("/api/foods", Bread())))["id"]!.GetValue<string>();
        await client.PostAsJsonAsync("/api/foods", Toast());
        await client.PostAsJsonAsync("/api/meals", MealWith(bread, "2026-10-10T08:00:00+02:00"));
        await client.PostAsJsonAsync("/api/meals", MealWith(bread, "2026-10-11T08:00:00+02:00"));

        var foods = (await ReadAsync(await client.GetAsync("/api/foods"))).AsArray();

        foods.Select(f => f!["name"]!.GetValue<string>()).ShouldBe(["Pan de molde", "Tostada del desayuno"]);
        foods[0]!["uses"]!.GetValue<int>().ShouldBe(2);
        foods[0]!["lastUsedAt"]!.GetValue<DateTimeOffset>()
            .ShouldBe(DateTimeOffset.Parse("2026-10-11T06:00:00Z", CultureInfo.InvariantCulture));
        foods[1]!["lastUsedAt"].ShouldBeNull();
    }

    [Fact]
    public async Task AMealItem_RemembersTheFoodItCameFrom()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var id = (await ReadAsync(await client.PostAsJsonAsync("/api/foods", Bread())))["id"]!.GetValue<string>();

        await client.PostAsJsonAsync("/api/meals", MealWith(id, "2026-10-11T08:00:00+02:00"));

        (await ReadAsync(await client.GetAsync("/api/meals")))[0]!["items"]![0]!["foodId"]!.GetValue<string>()
            .ShouldBe(id);
    }

    [Fact]
    public async Task AFoodSavedInTheApp_IsInTheMountOnTheNextRead()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var id = (await ReadAsync(await client.PostAsJsonAsync("/api/foods", Bread())))["id"]!.GetValue<string>();

        (await server.ReadAsync("/foods.csv")).ShouldContain($"{id},Pan de molde,46,g,32,,8480000823298,label");
    }
}