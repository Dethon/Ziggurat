using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using McpServerSugary.Store;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// Each person keeps a Sugary log of their own while the household shares its kitchen: the app's API
// acts for whoever the proxy's session check named in its headers, so one person's meals, doses,
// pens, cycle, notes and exercise are nobody else's, and a food or recipe one of them adds is there
// for everyone in the household and for nobody outside it.
public class SugaryPeopleApiTests
{
    private static JsonObject Meal(string notes) => new()
    {
        ["eatenAt"] = "2026-10-11T13:30:00+02:00",
        ["notes"] = notes,
        ["outcome"] = "unknown",
        ["items"] = new JsonArray(new JsonObject
        {
            ["name"] = "rice",
            ["grams"] = 150,
            ["carbsPer100g"] = 28,
            ["carbsLow"] = 32,
            ["carbs"] = 42,
            ["carbsHigh"] = 52,
            ["source"] = "estimate"
        })
    };

    private static async Task<string> IdOf(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<string>();
    }

    [Fact]
    public async Task HerMeals_AreNotListedForHim()
    {
        await using var server = await SugaryServer.StartAsync();
        var her = server.As(SugaryServer.Her);
        var him = server.As(SugaryServer.Him);

        await IdOf(await her.PostAsJsonAsync("/api/meals", Meal("her paella")));

        (await him.GetFromJsonAsync<JsonArray>("/api/meals"))!.ShouldBeEmpty();
        (await her.GetFromJsonAsync<JsonArray>("/api/meals"))!.Single()!["notes"]!.GetValue<string>()
            .ShouldBe("her paella");
    }

