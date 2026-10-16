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
        listed["mealId"].ShouldBeNull();
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
        { "long note", Dose(note: new string('x', 201)), "A note can be at most 200 characters." },
        { "unknown meal", With(Dose(), "mealId", Guid.NewGuid().ToString()), "There is no such meal to link the dose to." }
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

    private static JsonObject With(JsonObject dose, string field, string value)
    {
        dose[field] = value;
        return dose;
    }
}