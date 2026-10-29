using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpServerSugary.Store;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// Her insulin doses as the app records them: a time, a number of units and a kind, kept beside her
// meals. A dose is a fact of what she injected, so every refusal says why and stores nothing.
public class SugaryDoseApiTests
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static JsonObject Dose(
        string takenAt = "2026-10-11T22:00:00+02:00", double units = 14, string kind = "basal",
        string? note = null) => new()
        {
            ["takenAt"] = takenAt,
            ["units"] = units,
            ["kind"] = kind,
            ["note"] = note
        };

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonNode>(_json))!;

    private static async Task<JsonArray> ListAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<JsonNode>($"/api/doses{query}"))!.AsArray();

    [Fact]
    public async Task ADose_IsStoredAndListedBack()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var created = await client.PostAsJsonAsync("/api/doses", Dose(units: 4.5, kind: "rapid", note: " correction "));

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var saved = await ReadAsync(created);
        var listed = (await ListAsync(client)).Single()!;
        listed["id"]!.GetValue<string>().ShouldBe(saved["id"]!.GetValue<string>());
        listed["units"]!.GetValue<double>().ShouldBe(4.5);
        listed["kind"]!.GetValue<string>().ShouldBe("rapid");
        listed["note"]!.GetValue<string>().ShouldBe("correction");
        listed["mealIds"]!.AsArray().ShouldBeEmpty();
        DateTimeOffset.Parse(listed["takenAt"]!.GetValue<string>(), CultureInfo.InvariantCulture)
            .ShouldBe(DateTimeOffset.Parse("2026-10-11T20:00:00Z", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Doses_AreListedNewestFirst_AndPaged()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        await client.PostAsJsonAsync("/api/doses", Dose("2026-10-10T09:00:00Z", units: 1));
        await client.PostAsJsonAsync("/api/doses", Dose("2026-10-12T09:00:00Z", units: 3));
        await client.PostAsJsonAsync("/api/doses", Dose("2026-10-11T09:00:00Z", units: 2));

        (await ListAsync(client)).Select(d => d!["units"]!.GetValue<double>()).ShouldBe([3, 2, 1]);
        (await ListAsync(client, "?skip=1&take=1")).Select(d => d!["units"]!.GetValue<double>()).ShouldBe([2]);
    }

    // The Log asks for the days it shows; from is inclusive and to is not, as the downloads are.
    [Fact]
    public async Task Doses_CanBeListedForAPeriod()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        await client.PostAsJsonAsync("/api/doses", Dose("2026-10-10T09:00:00Z", units: 1));
        await client.PostAsJsonAsync("/api/doses", Dose("2026-10-11T09:00:00Z", units: 2));
        await client.PostAsJsonAsync("/api/doses", Dose("2026-10-12T09:00:00Z", units: 3));

        var from = Uri.EscapeDataString("2026-10-11T09:00:00Z");
        var to = Uri.EscapeDataString("2026-10-12T09:00:00Z");

        (await ListAsync(client, $"?from={from}&to={to}")).Select(d => d!["units"]!.GetValue<double>()).ShouldBe([2]);
        (await ListAsync(client, $"?from={from}")).Select(d => d!["units"]!.GetValue<double>()).ShouldBe([3, 2]);
    }

    public static TheoryData<string, JsonObject, string> Refusals => new()
    {
        { "no units", Without(Dose(), "units"), "A dose needs a number of units above 0." },
        { "zero", Dose(units: 0), "A dose needs a number of units above 0." },
        { "negative", Dose(units: -2), "A dose needs a number of units above 0." },
        { "over 100", Dose(units: 100.5), "A dose can be at most 100 units." },
        { "three decimals", Dose(units: 1.125), "Units can have at most two decimals." },
        { "unknown kind", Dose(kind: "mixed"), "A dose is rapid or basal." },
        { "no kind", Without(Dose(), "kind"), "A dose is rapid or basal." },
        { "no time", Without(Dose(), "takenAt"), "A dose needs the time it was taken." },
        { "long note", Dose(note: new string('x', 201)), "A note can be at most 200 characters." },
        { "unknown meal", WithMeals(Dose(), Guid.NewGuid().ToString()), "There is no such meal to link the dose to." },
        { "unknown meal, sent the old way", With(Dose(), "mealId", Guid.NewGuid().ToString()), "There is no such meal to link the dose to." }
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ARefusedDose_SaysWhyAndStoresNothing(string _, JsonObject dose, string sentence)
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var response = await client.PostAsJsonAsync("/api/doses", dose);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadAsync(response))["detail"]!.GetValue<string>().ShouldBe(sentence);
        (await ListAsync(client)).ShouldBeEmpty();
    }

    // The bounds refuse; they never round or clamp what she typed into something she did not take.
    [Fact]
    public async Task TheBounds_AreKeptExactly()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        (await client.PostAsJsonAsync("/api/doses", Dose(units: 100))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await client.PostAsJsonAsync("/api/doses", Dose(units: 0.05))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await client.PostAsJsonAsync("/api/doses", Dose(note: new string('x', 200)))).StatusCode
            .ShouldBe(HttpStatusCode.Created);

        (await ListAsync(client)).Select(d => d!["units"]!.GetValue<double>()).Order().ShouldBe([0.05, 14, 100]);
    }

    [Fact]
    public async Task ADose_CanBeReplacedAndDeleted()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var id = (await ReadAsync(await client.PostAsJsonAsync("/api/doses", Dose())))["id"]!.GetValue<string>();

        var replaced = await client.PutAsJsonAsync(
            $"/api/doses/{id}", Dose("2026-10-12T08:15:00+02:00", units: 6.5, kind: "rapid", note: "pre-bolus"));

        replaced.StatusCode.ShouldBe(HttpStatusCode.OK);
        var listed = (await ListAsync(client)).Single()!;
        listed["id"]!.GetValue<string>().ShouldBe(id);
        listed["units"]!.GetValue<double>().ShouldBe(6.5);
        listed["kind"]!.GetValue<string>().ShouldBe("rapid");
        listed["note"]!.GetValue<string>().ShouldBe("pre-bolus");
        DateTimeOffset.Parse(listed["takenAt"]!.GetValue<string>(), CultureInfo.InvariantCulture)
            .ShouldBe(DateTimeOffset.Parse("2026-10-12T06:15:00Z", CultureInfo.InvariantCulture));

        (await client.DeleteAsync($"/api/doses/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await ListAsync(client)).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ARefusedReplace_SaysWhyAndLeavesTheDoseAsItWas(string _, JsonObject dose, string sentence)
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var id = (await ReadAsync(await client.PostAsJsonAsync("/api/doses", Dose(note: "night"))))["id"]!
            .GetValue<string>();

        var response = await client.PutAsJsonAsync($"/api/doses/{id}", dose);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadAsync(response))["detail"]!.GetValue<string>().ShouldBe(sentence);
        var kept = (await ListAsync(client)).Single()!;
        kept["units"]!.GetValue<double>().ShouldBe(14);
        kept["kind"]!.GetValue<string>().ShouldBe("basal");
        kept["note"]!.GetValue<string>().ShouldBe("night");
    }

    [Fact]
    public async Task AnUnknownDose_IsNotFound()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        (await client.PutAsJsonAsync($"/api/doses/{Guid.NewGuid()}", Dose())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.DeleteAsync($"/api/doses/{Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // One bolus for a meal and the dessert logged after it: the dose is linked to both, each once,
    // in the order she picked them.
    [Fact]
    public async Task ADose_CanBeForSeveralMeals()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var lunch = await MealAsync(client, "2026-10-11T13:30:00+02:00");
        var dessert = await MealAsync(client, "2026-10-11T13:50:00+02:00");

        var created = await client.PostAsJsonAsync("/api/doses", WithMeals(Dose(kind: "rapid"), dessert, lunch, dessert));

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ListAsync(client)).Single()!["mealIds"]!.AsArray().Select(id => id!.GetValue<string>())
            .ShouldBe([dessert, lunch]);
        var id = (await ReadAsync(created))["id"]!.GetValue<string>();
        await client.PutAsJsonAsync($"/api/doses/{id}", WithMeals(Dose(kind: "rapid"), lunch));
        (await ListAsync(client)).Single()!["mealIds"]!.AsArray().Select(m => m!.GetValue<string>()).ShouldBe([lunch]);
    }

    // An installed app from before a dose could be for several meals still sends the one it picked,
    // and that link is kept rather than dropped.
    [Fact]
    public async Task AnOlderApp_LinkingOneMeal_IsStillHeard()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var lunch = await MealAsync(client, "2026-10-11T13:30:00+02:00");

        (await client.PostAsJsonAsync("/api/doses", With(Dose(kind: "rapid"), "mealId", lunch))).StatusCode
            .ShouldBe(HttpStatusCode.Created);

        (await ListAsync(client)).Single()!["mealIds"]!.AsArray().Select(id => id!.GetValue<string>()).ShouldBe([lunch]);
    }

    // A log from before a dose could be for several meals keeps every link it had.
    [Fact]
    public async Task ALogWithLinkedDoses_KeepsEachDosesMeal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sugary-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sugary.db");
        var mealId = Guid.NewGuid();
        await using (var db = new SugaryDbContext(
                         new DbContextOptionsBuilder<SugaryDbContext>().UseSqlite($"Data Source={path}").Options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261017102454_Exercise");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Meals (Id, EatenAt, Outcome, CarbsLow, Carbs, CarbsHigh) "
                + "VALUES ({0}, '2026-10-01 12:00:00', 'Unknown', 40, 50, 60)",
                mealId.ToString().ToUpperInvariant());
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Doses (Id, TakenAt, Units, Kind, MealId) VALUES ({0}, '2026-10-01 12:00:00', 6, 'Rapid', {1}), "
                + "({2}, '2026-10-01 22:00:00', 14, 'Basal', NULL)",
                Guid.NewGuid().ToString().ToUpperInvariant(), mealId.ToString().ToUpperInvariant(),
                Guid.NewGuid().ToString().ToUpperInvariant());
        }

        SqliteConnection.ClearAllPools();

        try
        {
            await using var server = await SugaryServer.StartAsync(configure: settings => settings with
            {
                DatabasePath = path
            });
            // Kept through every migration, and nobody's until given to the person whose log it was.
            await server.GiveUnassignedRowsToAsync(SugaryServer.Her);
            var client = await server.SignedInAsync();

            var doses = await ListAsync(client);
            doses.Select(d => d!["mealIds"]!.AsArray().Select(id => id!.GetValue<string>()).ToList())
                .ShouldBe([[], [mealId.ToString()]]);
            var meal = (await client.GetFromJsonAsync<JsonNode>("/api/meals"))!.AsArray().Single()!;
            meal["doses"]!.AsArray().Single()!["units"]!.GetValue<double>().ShouldBe(6);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    // An existing log upgrades in place: the server opens a database made before doses existed,
    // gains the table and loses no meal.
    [Fact]
    public async Task ALogFromBeforeDoses_GainsThemOnStart_AndKeepsItsMeals()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sugary-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sugary.db");
        var mealId = Guid.NewGuid();
        await using (var db = new SugaryDbContext(
                         new DbContextOptionsBuilder<SugaryDbContext>().UseSqlite($"Data Source={path}").Options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261014205137_Initial");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Meals (Id, EatenAt, Outcome, CarbsLow, Carbs, CarbsHigh) "
                + "VALUES ({0}, '2026-10-01 12:00:00', 'Unknown', 40, 50, 60)",
                mealId.ToString().ToUpperInvariant());
        }

        SqliteConnection.ClearAllPools();

        try
        {
            await using var server = await SugaryServer.StartAsync(configure: settings => settings with
            {
                DatabasePath = path
            });
            // Kept through every migration, and nobody's until given to the person whose log it was.
            await server.GiveUnassignedRowsToAsync(SugaryServer.Her);
            var client = await server.SignedInAsync();

            var meals = (await client.GetFromJsonAsync<JsonNode>("/api/meals"))!.AsArray();
            meals.Single()!["id"]!.GetValue<string>().ShouldBe(mealId.ToString());
            (await client.PostAsJsonAsync("/api/doses", Dose())).StatusCode.ShouldBe(HttpStatusCode.Created);
            (await ListAsync(client)).Count.ShouldBe(1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static JsonObject Without(JsonObject dose, string field)
    {
        dose.Remove(field);
        return dose;
    }

    private static JsonObject WithMeals(JsonObject dose, params string[] mealIds)
    {
        dose["mealIds"] = new JsonArray([.. mealIds.Select(id => JsonValue.Create(id))]);
        return dose;
    }

    private static async Task<string> MealAsync(HttpClient client, string eatenAt) =>
        (await ReadAsync(await client.PostAsJsonAsync("/api/meals", new JsonObject
        {
            ["eatenAt"] = eatenAt,
            ["outcome"] = "unknown",
            ["items"] = new JsonArray(new JsonObject
            {
                ["name"] = "rice", ["carbsLow"] = 30, ["carbs"] = 40, ["carbsHigh"] = 50, ["source"] = "manual"
            })
        })))["id"]!.GetValue<string>();

    private static JsonObject With(JsonObject dose, string field, string value)
    {
        dose[field] = value;
        return dose;
    }
}