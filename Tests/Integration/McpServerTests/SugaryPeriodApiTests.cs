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

// Her periods as the app records them: the day one started and the day it ended, or none while it
// is still going. Days on her calendar, not instants. Every refusal says why and stores nothing.
public class SugaryPeriodApiTests
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static JsonObject Period(string? start = "2026-09-03", string? end = "2026-09-07") => new()
    {
        ["start"] = start,
        ["end"] = end
    };

    // Days relative to today in the home's zone, for the rule about the future.
    private static string FromToday(int days) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Madrid).DateTime).AddDays(days)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static readonly TimeZoneInfo Madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonNode>(_json))!;

    private static async Task<JsonArray> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonNode>("/api/periods"))!.AsArray();

    private static string Day(JsonNode? period, string field) => period![field]!.GetValue<string>();

    [Fact]
    public async Task APeriod_IsStoredAndListedBack()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var created = await client.PostAsJsonAsync("/api/periods", Period());

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var saved = await ReadAsync(created);
        var listed = (await ListAsync(client)).Single()!;
        listed["id"]!.GetValue<string>().ShouldBe(saved["id"]!.GetValue<string>());
        Day(listed, "start").ShouldBe("2026-09-03");
        Day(listed, "end").ShouldBe("2026-09-07");
    }

    [Fact]
    public async Task Periods_AreListedNewestFirst()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        await client.PostAsJsonAsync("/api/periods", Period("2026-08-06", "2026-08-10"));
        await client.PostAsJsonAsync("/api/periods", Period("2026-09-03", "2026-09-07"));
        await client.PostAsJsonAsync("/api/periods", Period("2026-07-09", "2026-07-13"));

        (await ListAsync(client)).Select(p => Day(p, "start")).ShouldBe(["2026-09-03", "2026-08-06", "2026-07-09"]);
    }

    // Started today and ended days later: an ongoing period is one with no end, until a replace
    // gives it one.
    [Fact]
    public async Task AnOngoingPeriod_IsEndedLater()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var created = await client.PostAsJsonAsync("/api/periods", Period("2026-10-01", null));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await ReadAsync(created))["id"]!.GetValue<string>();
        (await ListAsync(client)).Single()!["end"].ShouldBeNull();

        var ended = await client.PutAsJsonAsync($"/api/periods/{id}", Period("2026-10-01", "2026-10-05"));

        ended.StatusCode.ShouldBe(HttpStatusCode.OK);
        Day((await ListAsync(client)).Single(), "end").ShouldBe("2026-10-05");
    }

    // A period up to tomorrow is kept: her phone can be a day ahead of the home's calendar.
    [Fact]
    public async Task TheBounds_AreKeptExactly()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        (await client.PostAsJsonAsync("/api/periods", Period("2026-01-01", "2026-01-31"))).StatusCode
            .ShouldBe(HttpStatusCode.Created);
        (await client.PostAsJsonAsync("/api/periods", Period("2026-03-04", "2026-03-04"))).StatusCode
            .ShouldBe(HttpStatusCode.Created);
        (await client.PostAsJsonAsync("/api/periods", Period(FromToday(1), null))).StatusCode
            .ShouldBe(HttpStatusCode.Created);

        (await ListAsync(client)).Count.ShouldBe(3);
    }

    public static TheoryData<string, JsonObject, string> Refusals => new()
    {
        { "no start", Period(start: null), "A period needs the day it started." },
        { "end before start", Period("2026-09-07", "2026-09-03"), "A period can't end before it starts." },
        { "starts in the future", Period(FromToday(2), null), "A period can't start or end after today." },
        { "ends in the future", Period(FromToday(-2), FromToday(2)), "A period can't start or end after today." },
        { "too long", Period("2026-01-01", "2026-02-01"), "A period can be at most 31 days." }
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ARefusedPeriod_SaysWhyAndStoresNothing(string _, JsonObject period, string sentence)
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var response = await client.PostAsJsonAsync("/api/periods", period);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadAsync(response))["detail"]!.GetValue<string>().ShouldBe(sentence);
        (await ListAsync(client)).ShouldBeEmpty();
    }

    // No day is in two periods, and an ongoing period reaches every day after its start, so a
    // later one cannot begin until it has ended.
    public static TheoryData<string, JsonObject, JsonObject, string> Overlaps => new()
    {
        {
            "shares a day", Period("2026-09-03", "2026-09-07"), Period("2026-09-07", "2026-09-09"),
            "It overlaps the period that started on 2026-09-03."
        },
        {
            "inside another", Period("2026-09-03", "2026-09-07"), Period("2026-09-04", "2026-09-05"),
            "It overlaps the period that started on 2026-09-03."
        },
        {
            "around another", Period("2026-09-03", "2026-09-07"), Period("2026-09-01", "2026-09-10"),
            "It overlaps the period that started on 2026-09-03."
        },
        {
            "after an ongoing one", Period("2026-09-03", null), Period("2026-10-01", "2026-10-04"),
            "The period that started on 2026-09-03 hasn't ended. End it first."
        },
        {
            "ongoing over a later one", Period("2026-09-03", "2026-09-07"), Period("2026-08-30", null),
            "It overlaps the period that started on 2026-09-03."
        }
    };

    [Theory]
    [MemberData(nameof(Overlaps))]
    public async Task AnOverlappingPeriod_IsRefused(string _, JsonObject first, JsonObject second, string sentence)
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        (await client.PostAsJsonAsync("/api/periods", first)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var response = await client.PostAsJsonAsync("/api/periods", second);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadAsync(response))["detail"]!.GetValue<string>().ShouldBe(sentence);
        (await ListAsync(client)).Count.ShouldBe(1);
    }

    // A period replaced with its own dates moved a little does not overlap itself.
    [Fact]
    public async Task APeriod_IsNotRefusedForOverlappingItself()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var id = (await ReadAsync(await client.PostAsJsonAsync("/api/periods", Period())))["id"]!.GetValue<string>();

        (await client.PutAsJsonAsync($"/api/periods/{id}", Period("2026-09-02", "2026-09-06"))).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        Day((await ListAsync(client)).Single(), "start").ShouldBe("2026-09-02");
    }

    [Fact]
    public async Task APeriod_CanBeDeleted()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var id = (await ReadAsync(await client.PostAsJsonAsync("/api/periods", Period())))["id"]!.GetValue<string>();

        (await client.DeleteAsync($"/api/periods/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ListAsync(client)).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ARefusedReplace_SaysWhyAndLeavesThePeriodAsItWas(string _, JsonObject period, string sentence)
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var id = (await ReadAsync(await client.PostAsJsonAsync("/api/periods", Period())))["id"]!.GetValue<string>();

        var response = await client.PutAsJsonAsync($"/api/periods/{id}", period);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadAsync(response))["detail"]!.GetValue<string>().ShouldBe(sentence);
        var kept = (await ListAsync(client)).Single();
        Day(kept, "start").ShouldBe("2026-09-03");
        Day(kept, "end").ShouldBe("2026-09-07");
    }

    [Fact]
    public async Task AnUnknownPeriod_IsNotFound()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        (await client.PutAsJsonAsync($"/api/periods/{Guid.NewGuid()}", Period())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.DeleteAsync($"/api/periods/{Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // An existing log upgrades in place: the server opens a database made before periods existed,
    // gains the table and loses no meal and no dose.
    [Fact]
    public async Task ALogFromBeforePeriods_GainsThemOnStart_AndKeepsItsMealsAndDoses()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sugary-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sugary.db");
        var mealId = Guid.NewGuid();
        await using (var db = new SugaryDbContext(
                         new DbContextOptionsBuilder<SugaryDbContext>().UseSqlite($"Data Source={path}").Options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261016103306_Doses");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Meals (Id, EatenAt, Outcome, CarbsLow, Carbs, CarbsHigh) "
                + "VALUES ({0}, '2026-10-01 12:00:00', 'Unknown', 40, 50, 60)",
                mealId.ToString().ToUpperInvariant());
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Doses (Id, TakenAt, Units, Kind) VALUES ({0}, '2026-10-01 12:00:00', 6, 'Rapid')",
                Guid.NewGuid().ToString().ToUpperInvariant());
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
            (await client.GetFromJsonAsync<JsonNode>("/api/doses"))!.AsArray().Count.ShouldBe(1);
            (await client.PostAsJsonAsync("/api/periods", Period())).StatusCode.ShouldBe(HttpStatusCode.Created);
            (await ListAsync(client)).Count.ShouldBe(1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}