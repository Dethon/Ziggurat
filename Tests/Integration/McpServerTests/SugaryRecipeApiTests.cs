using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// Her recipes: foods from her pantry in given amounts, with what the pot makes, kept at the Sugary
// server's HTTP boundary. The server keeps a recipe's shape; the app works out its carbs.
public class SugaryRecipeApiTests
{
    private static JsonObject Food(string name, double carbsPer100g) => new()
    {
        ["name"] = name,
        ["carbsPer100g"] = carbsPer100g,
        ["unit"] = "g",
        ["portionGrams"] = null,
        ["portionCarbs"] = null,
        ["barcode"] = null,
        ["source"] = "manual"
    };

    private static JsonObject Lentils() => Food("Lentejas", 48);

    private static JsonObject Potato() => Food("Patata", 17);

    private static JsonObject Stew(params (string FoodId, double Amount)[] ingredients) => new()
    {
        ["name"] = " Lentejas de la abuela ",
        ["cookedGrams"] = 1800,
        ["servings"] = null,
        ["ingredients"] = new JsonArray([.. ingredients.Select(i => (JsonNode)new JsonObject
        {
            ["foodId"] = i.FoodId,
            ["amount"] = i.Amount
        })])
    };

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonNode>())!;

    private static async Task<string> FoodAsync(HttpClient client, JsonObject food) =>
        (await ReadAsync(await client.PostAsJsonAsync("/api/foods", food)))["id"]!.GetValue<string>();

    [Fact]
    public async Task ARecipe_CanBeSavedChangedAndRemoved()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var lentils = await FoodAsync(client, Lentils());
        var potato = await FoodAsync(client, Potato());

        var created = await client.PostAsJsonAsync("/api/recipes", Stew((lentils, 300), (potato, 400)));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var recipe = await ReadAsync(created);
        recipe["name"]!.GetValue<string>().ShouldBe("Lentejas de la abuela");
        recipe["cookedGrams"]!.GetValue<double>().ShouldBe(1800);
        recipe["servings"].ShouldBeNull();
        recipe["ingredients"]!.AsArray().Select(i => (i!["foodId"]!.GetValue<string>(), i["amount"]!.GetValue<double>()))
            .ShouldBe([(lentils, 300d), (potato, 400d)]);
        recipe["uses"]!.GetValue<int>().ShouldBe(0);
        var id = recipe["id"]!.GetValue<string>();

        var changed = Stew((potato, 500), (lentils, 250));
        changed["cookedGrams"] = null;
        changed["servings"] = 6;
        var updated = await ReadAsync(await client.PutAsJsonAsync($"/api/recipes/{id}", changed));
        updated["servings"]!.GetValue<double>().ShouldBe(6);
        updated["cookedGrams"].ShouldBeNull();

        var listed = (await ReadAsync(await client.GetAsync("/api/recipes"))).AsArray().ShouldHaveSingleItem()!;
        listed["ingredients"]!.AsArray().Select(i => (i!["foodId"]!.GetValue<string>(), i["amount"]!.GetValue<double>()))
            .ShouldBe([(potato, 500d), (lentils, 250d)]);

        (await client.DeleteAsync($"/api/recipes/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ReadAsync(await client.GetAsync("/api/recipes"))).AsArray().ShouldBeEmpty();
        (await client.PutAsJsonAsync($"/api/recipes/{id}", changed)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("name", " ")]
    [InlineData("cookedGrams", 0)]
    [InlineData("servings", 4)]
    public async Task ARecipeThatMakesNoSense_IsRejected(string field, object value)
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var recipe = Stew((await FoodAsync(client, Lentils()), 300));
        recipe[field] = JsonValue.Create(value);

        (await client.PostAsJsonAsync("/api/recipes", recipe)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ARecipe_NeedsWhatThePotMakes()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var recipe = Stew((await FoodAsync(client, Lentils()), 300));
        recipe["cookedGrams"] = null;

        (await client.PostAsJsonAsync("/api/recipes", recipe)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ARecipe_NeedsIngredientsFromHerPantry()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var lentils = await FoodAsync(client, Lentils());

        (await client.PostAsJsonAsync("/api/recipes", Stew())).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/recipes", Stew((Guid.NewGuid().ToString(), 300))))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/recipes", Stew((lentils, 0))))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ARecipe_TakesEachFoodOnce()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var lentils = await FoodAsync(client, Lentils());

        (await client.PostAsJsonAsync("/api/recipes", Stew((lentils, 300), (lentils, 100))))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ARecipeChanged_StillSaysHowOftenSheHadIt()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var lentils = await FoodAsync(client, Lentils());
        var stew = (await ReadAsync(await client.PostAsJsonAsync("/api/recipes", Stew((lentils, 300)))))["id"]!
            .GetValue<string>();
        await client.PostAsJsonAsync("/api/meals", HelpingOf(stew, "2026-10-11T14:00:00+02:00"));

        var changed = await ReadAsync(await client.PutAsJsonAsync($"/api/recipes/{stew}", Stew((lentils, 350))));

        changed["uses"]!.GetValue<int>().ShouldBe(1);
        changed["lastUsedAt"]!.GetValue<DateTimeOffset>()
            .ShouldBe(DateTimeOffset.Parse("2026-10-11T12:00:00Z", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task AFoodInARecipe_CannotBeDeletedUntilTheRecipeLetsGoOfIt()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var lentils = await FoodAsync(client, Lentils());
        var recipe = (await ReadAsync(await client.PostAsJsonAsync("/api/recipes", Stew((lentils, 300)))))["id"]!
            .GetValue<string>();

        (await client.DeleteAsync($"/api/foods/{lentils}")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ReadAsync(await client.GetAsync("/api/foods"))).AsArray().ShouldHaveSingleItem();

        (await client.DeleteAsync($"/api/recipes/{recipe}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.DeleteAsync($"/api/foods/{lentils}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Recipes_AreListedWithTheOnesEatenMostRecentlyFirst()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var lentils = await FoodAsync(client, Lentils());
        var stew = (await ReadAsync(await client.PostAsJsonAsync("/api/recipes", Stew((lentils, 300)))))["id"]!
            .GetValue<string>();
        var other = Stew((lentils, 100));
        other["name"] = "Algo de lentejas";
        await client.PostAsJsonAsync("/api/recipes", other);
        await client.PostAsJsonAsync("/api/meals", HelpingOf(stew, "2026-10-10T14:00:00+02:00"));
        await client.PostAsJsonAsync("/api/meals", HelpingOf(stew, "2026-10-11T14:00:00+02:00"));

        var recipes = (await ReadAsync(await client.GetAsync("/api/recipes"))).AsArray();

        recipes.Select(r => r!["name"]!.GetValue<string>()).ShouldBe(["Lentejas de la abuela", "Algo de lentejas"]);
        recipes[0]!["uses"]!.GetValue<int>().ShouldBe(2);
        recipes[0]!["lastUsedAt"]!.GetValue<DateTimeOffset>()
            .ShouldBe(DateTimeOffset.Parse("2026-10-11T12:00:00Z", CultureInfo.InvariantCulture));
        recipes[1]!["lastUsedAt"].ShouldBeNull();
        (await ReadAsync(await client.GetAsync("/api/meals")))[0]!["items"]![0]!["recipeId"]!.GetValue<string>()
            .ShouldBe(stew);
    }

    private static JsonObject HelpingOf(string recipeId, string eatenAt) => new()
    {
        ["eatenAt"] = eatenAt,
        ["outcome"] = "unknown",
        ["items"] = new JsonArray(new JsonObject
        {
            ["name"] = "Lentejas de la abuela",
            ["grams"] = 300,
            ["carbsPer100g"] = 8,
            ["carbsLow"] = 24,
            ["carbs"] = 24,
            ["carbsHigh"] = 24,
            ["source"] = "manual",
            ["recipeId"] = recipeId
        })
    };

    // Two people of the household with the recipe open: Ana raised the flour and saved, then Dani —
    // whose sheet still had the old amount — changed the servings and saved, and Ana's flour was back
    // to what it was, the carbs per helping wrong with nobody told. A sheet sends what it opened with
    // beside what it wants, and only what it changed is laid over the recipe as it is now.
    [Fact]
    public async Task TwoPeopleChangingOneRecipe_EachChangeStands_AndTheSameFieldIsRefused()
    {
        await using var server = await SugaryServer.StartAsync();
        var ana = server.As(SugaryServer.Her);
        var dani = server.As(SugaryServer.Him);
        var lentils = await FoodAsync(ana, Lentils());
        var potato = await FoodAsync(ana, Potato());
        var opened = Stew((lentils, 300), (potato, 400));
        var id = (await ReadAsync(await ana.PostAsJsonAsync("/api/recipes", opened)))["id"]!.GetValue<string>();
        opened["name"] = "Lentejas de la abuela";

        var anas = (JsonObject)opened.DeepClone();
        anas["ingredients"]![0]!["amount"] = 350;
        anas["loaded"] = opened.DeepClone();
        (await ana.PutAsJsonAsync($"/api/recipes/{id}", anas)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var danis = (JsonObject)opened.DeepClone();
        danis["cookedGrams"] = 2000;
        danis["loaded"] = opened.DeepClone();
        var merged = await ReadAsync(await dani.PutAsJsonAsync($"/api/recipes/{id}", danis));
        merged["cookedGrams"]!.GetValue<double>().ShouldBe(2000);
        merged["ingredients"]!.AsArray().Select(i => i!["amount"]!.GetValue<double>()).ShouldBe([350d, 400d]);

        var clash = (JsonObject)opened.DeepClone();
        clash["ingredients"]![0]!["amount"] = 200;
        clash["loaded"] = opened.DeepClone();
        (await dani.PutAsJsonAsync($"/api/recipes/{id}", clash)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var kept = (await ReadAsync(await ana.GetAsync("/api/recipes"))).AsArray().ShouldHaveSingleItem()!;
        kept["ingredients"]!.AsArray().Select(i => i!["amount"]!.GetValue<double>()).ShouldBe([350d, 400d]);
        kept["cookedGrams"]!.GetValue<double>().ShouldBe(2000);
    }

    // The same two saves at the same instant: each read the recipe before the other wrote, so both
    // saw nothing changed since their sheets opened and the second wrote the first's change away.
    [Fact]
    public async Task TwoPeopleSavingOneRecipeAtOnce_EachChangeStands()
    {
        await using var server = await SugaryServer.StartAsync();
        var ana = server.As(SugaryServer.Her);
        var dani = server.As(SugaryServer.Him);
        var lentils = await FoodAsync(ana, Lentils());
        var potato = await FoodAsync(ana, Potato());

        foreach (var round in Enumerable.Range(1, 8))
        {
            var opened = Stew((lentils, 300), (potato, 400));
            var id = (await ReadAsync(await ana.PostAsJsonAsync("/api/recipes", opened)))["id"]!.GetValue<string>();
            opened["name"] = "Lentejas de la abuela";
            var anas = (JsonObject)opened.DeepClone();
            anas["ingredients"]![0]!["amount"] = 300 + round;
            anas["loaded"] = opened.DeepClone();
            var danis = (JsonObject)opened.DeepClone();
            danis["cookedGrams"] = 1800 + round;
            danis["loaded"] = opened.DeepClone();

            var answers = await Task.WhenAll(
                ana.PutAsJsonAsync($"/api/recipes/{id}", anas), dani.PutAsJsonAsync($"/api/recipes/{id}", danis));

            answers.Select(a => a.StatusCode).ShouldAllBe(status => status == HttpStatusCode.OK);
            var kept = (await ReadAsync(await ana.GetAsync("/api/recipes"))).AsArray()
                .Single(r => r!["id"]!.GetValue<string>() == id)!;
            kept["cookedGrams"]!.GetValue<double>().ShouldBe(1800 + round, $"round {round}");
            kept["ingredients"]!.AsArray().Select(i => i!["amount"]!.GetValue<double>())
                .ShouldBe([300d + round, 400d], $"round {round}");
        }
    }
}