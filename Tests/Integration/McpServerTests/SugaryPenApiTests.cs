using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpServerSugary.App;
using McpServerSugary.Store;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// Her insulin pens as the app records them: one open pen of each kind, which insulin, the day she
// first used it and how many days it lasts. A new pen of a kind replaces the open one, which is kept
// as history with the day it was replaced. Every refusal says why and stores nothing.
public class SugaryPenApiTests
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static readonly TimeZoneInfo Madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    private static string FromToday(int days) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Madrid).DateTime).AddDays(days)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static JsonObject Pen(string kind = "rapid", string? insulin = "Fiasp", string? openedOn = "2026-10-01",
        int? days = 28) => new()
        {
            ["kind"] = kind,
            ["insulin"] = insulin,
            ["openedOn"] = openedOn,
            ["days"] = days
        };

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonNode>(_json))!;

    private static async Task<JsonArray> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonNode>("/api/pens"))!.AsArray();

    private static string Text(JsonNode? node, string field) => node![field]!.GetValue<string>();

    [Fact]
    public async Task APen_IsStoredAndListedBackWithItsReplacementDay()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var created = await client.PostAsJsonAsync("/api/pens", Pen());

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var listed = (await ListAsync(client)).Single()!;
        Text(listed, "id").ShouldBe(Text(await ReadAsync(created), "id"));
        Text(listed, "kind").ShouldBe("rapid");
        Text(listed, "insulin").ShouldBe("Fiasp");
        Text(listed, "openedOn").ShouldBe("2026-10-01");
        listed["days"]!.GetValue<int>().ShouldBe(28);
        Text(listed, "replaceOn").ShouldBe("2026-10-29");
        listed["replacedOn"].ShouldBeNull();
    }

    // The open pen of the kind is closed on the day the new one was opened; the other kind's stays.
    [Fact]
    public async Task ANewPen_ReplacesTheOpenOneOfItsKind()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        await client.PostAsJsonAsync("/api/pens", Pen("basal", "Tresiba", "2026-09-20", 56));
        await client.PostAsJsonAsync("/api/pens", Pen());

        var replaced = await client.PostAsJsonAsync("/api/pens", Pen(openedOn: "2026-10-20"));

        replaced.StatusCode.ShouldBe(HttpStatusCode.Created);
        var pens = await ListAsync(client);
        pens.Select(p => (Text(p, "kind"), Text(p, "openedOn"), p!["replacedOn"]?.GetValue<string>()))
            .ShouldBe([("rapid", "2026-10-20", null), ("rapid", "2026-10-01", "2026-10-20"), ("basal", "2026-09-20", null)]);
    }

    [Fact]
    public async Task ANewPen_OpenedBeforeTheOpenOne_IsRefused()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        await client.PostAsJsonAsync("/api/pens", Pen());

        var early = await client.PostAsJsonAsync("/api/pens", Pen(openedOn: "2026-09-30"));

        early.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        Text(await ReadAsync(early), "detail").ShouldBe(PenEndpoints.OpenedBeforeCurrent);
        (await ListAsync(client)).Count.ShouldBe(1);
    }

    // A second tap on "New pen" the same day would keep a pen used for no days at all.
    [Fact]
    public async Task ANewPen_OpenedTheSameDayAsTheOpenOne_IsRefused()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        await client.PostAsJsonAsync("/api/pens", Pen());

        var again = await client.PostAsJsonAsync("/api/pens", Pen());

        again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        Text(await ReadAsync(again), "detail").ShouldBe(PenEndpoints.OpenedBeforeCurrent);
        (await ListAsync(client)).Single()!["replacedOn"].ShouldBeNull();
    }

    [Theory]
    [InlineData(null, "2026-10-01", 28, PenRules.NoInsulin)]
    [InlineData("Fiasp", null, 28, PenRules.NoOpenedOn)]
    [InlineData("Fiasp", "2026-10-01", 0, PenRules.DaysOutOfRange)]
    public async Task AnIncompletePen_IsRefusedWithWhy(string? insulin, string? openedOn, int? days, string problem)
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var refused = await client.PostAsJsonAsync("/api/pens", Pen(insulin: insulin, openedOn: openedOn, days: days));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        Text(await ReadAsync(refused), "detail").ShouldBe(problem);
        (await ListAsync(client)).ShouldBeEmpty();
    }

    [Fact]
    public async Task APenOpenedAfterTomorrow_IsRefused()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var refused = await client.PostAsJsonAsync("/api/pens", Pen(openedOn: FromToday(2)));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        Text(await ReadAsync(refused), "detail").ShouldBe(PenRules.OpenedInFuture);
    }

    [Fact]
    public async Task AnUnknownKind_IsRefusedWithWhy()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var refused = await client.PostAsJsonAsync("/api/pens", Pen(kind: "inhaled"));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        Text(await ReadAsync(refused), "detail").ShouldBe(PenEndpoints.UnknownKind);
    }

    // A correction keeps its kind: the insulin, the day and the days are hers to change.
    [Fact]
    public async Task APen_IsCorrected()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var id = Text(await ReadAsync(await client.PostAsJsonAsync("/api/pens", Pen())), "id");

        var corrected = await client.PutAsJsonAsync($"/api/pens/{id}", Pen("basal", "Lyumjev", "2026-10-03", 30));

        corrected.StatusCode.ShouldBe(HttpStatusCode.OK);
        var pen = (await ListAsync(client)).Single();
        (Text(pen, "kind"), Text(pen, "insulin"), Text(pen, "openedOn"), Text(pen, "replaceOn"))
            .ShouldBe(("rapid", "Lyumjev", "2026-10-03", "2026-11-02"));
    }

    // A replaced pen was in use from the day it was opened to the day it was replaced.
    [Fact]
    public async Task AReplacedPen_IsNotMovedPastItsReplacement()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var old = Text(await ReadAsync(await client.PostAsJsonAsync("/api/pens", Pen())), "id");
        await client.PostAsJsonAsync("/api/pens", Pen(openedOn: "2026-10-20"));

        var refused = await client.PutAsJsonAsync($"/api/pens/{old}", Pen(openedOn: "2026-10-21"));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        Text(await ReadAsync(refused), "detail").ShouldBe(PenEndpoints.OpenedAfterReplaced);
    }

    [Fact]
    public async Task APen_IsDeleted()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var id = Text(await ReadAsync(await client.PostAsJsonAsync("/api/pens", Pen())), "id");

        (await client.DeleteAsync($"/api/pens/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await ListAsync(client)).ShouldBeEmpty();
        (await client.DeleteAsync($"/api/pens/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Pens_NeedASignedInPhone()
    {
        await using var server = await SugaryServer.StartAsync();

        (await server.Browser().GetAsync("/api/pens")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}