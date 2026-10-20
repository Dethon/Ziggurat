using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// A note she writes on a day of her calendar, period or not: one per day, set by writing it and
// gone when she empties it.
public class SugaryDayNoteApiTests
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    private static readonly TimeZoneInfo Madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    private static string FromToday(int days) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Madrid).DateTime).AddDays(days)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<JsonArray> ListAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<JsonNode>($"/api/day-notes{query}"))!.AsArray();

    private static Task<HttpResponseMessage> PutAsync(HttpClient client, string day, string? text) =>
        client.PutAsJsonAsync($"/api/day-notes/{day}", new JsonObject { ["text"] = text });

    [Fact]
    public async Task ANote_IsSetReplacedAndListedOldestFirst()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        (await PutAsync(client, "2026-10-12", " craving sweets ")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PutAsync(client, "2026-10-10", "cramps")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await PutAsync(client, "2026-10-12", "craving chocolate")).StatusCode.ShouldBe(HttpStatusCode.OK);

        var notes = await ListAsync(client);
        notes.Select(n => n!["day"]!.GetValue<string>()).ShouldBe(["2026-10-10", "2026-10-12"]);
        notes.Select(n => n!["text"]!.GetValue<string>()).ShouldBe(["cramps", "craving chocolate"]);
    }

    [Fact]
    public async Task Notes_CanBeListedForAPeriodOfDays()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        await PutAsync(client, "2026-08-31", "a");
        await PutAsync(client, "2026-09-01", "b");
        await PutAsync(client, "2026-09-30", "c");
        await PutAsync(client, "2026-10-01", "d");

        (await ListAsync(client, "?from=2026-09-01&to=2026-09-30")).Select(n => n!["text"]!.GetValue<string>())
            .ShouldBe(["b", "c"]);
    }

    // Emptying a note is deleting it: a day with no words has no note.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task AnEmptiedNote_IsGone(string? text)
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        await PutAsync(client, "2026-10-12", "cramps");

        (await PutAsync(client, "2026-10-12", text)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ListAsync(client)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ANote_CanBeDeleted()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        await PutAsync(client, "2026-10-12", "cramps");

        (await client.DeleteAsync("/api/day-notes/2026-10-12")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.DeleteAsync("/api/day-notes/2026-10-12")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        (await ListAsync(client)).ShouldBeEmpty();
    }

    [Fact]
    public async Task TheBounds_AreKeptExactly()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        (await PutAsync(client, FromToday(1), new string('x', 500))).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await ListAsync(client)).Single()!["text"]!.GetValue<string>().Length.ShouldBe(500);
    }

    public static TheoryData<string, string, string?, string> Refusals => new()
    {
        { "too long", "2026-10-12", new string('x', 501), "A note can be at most 500 characters." },
        { "in the future", FromToday(2), "cramps", "A note can't be for a day after today." },
        { "not a day", "2026-13-40", "cramps", "A day is written as yyyy-MM-dd." },
        { "not a day at all", "yesterday", "cramps", "A day is written as yyyy-MM-dd." }
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ARefusedNote_SaysWhyAndStoresNothing(string _, string day, string? text, string sentence)
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var response = await PutAsync(client, day, text);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonNode>(_json))!["detail"]!.GetValue<string>().ShouldBe(sentence);
        (await ListAsync(client)).ShouldBeEmpty();
    }
}