    // Knowing a meal's id opens nothing: to anyone else it is a meal that does not exist.
    [Fact]
    public async Task HerMeal_CannotBeReadChangedOrDeletedByHim()
    {
        await using var server = await SugaryServer.StartAsync();
        var her = server.As(SugaryServer.Her);
        var him = server.As(SugaryServer.Him);
        var meal = Meal("her paella");
        meal["photo"] = "data:image/jpeg;base64,/9j/4AAQSkZJRg==";
        var id = await IdOf(await her.PostAsJsonAsync("/api/meals", meal));

        (await him.GetAsync($"/api/meals/{id}/photo")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await him.PutAsJsonAsync($"/api/meals/{id}", Meal("his now"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await him.DeleteAsync($"/api/meals/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await her.GetAsync($"/api/meals/{id}/photo")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await her.GetFromJsonAsync<JsonArray>("/api/meals"))!.Single()!["notes"]!.GetValue<string>()
            .ShouldBe("her paella");
    }

    [Fact]
    public async Task HerDoses_AreHersAlone_AndHisCannotBeForHerMeal()
    {
        await using var server = await SugaryServer.StartAsync();
        var her = server.As(SugaryServer.Her);
        var him = server.As(SugaryServer.Him);
        var meal = await IdOf(await her.PostAsJsonAsync("/api/meals", Meal("her paella")));
        var dose = await IdOf(await her.PostAsJsonAsync("/api/doses", Dose(meal)));

        (await him.GetFromJsonAsync<JsonArray>("/api/doses"))!.ShouldBeEmpty();
        (await him.PutAsJsonAsync($"/api/doses/{dose}", Dose(null))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await him.DeleteAsync($"/api/doses/{dose}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await him.PostAsJsonAsync("/api/doses", Dose(meal))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        (await her.GetFromJsonAsync<JsonArray>("/api/doses"))!.Single()!["id"]!.GetValue<string>().ShouldBe(dose);
    }

    // A pen of hers is open while his of the same kind is too: one open pen of a kind is a rule of
    // one person's pens.
    [Fact]
    public async Task EachPerson_KeepsTheirOwnPens()
    {
        await using var server = await SugaryServer.StartAsync();
        var her = server.As(SugaryServer.Her);
        var him = server.As(SugaryServer.Him);

        (await her.PostAsJsonAsync("/api/pens", Pen("Fiasp", "2026-10-01"))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await him.PostAsJsonAsync("/api/pens", Pen("Humalog", "2026-09-01"))).StatusCode.ShouldBe(HttpStatusCode.Created);

        (await her.GetFromJsonAsync<JsonArray>("/api/pens"))!.Single()!["insulin"]!.GetValue<string>().ShouldBe("Fiasp");
        var his = (await him.GetFromJsonAsync<JsonArray>("/api/pens"))!.Single()!;
        his["insulin"]!.GetValue<string>().ShouldBe("Humalog");
        his["replacedOn"].ShouldBeNull();
    }

    [Fact]
    public async Task EachPerson_KeepsTheirOwnCycleNotesAndExercise()
    {
        await using var server = await SugaryServer.StartAsync();
        var her = server.As(SugaryServer.Her);
        var him = server.As(SugaryServer.Him);

        (await her.PostAsJsonAsync("/api/periods", new { start = "2026-10-01", end = "2026-10-05" })).StatusCode
            .ShouldBe(HttpStatusCode.Created);
        (await her.PutAsJsonAsync("/api/day-notes/2026-10-02", new { text = "her day" })).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        (await him.PutAsJsonAsync("/api/day-notes/2026-10-02", new { text = "his day" })).StatusCode
            .ShouldBe(HttpStatusCode.OK);
        (await her.PostAsJsonAsync("/api/exercise", new
        {
            kind = "walk",
            startedAt = "2026-10-02T09:00:00+02:00",
            endedAt = "2026-10-02T10:00:00+02:00"
        })).StatusCode.ShouldBe(HttpStatusCode.Created);

        (await him.GetFromJsonAsync<JsonArray>("/api/periods"))!.ShouldBeEmpty();
        (await him.GetFromJsonAsync<JsonArray>("/api/exercise"))!.ShouldBeEmpty();
        (await him.GetFromJsonAsync<JsonArray>("/api/day-notes"))!.Single()!["text"]!.GetValue<string>()
            .ShouldBe("his day");
        (await her.GetFromJsonAsync<JsonArray>("/api/day-notes"))!.Single()!["text"]!.GetValue<string>()
            .ShouldBe("her day");
        (await her.GetFromJsonAsync<JsonArray>("/api/periods"))!.ShouldHaveSingleItem();
    }

    // The downloads are the downloader's log; the kitchen in them is the household's.
    [Fact]
    public async Task HisDownloads_CarryNoneOfHerLog()
    {
        await using var server = await SugaryServer.StartAsync();
        var her = server.As(SugaryServer.Her);
        var him = server.As(SugaryServer.Him);
        await IdOf(await her.PostAsJsonAsync("/api/meals", Meal("her paella")));
        await IdOf(await her.PostAsJsonAsync("/api/foods", Food("Lentejas")));

        var meals = await him.GetStringAsync("/api/export/meals.csv");
        var foods = await him.GetStringAsync("/api/export/foods.csv");

        meals.ShouldNotContain("her paella");
        foods.ShouldContain("Lentejas");
        (await her.GetStringAsync("/api/export/meals.csv")).ShouldContain("her paella");
    }

    // A recipe cooked for everyone is entered once: the household's foods and recipes are everyone's
    // in it, and his meal can be a helping of the pot she wrote down.
    [Fact]
    public async Task ARecipeSheAdds_IsUsableByHim()
    {
        await using var server = await SugaryServer.StartAsync();
        var her = server.As(SugaryServer.Her);
        var him = server.As(SugaryServer.Him);
        var lentils = await IdOf(await her.PostAsJsonAsync("/api/foods", Food("Lentejas")));
        var stew = await IdOf(await her.PostAsJsonAsync("/api/recipes", Stew(lentils)));

        (await him.GetFromJsonAsync<JsonArray>("/api/foods"))!.Single()!["id"]!.GetValue<string>().ShouldBe(lentils);
        (await him.GetFromJsonAsync<JsonArray>("/api/recipes"))!.Single()!["id"]!.GetValue<string>().ShouldBe(stew);
        var helping = Meal("a helping of her stew");
        helping["items"]![0]!["recipeId"] = stew;
        await IdOf(await him.PostAsJsonAsync("/api/meals", helping));

        var recipe = (await him.GetFromJsonAsync<JsonArray>("/api/recipes"))!.Single()!;
        recipe["uses"]!.GetValue<int>().ShouldBe(1, "his helping is his use of the household's recipe");
        (await her.GetFromJsonAsync<JsonArray>("/api/recipes"))!.Single()!["uses"]!.GetValue<int>()
            .ShouldBe(0, "how often each has it is their own log's");
    }

    [Fact]
    public async Task AnotherHouseholdsKitchen_IsNotThisOnes()
    {
        await using var server = await SugaryServer.StartAsync();
        var her = server.As(SugaryServer.Her);
        var neighbour = server.As(SugaryServer.Neighbour);
        var lentils = await IdOf(await her.PostAsJsonAsync("/api/foods", Food("Lentejas", "8410188012092")));
        var stew = await IdOf(await her.PostAsJsonAsync("/api/recipes", Stew(lentils)));

        (await neighbour.GetFromJsonAsync<JsonArray>("/api/foods"))!.ShouldBeEmpty();
        (await neighbour.GetFromJsonAsync<JsonArray>("/api/recipes"))!.ShouldBeEmpty();
        (await neighbour.PutAsJsonAsync($"/api/foods/{lentils}", Food("Mine now"))).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await neighbour.DeleteAsync($"/api/recipes/{stew}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await neighbour.PostAsJsonAsync("/api/recipes", Stew(lentils))).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest, "a recipe is made of its own household's foods");
        // Her label for the barcode is her household's; the neighbour's scan goes to the catalogue,
        // which is not reachable from a test.
        (await neighbour.GetAsync("/api/products/8410188012092")).StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await her.GetFromJsonAsync<JsonNode>("/api/products/8410188012092"))!["name"]!.GetValue<string>()
            .ShouldBe("Lentejas");
    }

    // The log from before it was per person upgrades in place and loses nothing, but none of it is
    // anybody's until the migration into the home household gives it to the person whose log it was:
    // no request and no turn sees it, hers included.
    [Fact]
    public async Task ALogFromBeforePeople_IsKeptWhole_AndIsNobodysUntilItIsGiven()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sugary-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sugary.db");
        await using (var db = new SugaryDbContext(
                         new DbContextOptionsBuilder<SugaryDbContext>().UseSqlite($"Data Source={path}").Options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261023150911_PushSubscriptions");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Meals (Id, EatenAt, Outcome, CarbsLow, Carbs, CarbsHigh, Notes) "
                + "VALUES ({0}, '2026-10-01 12:00:00', 'Unknown', 40, 50, 60, 'before people')",
                Guid.NewGuid().ToString().ToUpperInvariant());
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Foods (Id, Name, Unit, Source, CreatedAt) VALUES ({0}, 'Lentejas', 'g', 'Manual', '2026-10-01 12:00:00')",
                Guid.NewGuid().ToString().ToUpperInvariant());
            await db.Database.ExecuteSqlRawAsync("INSERT INTO DayNotes (Day, Text) VALUES ('2026-10-01', 'a day before')");
        }

        SqliteConnection.ClearAllPools();

        try
        {
            await using var server = await SugaryServer.StartAsync(configure: settings => settings with
            {
                DatabasePath = path
            });
            var her = server.As(SugaryServer.Her);

            (await her.GetFromJsonAsync<JsonArray>("/api/meals"))!.ShouldBeEmpty();
            (await her.GetFromJsonAsync<JsonArray>("/api/foods"))!.ShouldBeEmpty();
            (await her.GetFromJsonAsync<JsonArray>("/api/day-notes"))!.ShouldBeEmpty();
            (await server.QueryAsync("select count(*) from meals")).Single()![0]!.GetValue<int>().ShouldBe(0);

            await server.GiveUnassignedRowsToAsync(SugaryServer.Her);

            (await her.GetFromJsonAsync<JsonArray>("/api/meals"))!.Single()!["notes"]!.GetValue<string>()
                .ShouldBe("before people");
            (await her.GetFromJsonAsync<JsonArray>("/api/foods"))!.Single()!["name"]!.GetValue<string>()
                .ShouldBe("Lentejas");
            (await her.GetFromJsonAsync<JsonArray>("/api/day-notes"))!.Single()!["text"]!.GetValue<string>()
                .ShouldBe("a day before");
            (await server.As(SugaryServer.Him).GetFromJsonAsync<JsonArray>("/api/meals"))!.ShouldBeEmpty();
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static JsonObject Dose(string? meal) => new()
    {
        ["takenAt"] = "2026-10-11T13:30:00+02:00",
        ["units"] = 6,
        ["kind"] = "rapid",
        ["mealIds"] = meal is null ? new JsonArray() : new JsonArray(JsonValue.Create(meal))
    };

    private static JsonObject Pen(string insulin, string openedOn) => new()
    {
        ["kind"] = "rapid",
        ["insulin"] = insulin,
        ["openedOn"] = openedOn,
        ["days"] = 28
    };

    private static JsonObject Food(string name, string? barcode = null) => new()
    {
        ["name"] = name,
        ["carbsPer100g"] = 48,
        ["unit"] = "g",
        ["barcode"] = barcode,
        ["source"] = "manual"
    };

    private static JsonObject Stew(string food) => new()
    {
        ["name"] = "Lentejas de la abuela",
        ["cookedGrams"] = 1800,
        ["ingredients"] = new JsonArray(new JsonObject { ["foodId"] = food, ["amount"] = 300 })
    };
}