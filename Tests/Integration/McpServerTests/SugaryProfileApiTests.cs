using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// Who she is as the app records it: whether she is a woman or a man (which the Cycle tab follows), her
// diabetes and when it was diagnosed, and her height and weight as a history of days, since a child
// grows. Each is hers alone, read by the agent beside her log; nothing she left unsaid is filled in.
public class SugaryProfileApiTests
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static readonly TimeZoneInfo Madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

    private static string FromToday(int days) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Madrid).DateTime).AddDays(days)
            .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static JsonObject Profile(string? sex = "female", string? diabetesType = "type1",
        string? diagnosedOn = "2015-03-02") => new()
        {
            ["sex"] = sex,
            ["diabetesType"] = diabetesType,
            ["diagnosedOn"] = diagnosedOn
        };

    private static JsonObject Measurement(string? kind = "weight", string? on = "2026-10-01", double? value = 58.4) => new()
    {
        ["kind"] = kind,
        ["on"] = on,
        ["value"] = value
    };

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonNode>(_json))!;

    private static async Task<JsonNode> ProfileOfAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonNode>("/api/profile"))!;

    private static async Task<JsonArray> MeasurementsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonNode>("/api/measurements"))!.AsArray();

    private static string? Text(JsonNode? node, string field) => node![field]?.GetValue<string>();

    private static async Task<string> ProblemAsync(HttpResponseMessage response) =>
        Text(await ReadAsync(response), "detail")!;

    [Fact]
    public async Task AProfileNeverSaved_SaysNothing()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var profile = await ProfileOfAsync(client);

        profile["sex"].ShouldBeNull();
        profile["diabetesType"].ShouldBeNull();
        profile["diagnosedOn"].ShouldBeNull();
    }

    [Fact]
    public async Task AProfile_IsSavedAndReadBack_AndIsHersAlone()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var saved = await client.PutAsJsonAsync("/api/profile", Profile());

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var profile = await ProfileOfAsync(client);
        Text(profile, "sex").ShouldBe("female");
        Text(profile, "diabetesType").ShouldBe("type1");
        Text(profile, "diagnosedOn").ShouldBe("2015-03-02");
        (await ProfileOfAsync(server.As(SugaryServer.Him)))["sex"].ShouldBeNull();
    }

    // Every field is optional: she may say she is a man and nothing about her diabetes, and take back
    // what she said.
    [Fact]
    public async Task AFieldSavedEmpty_IsForgotten()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        await client.PutAsJsonAsync("/api/profile", Profile());

        (await client.PutAsJsonAsync("/api/profile", Profile("male", null, null))).StatusCode.ShouldBe(HttpStatusCode.OK);

        var profile = await ProfileOfAsync(client);
        Text(profile, "sex").ShouldBe("male");
        profile["diabetesType"].ShouldBeNull();
        profile["diagnosedOn"].ShouldBeNull();
    }

    [Theory]
    [InlineData("other", "type1", "2015-03-02", "Sex is female or male.")]
    [InlineData("female", "type7", "2015-03-02",
        "A diabetes type is type1, type2, lada, mody, gestational or other.")]
    public async Task AnUnknownWord_IsRefusedAndNothingIsSaved(string sex, string type, string diagnosed, string problem)
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var refused = await client.PutAsJsonAsync("/api/profile", Profile(sex, type, diagnosed));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ProblemAsync(refused)).ShouldBe(problem);
        (await ProfileOfAsync(client))["sex"].ShouldBeNull();
    }

    [Fact]
    public async Task ADiagnosisAfterToday_IsRefused()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var refused = await client.PutAsJsonAsync("/api/profile", Profile(diagnosedOn: FromToday(2)));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ProblemAsync(refused)).ShouldBe("A diagnosis can't be after today.");
    }

    // One person, two phones: one says when she was diagnosed while the other, open since before,
    // says which diabetes. Both stand; the same field changed to two answers is refused.
    [Fact]
    public async Task TwoPhonesChangingTheProfile_EachChangeStands_AndTheSameFieldIsRefused()
    {
        await using var server = await SugaryServer.StartAsync();
        var phone = await server.SignedInAsync();
        var other = await server.SignedInAsync();
        await phone.PutAsJsonAsync("/api/profile", Profile());
        var opened = StaleSheet.Opened(await ProfileOfAsync(phone), "sex", "diabetesType", "diagnosedOn");

        (await phone.PutAsJsonAsync("/api/profile", StaleSheet.Changed(opened, "diagnosedOn", "2016-01-10")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await other.PutAsJsonAsync("/api/profile", StaleSheet.Changed(opened, "diabetesType", "lada")))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var merged = await ProfileOfAsync(phone);
        Text(merged, "diagnosedOn").ShouldBe("2016-01-10");
        Text(merged, "diabetesType").ShouldBe("lada");
        (await other.PutAsJsonAsync("/api/profile", StaleSheet.Changed(opened, "diagnosedOn", "2014-05-05")))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Measurements_AreKeptAsAHistory_NewestFirst()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var created = await client.PostAsJsonAsync("/api/measurements", Measurement("height", "2026-03-01", 151.5));
        await client.PostAsJsonAsync("/api/measurements", Measurement("height", "2026-09-01", 154));
        await client.PostAsJsonAsync("/api/measurements", Measurement("weight", "2026-09-01", 41.2));

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var listed = await MeasurementsAsync(client);
        listed.Select(m => (Text(m, "kind"), Text(m, "on"), m!["value"]!.GetValue<double>())).ShouldBe([
            ("height", "2026-09-01", 154),
            ("weight", "2026-09-01", 41.2),
            ("height", "2026-03-01", 151.5)
        ]);
        (await MeasurementsAsync(server.As(SugaryServer.Him))).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("shoe", "2026-10-01", 40.0, "A measurement is height or weight.")]
    [InlineData("weight", null, 58.0, "A measurement needs the day it was taken.")]
    [InlineData("height", "2026-10-01", 20.0, "A height is between 30 and 250 cm.")]
    [InlineData("height", "2026-10-01", 1.62, "A height is between 30 and 250 cm.")]
    [InlineData("weight", "2026-10-01", 0.5, "A weight is between 1 and 400 kg.")]
    [InlineData("weight", "2026-10-01", 580.0, "A weight is between 1 and 400 kg.")]
    public async Task AMeasurementOutOfBounds_IsRefusedAndNothingIsSaved(
        string kind, string? on, double value, string problem)
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var refused = await client.PostAsJsonAsync("/api/measurements", Measurement(kind, on, value));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ProblemAsync(refused)).ShouldBe(problem);
        (await MeasurementsAsync(client)).ShouldBeEmpty();
    }

    [Fact]
    public async Task AMeasurementAfterToday_IsRefused()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();

        var refused = await client.PostAsJsonAsync("/api/measurements", Measurement(on: FromToday(2)));

        (await ProblemAsync(refused)).ShouldBe("A measurement can't be taken after today.");
    }

    // A day has one weight: a second is a correction of the first, made on that one.
    [Fact]
    public async Task ASecondMeasurementOfAKindOnADay_IsRefused()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        await client.PostAsJsonAsync("/api/measurements", Measurement());

        var refused = await client.PostAsJsonAsync("/api/measurements", Measurement(value: 59));

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ProblemAsync(refused)).ShouldBe("That day already has a weight: change that one.");
        (await client.PostAsJsonAsync("/api/measurements", Measurement("height", value: 160)))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task AMeasurement_IsCorrectedAndDeleted()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        var saved = await ReadAsync(await client.PostAsJsonAsync("/api/measurements", Measurement()));
        var url = $"/api/measurements/{Text(saved, "id")}";

        var corrected = await client.PutAsJsonAsync(url, Measurement(on: "2026-10-02", value: 57.9));

        corrected.StatusCode.ShouldBe(HttpStatusCode.OK);
        var listed = (await MeasurementsAsync(client)).Single()!;
        Text(listed, "on").ShouldBe("2026-10-02");
        listed["value"]!.GetValue<double>().ShouldBe(57.9);
        (await client.DeleteAsync(url)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await MeasurementsAsync(client)).ShouldBeEmpty();
        (await client.DeleteAsync(url)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TwoPhonesCorrectingOneMeasurement_TheSameFieldIsRefused()
    {
        await using var server = await SugaryServer.StartAsync();
        var phone = await server.SignedInAsync();
        var other = await server.SignedInAsync();
        var saved = await ReadAsync(await phone.PostAsJsonAsync("/api/measurements", Measurement()));
        var url = $"/api/measurements/{Text(saved, "id")}";
        var opened = StaleSheet.Opened(saved, "kind", "on", "value");

        (await phone.PutAsJsonAsync(url, StaleSheet.Changed(opened, "value", 58.0))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await other.PutAsJsonAsync(url, StaleSheet.Changed(opened, "on", "2026-09-30"))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await other.PutAsJsonAsync(url, StaleSheet.Changed(opened, "value", 60.0))).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var listed = (await MeasurementsAsync(phone)).Single()!;
        listed["value"]!.GetValue<double>().ShouldBe(58.0);
        Text(listed, "on").ShouldBe("2026-09-30");
    }

    [Fact]
    public async Task WithoutAPerson_ProfileAndMeasurementsAreUnauthorized()
    {
        await using var server = await SugaryServer.StartAsync();

        (await server.Browser().GetAsync("/api/profile")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await server.Browser().GetAsync("/api/measurements")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // What the agent reads: the profile as one row, each measurement on its own table, and her pens.
    // What she never said is an empty cell, never a guess.
    [Fact]
    public async Task TheAgent_ReadsHerProfileMeasurementsAndPens()
    {
        await using var server = await SugaryServer.StartAsync();
        var client = await server.SignedInAsync();
        (await server.TableRowsAsync("/profile.parquet")).ShouldBe([[null, null, null]]);
        await client.PutAsJsonAsync("/api/profile", Profile(diagnosedOn: null));
        await client.PostAsJsonAsync("/api/measurements", Measurement("height", "2026-09-01", 154));
        await client.PostAsJsonAsync("/api/measurements", Measurement("height", "2026-03-01", 151.5));
        await client.PostAsJsonAsync("/api/measurements", Measurement("weight", "2026-09-01", 41.2));
        await client.PostAsJsonAsync("/api/pens", new { kind = "basal", insulin = "Tresiba", openedOn = "2026-09-01", days = 56 });
        await client.PostAsJsonAsync("/api/pens", new { kind = "rapid", insulin = "Fiasp", openedOn = "2026-09-01", days = 28 });
        await client.PostAsJsonAsync("/api/pens", new { kind = "rapid", insulin = "Lyumjev", openedOn = "2026-09-20", days = 28 });

        (await server.TableRowsAsync("/profile.parquet")).ShouldBe([["female", "type1", null]]);
        (await server.TableRowsAsync("/heights.parquet")).ShouldBe([["2026-03-01", "151.5"], ["2026-09-01", "154.0"]]);
        (await server.TableRowsAsync("/weights.parquet")).ShouldBe([["2026-09-01", "41.2"]]);
        var pens = await server.QueryAsync(
            "select kind, insulin, opened_on, days, replace_on, replaced_on from pens order by opened_on, kind");
        pens.Select(row => row!.ToJsonString()).ShouldBe([
            """["basal","Tresiba","2026-09-01",56,"2026-10-27",null]""",
            """["rapid","Fiasp","2026-09-01",28,"2026-09-29","2026-09-20"]""",
            """["rapid","Lyumjev","2026-09-20",28,"2026-10-18",null]"""
        ]);
        (await server.QueryAsync("select sex, diabetes_type from profile")).Single()!.ToJsonString()
            .ShouldBe("""["female","type1"]""");
    }
}