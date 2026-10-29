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

// Her exercise as the app records it: a kind, when it started and when it ended, or no end while it
// is running. Every refusal says why and stores nothing.
public class SugaryExerciseApiTests
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static JsonObject Session(
        string kind = "walk", string? startedAt = "2026-10-11T10:00:00+02:00",
        string? endedAt = "2026-10-11T10:45:00+02:00", string? name = null, string? note = null) => new()
        {
            ["kind"] = kind,
            ["name"] = name,
            ["startedAt"] = startedAt,
            ["endedAt"] = endedAt,
            ["note"] = note
        };

    // A time relative to now, for the rules about the future and about a running session. The refusals
    // are built when the tests are found, not when they run, so a time meant to be in the future is
    // an hour ahead: a long run must not catch up with it inside the server's few minutes of slack.
    private static string Ago(TimeSpan span) =>
        DateTimeOffset.UtcNow.Subtract(span).ToString("O", CultureInfo.InvariantCulture);

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonNode>(_json))!;

    private static async Task<JsonArray> ListAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<JsonNode>($"/api/exercise{query}"))!.AsArray();

    private static DateTimeOffset Instant(JsonNode? session, string field) =>
        DateTimeOffset.Parse(session![field]!.GetValue<string>(), CultureInfo.InvariantCulture);

    [Fact]
    public async Task ASession_IsStoredAndListedBack()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var created = await client.PostAsJsonAsync(
            "/api/exercise", Session("other", name: " bici ", note: " con Ana "));

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var saved = await ReadAsync(created);
        var listed = (await ListAsync(client)).Single()!;
        listed["id"]!.GetValue<string>().ShouldBe(saved["id"]!.GetValue<string>());
        listed["kind"]!.GetValue<string>().ShouldBe("other");
        listed["name"]!.GetValue<string>().ShouldBe("bici");
        listed["note"]!.GetValue<string>().ShouldBe("con Ana");
        Instant(listed, "startedAt").ShouldBe(DateTimeOffset.Parse("2026-10-11T08:00:00Z", CultureInfo.InvariantCulture));
        Instant(listed, "endedAt").ShouldBe(DateTimeOffset.Parse("2026-10-11T08:45:00Z", CultureInfo.InvariantCulture));
    }

    // A walk or the gym is what it says; a name sent with one is not kept.
    [Fact]
    public async Task AWalk_KeepsNoName()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        await client.PostAsJsonAsync("/api/exercise", Session("gym", name: "pierna"));

        (await ListAsync(client)).Single()!["name"].ShouldBeNull();
    }

    // Tap to start: a session with no end is running, and the same session sent again with an end
    // has been stopped.
    [Fact]
    public async Task ARunningSession_IsListedWithNoEnd_AndStoppedByItsEnd()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var started = Ago(TimeSpan.FromMinutes(30));

        var created = await client.PostAsJsonAsync("/api/exercise", Session(startedAt: started, endedAt: null));
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await ListAsync(client)).Single()!["endedAt"].ShouldBeNull();

        var id = (await ReadAsync(created))["id"]!.GetValue<string>();
        var stopped = Ago(TimeSpan.Zero);
        (await client.PutAsJsonAsync($"/api/exercise/{id}", Session(startedAt: started, endedAt: stopped))).StatusCode
            .ShouldBe(HttpStatusCode.OK);

        Instant((await ListAsync(client)).Single(), "endedAt")
            .ShouldBe(DateTimeOffset.Parse(stopped, CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Sessions_AreListedNewestFirst_PagedAndForAPeriod()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        await client.PostAsJsonAsync("/api/exercise", Session("walk", "2026-10-10T09:00:00Z", "2026-10-10T09:30:00Z"));
        await client.PostAsJsonAsync("/api/exercise", Session("gym", "2026-10-12T09:00:00Z", "2026-10-12T10:00:00Z"));
        await client.PostAsJsonAsync("/api/exercise", Session("other", "2026-10-11T09:00:00Z", "2026-10-11T09:20:00Z", "bici"));

        (await ListAsync(client)).Select(s => s!["kind"]!.GetValue<string>()).ShouldBe(["gym", "other", "walk"]);
        (await ListAsync(client, "?skip=1&take=1")).Select(s => s!["kind"]!.GetValue<string>()).ShouldBe(["other"]);
        (await ListAsync(client, "?from=2026-10-11T00:00:00Z&to=2026-10-12T09:00:00Z"))
            .Select(s => s!["kind"]!.GetValue<string>()).ShouldBe(["other"]);
    }

    // Twelve hours to the second is a session; a hike is long, a forgotten stop is longer.
    [Fact]
    public async Task TwelveHours_IsKept()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        (await client.PostAsJsonAsync("/api/exercise", Session(
                startedAt: "2026-10-11T06:00:00Z", endedAt: "2026-10-11T18:00:00Z"))).StatusCode
            .ShouldBe(HttpStatusCode.Created);
    }

    public static TheoryData<string, JsonObject, string> Refusals => new()
    {
        { "no start", Session(startedAt: null), "A session needs the time it started." },
        { "an unknown kind", Session("swim"), "A session is a walk, the gym or another kind." },
        { "other with no name", Session("other", name: "  "), "Another kind of exercise needs a name." },
        { "a name too long", Session("other", name: new string('a', 41)), "A name can be at most 40 characters." },
        { "a note too long", Session(note: new string('a', 201)), "A note can be at most 200 characters." },
        {
            "an end before its start", Session(startedAt: "2026-10-11T10:00:00Z", endedAt: "2026-10-11T09:59:00Z"),
            "A session has to end after it starts."
        },
        {
            "an end at its start", Session(startedAt: "2026-10-11T10:00:00Z", endedAt: "2026-10-11T10:00:00Z"),
            "A session has to end after it starts."
        },
        {
            "over twelve hours", Session(startedAt: "2026-10-11T06:00:00Z", endedAt: "2026-10-11T18:00:01Z"),
            "A session can be at most 12 hours."
        },
        {
            "a start in the future", Session(startedAt: Ago(TimeSpan.FromHours(-1)), endedAt: null),
            "A session can't start or end in the future."
        },
        {
            "an end in the future",
            Session(startedAt: Ago(TimeSpan.FromMinutes(20)), endedAt: Ago(TimeSpan.FromHours(-1))),
            "A session can't start or end in the future."
        }
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ARefusedSession_SaysWhyAndStoresNothing(string _, JsonObject session, string sentence)
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var response = await client.PostAsJsonAsync("/api/exercise", session);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadAsync(response))["detail"]!.GetValue<string>().ShouldBe(sentence);
        (await ListAsync(client)).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ARefusedReplace_SaysWhyAndLeavesTheSessionAsItWas(string _, JsonObject session, string sentence)
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var id = (await ReadAsync(await client.PostAsJsonAsync("/api/exercise", Session())))["id"]!.GetValue<string>();

        var response = await client.PutAsJsonAsync($"/api/exercise/{id}", session);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ReadAsync(response))["detail"]!.GetValue<string>().ShouldBe(sentence);
        var kept = (await ListAsync(client)).Single()!;
        kept["kind"]!.GetValue<string>().ShouldBe("walk");
        Instant(kept, "endedAt").ShouldBe(DateTimeOffset.Parse("2026-10-11T08:45:00Z", CultureInfo.InvariantCulture));
    }

    // Only one session runs at a time. The app never offers a second, so this is a second phone,
    // told which session is running rather than having it stopped for her.
    [Fact]
    public async Task ASecondRunningSession_IsRefused_NamingTheOneRunning()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var started = DateTimeOffset.UtcNow.AddMinutes(-30);
        await client.PostAsJsonAsync("/api/exercise", Session(
            startedAt: started.ToString("O", CultureInfo.InvariantCulture), endedAt: null));

        var response = await client.PostAsJsonAsync("/api/exercise", Session("gym", Ago(TimeSpan.Zero), null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var home = TimeZoneInfo.ConvertTime(started, TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid"));
        (await ReadAsync(response))["detail"]!.GetValue<string>().ShouldBe(
            $"Another session has been running since {home:yyyy-MM-dd HH:mm}. Stop it first.");
        (await ListAsync(client)).Count.ShouldBe(1);
    }

    // A session she adds afterwards, or the running one stopped, is not a second running session.
    [Fact]
    public async Task AFinishedSession_OrTheRunningOneItself_IsNotRefusedWhileOneRuns()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var started = Ago(TimeSpan.FromMinutes(30));
        var id = (await ReadAsync(await client.PostAsJsonAsync("/api/exercise", Session(startedAt: started, endedAt: null))))
            ["id"]!.GetValue<string>();

        (await client.PostAsJsonAsync("/api/exercise", Session("gym"))).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await client.PutAsJsonAsync($"/api/exercise/{id}", Session("gym", started, null))).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    // Otro offers back what she called her other sessions: each name once, the latest first, a
    // handful, and never a walk's or the gym's.
    [Fact]
    public async Task TheNamesOfHerOtherSessions_AreEachOnce_LatestFirst_AtMostEight()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var names = new[] { "bici", "natación", "yoga", "pádel", "baile", "remo", "tenis", "escalada", "golf" };
        foreach (var (name, day) in names.Select((name, index) => (name, index + 1)))
        {
            await client.PostAsJsonAsync("/api/exercise", Session(
                "other", $"2026-10-{day:00}T09:00:00Z", $"2026-10-{day:00}T10:00:00Z", name));
        }

        await client.PostAsJsonAsync("/api/exercise", Session("other", "2026-10-12T09:00:00Z", "2026-10-12T10:00:00Z", "bici"));
        await client.PostAsJsonAsync("/api/exercise", Session("walk", "2026-10-13T09:00:00Z", "2026-10-13T10:00:00Z"));

        var listed = (await client.GetFromJsonAsync<JsonNode>("/api/exercise/names"))!.AsArray()
            .Select(n => n!.GetValue<string>());

        listed.ShouldBe(["bici", "golf", "escalada", "tenis", "remo", "baile", "pádel", "yoga"]);
    }

    // A session started by mistake is undone by deleting it.
    [Fact]
    public async Task ASession_CanBeDeleted()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var id = (await ReadAsync(await client.PostAsJsonAsync("/api/exercise", Session())))["id"]!.GetValue<string>();

        (await client.DeleteAsync($"/api/exercise/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ListAsync(client)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnUnknownSession_IsNotFound()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        (await client.PutAsJsonAsync($"/api/exercise/{Guid.NewGuid()}", Session())).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
        (await client.DeleteAsync($"/api/exercise/{Guid.NewGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // An existing log upgrades in place: the server opens a database made before exercise existed,
    // gains the table and loses no meal, dose or period.
    [Fact]
    public async Task ALogFromBeforeExercise_GainsItOnStart_AndKeepsItsMealsDosesAndPeriods()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sugary-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "sugary.db");
        await using (var db = new SugaryDbContext(
                         new DbContextOptionsBuilder<SugaryDbContext>().UseSqlite($"Data Source={path}").Options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261016114005_DayNotes");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Meals (Id, EatenAt, Outcome, CarbsLow, Carbs, CarbsHigh) "
                + "VALUES ({0}, '2026-10-01 12:00:00', 'Unknown', 40, 50, 60)",
                Guid.NewGuid().ToString().ToUpperInvariant());
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Doses (Id, TakenAt, Units, Kind) VALUES ({0}, '2026-10-01 12:00:00', 6, 'Rapid')",
                Guid.NewGuid().ToString().ToUpperInvariant());
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO Periods (Id, Start, End) VALUES ({0}, '2026-09-03', '2026-09-07')",
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

            (await client.GetFromJsonAsync<JsonNode>("/api/meals"))!.AsArray().Count.ShouldBe(1);
            (await client.GetFromJsonAsync<JsonNode>("/api/doses"))!.AsArray().Count.ShouldBe(1);
            (await client.GetFromJsonAsync<JsonNode>("/api/periods"))!.AsArray().Count.ShouldBe(1);
            (await client.PostAsJsonAsync("/api/exercise", Session())).StatusCode.ShouldBe(HttpStatusCode.Created);
            (await ListAsync(client)).Count.ShouldBe(1);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}