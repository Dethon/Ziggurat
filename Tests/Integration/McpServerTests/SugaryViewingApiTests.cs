using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using McpServerSugary.Store;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// Sugary is on for a person only once an owner said so, and a holder of everyone's log picks whose log
// the app shows (user's decisions, 2026-11-19): somebody it is off for is refused the whole app, by a
// code the app words; a reader reads anybody Sugary is on for by naming them, never writes to them,
// and keeps their own log as it was; a reader Sugary is off for reads the others and has no log.
public class SugaryViewingApiTests
{
    private static readonly Account Reader = new("household-home", "user-reader");
    private static readonly Account Off = new("household-home", "user-off");

    private static HouseholdSugaryPeople People(bool readerUsesSugary = true) => new([
        new SugaryPerson(SugaryServer.Her.UserId, "Her", ReadsEveryLog: false, UsesSugary: true),
        new SugaryPerson(SugaryServer.Him.UserId, "Him", ReadsEveryLog: false, UsesSugary: true),
        new SugaryPerson(Reader.UserId, "Reader", ReadsEveryLog: true, UsesSugary: readerUsesSugary),
        new SugaryPerson(Off.UserId, "Off", ReadsEveryLog: false, UsesSugary: false)
    ]);

    private static JsonObject Meal(string notes) => new()
    {
        ["eatenAt"] = "2026-10-11T13:30:00+02:00",
        ["notes"] = notes,
        ["outcome"] = "unknown",
        ["photo"] = "data:image/jpeg;base64,/9j/4AAQSkZJRg==",
        ["items"] = new JsonArray(new JsonObject
        {
            ["name"] = "rice", ["grams"] = 150, ["carbsPer100g"] = 28, ["carbsLow"] = 32, ["carbs"] = 42, ["carbsHigh"] = 52,
            ["source"] = "estimate"
        })
    };

    private static async Task<string> CodeOf(HttpResponseMessage response, HttpStatusCode status)
    {
        response.StatusCode.ShouldBe(status, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!["code"]!.GetValue<string>();
    }

    private static async Task<string> SavedAsync(HttpClient phone, JsonObject meal)
    {
        var response = await phone.PostAsJsonAsync("/api/meals", meal);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<string>();
    }

    [Fact]
    public async Task SomebodySugaryIsOffFor_IsRefusedTheWholeApp()
    {
        await using var server = await SugaryServer.StartAsync(sugaryPeople: People());
        var off = server.As(Off);

        (await CodeOf(await off.GetAsync("/api/session"), HttpStatusCode.Forbidden)).ShouldBe("sugary-not-yours");
        (await CodeOf(await off.GetAsync("/api/meals"), HttpStatusCode.Forbidden)).ShouldBe("sugary-not-yours");
        (await CodeOf(await off.PostAsJsonAsync("/api/meals", Meal("mine")), HttpStatusCode.Forbidden)).ShouldBe("sugary-not-yours");
        (await CodeOf(await off.GetAsync($"/api/meals?person={SugaryServer.Her.UserId}"), HttpStatusCode.Forbidden))
            .ShouldBe("sugary-not-yours");
    }

    [Fact]
    public async Task TheSession_OffersAReaderEverybodySugaryIsOnFor_AndNobodyElseAnyone()
    {
        await using var server = await SugaryServer.StartAsync(sugaryPeople: People());

        var reader = (await server.As(Reader).GetFromJsonAsync<JsonNode>("/api/session"))!;
        var her = (await server.As(SugaryServer.Her).GetFromJsonAsync<JsonNode>("/api/session"))!;

        reader["usesSugary"]!.GetValue<bool>().ShouldBeTrue();
        reader["people"]!.AsArray().Select(p => (p!["userId"]!.GetValue<string>(), p["displayName"]!.GetValue<string>()))
            .ShouldBe([(SugaryServer.Her.UserId, "Her"), (SugaryServer.Him.UserId, "Him"), (Reader.UserId, "Reader")]);
        her["usesSugary"]!.GetValue<bool>().ShouldBeTrue();
        her["people"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task AReader_ReadsHerLogByNamingHer_AndKeepsTheirOwn()
    {
        await using var server = await SugaryServer.StartAsync(sugaryPeople: People());
        var id = await SavedAsync(server.As(SugaryServer.Her), Meal("her paella"));
        var reader = server.As(Reader);
        await SavedAsync(reader, Meal("reader's toast"));

        var hers = (await reader.GetFromJsonAsync<JsonArray>($"/api/meals?person={SugaryServer.Her.UserId}"))!;
        var own = (await reader.GetFromJsonAsync<JsonArray>("/api/meals"))!;
        var photo = await reader.GetAsync($"/api/meals/{id}/photo?person={SugaryServer.Her.UserId}");
        var export = await reader.GetStringAsync($"/api/export/meals.csv?person={SugaryServer.Her.UserId}");

        hers.Single()!["notes"]!.GetValue<string>().ShouldBe("her paella");
        own.Single()!["notes"]!.GetValue<string>().ShouldBe("reader's toast");
        photo.StatusCode.ShouldBe(HttpStatusCode.OK);
        export.ShouldContain("her paella");
        export.ShouldNotContain("reader's toast");
    }

    [Fact]
    public async Task AReader_WritesNothingToHerLog()
    {
        await using var server = await SugaryServer.StartAsync(sugaryPeople: People());
        var her = server.As(SugaryServer.Her);
        var id = await SavedAsync(her, Meal("her paella"));
        var reader = server.As(Reader);
        var asHer = $"?person={SugaryServer.Her.UserId}";

        (await CodeOf(await reader.PostAsJsonAsync($"/api/meals{asHer}", Meal("planted")), HttpStatusCode.Forbidden))
            .ShouldBe("viewing-read-only");
        (await CodeOf(await reader.PutAsJsonAsync($"/api/meals/{id}{asHer}", Meal("changed")), HttpStatusCode.Forbidden))
            .ShouldBe("viewing-read-only");
        (await CodeOf(await reader.DeleteAsync($"/api/meals/{id}{asHer}"), HttpStatusCode.Forbidden)).ShouldBe("viewing-read-only");

        (await her.GetFromJsonAsync<JsonArray>("/api/meals"))!.Single()!["notes"]!.GetValue<string>().ShouldBe("her paella");
        (await reader.GetFromJsonAsync<JsonArray>("/api/meals"))!.ShouldBeEmpty();
    }

    // Her LibreLinkUp account is hers: a reader sees her link's state and range, never the email she
    // signs in with nor the names of the people her account follows.
    [Fact]
    public async Task AReader_SeesHerGlucoseLinksState_NotHerAccount()
    {
        await using var server = await SugaryServer.StartAsync(sugaryPeople: People());
        await using (var db = server.Services.GetRequiredService<SugaryStore>().For(SugaryServer.Her))
        {
            var seeded = new GlucoseLink
            {
                Email = "ana@example.com", SealedPassword = [1, 2, 3], PatientId = "patient-1", TargetLow = 80,
                LinkedAt = DateTimeOffset.UtcNow.AddDays(-1), LastSuccessAt = DateTimeOffset.UtcNow
            };
            seeded.Connections.Add(new GlucoseConnectionOffered { PatientId = "patient-1", Name = "Ana García" });
            db.GlucoseLinks.Add(seeded);
            await db.SaveChangesAsync();
        }

        var reader = server.As(Reader);
        var now = (await reader.GetFromJsonAsync<JsonNode>($"/api/glucose/current?person={SugaryServer.Her.UserId}"))!;
        var link = (await reader.GetFromJsonAsync<JsonNode>($"/api/glucose/link?person={SugaryServer.Her.UserId}"))!;
        var hers = (await server.As(SugaryServer.Her).GetFromJsonAsync<JsonNode>("/api/glucose/link"))!;

        now["targetLow"]!.GetValue<int>().ShouldBe(80);
        foreach (var shown in new[] { now["link"]!, link })
        {
            shown.ToJsonString().ShouldNotContain("ana@example.com");
            shown.ToJsonString().ShouldNotContain("Ana García");
            shown["patientId"]!.GetValue<string>().ShouldBe("patient-1");
        }

        hers["email"]!.GetValue<string>().ShouldBe("ana@example.com");
    }

    // The pantry is the household's, whoever's log is open: a reader adds a food while looking at her
    // log, a reader with no log of their own keeps it too, and it is the same pantry for everyone.
    // Somebody Sugary is off for, without the grant, has none of it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ThePantry_IsTheHouseholds_WhoeversLogIsOpen(bool readerUsesSugary)
    {
        await using var server = await SugaryServer.StartAsync(sugaryPeople: People(readerUsesSugary));
        var reader = server.As(Reader);
        var added = await reader.PostAsJsonAsync($"/api/foods?person={SugaryServer.Her.UserId}", Food("Pan de molde"));
        var plain = await reader.PostAsJsonAsync("/api/foods", Food("Arroz"));

        added.StatusCode.ShouldBe(HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        plain.StatusCode.ShouldBe(HttpStatusCode.Created, await plain.Content.ReadAsStringAsync());
        (await server.As(SugaryServer.Her).GetFromJsonAsync<JsonArray>("/api/foods"))!
            .Select(f => f!["name"]!.GetValue<string>()).ShouldBe(["Arroz", "Pan de molde"], ignoreOrder: true);
        (await CodeOf(await server.As(Off).GetAsync("/api/foods"), HttpStatusCode.Forbidden)).ShouldBe("sugary-not-yours");
    }

    private static JsonObject Food(string name) => new()
    {
        ["name"] = name, ["carbsPer100g"] = 46, ["unit"] = "g", ["portionGrams"] = null, ["portionCarbs"] = null,
        ["barcode"] = null, ["source"] = "manual"
    };

    // Naming somebody is no way in: not without the grant, not somebody Sugary is off for, not somebody
    // of another household — each refused, never answered with an empty log.
    [Fact]
    public async Task NamingSomebodyNotOffered_IsRefused()
    {
        await using var server = await SugaryServer.StartAsync(sugaryPeople: People());
        await SavedAsync(server.As(SugaryServer.Her), Meal("her paella"));

        (await CodeOf(await server.As(SugaryServer.Him).GetAsync($"/api/meals?person={SugaryServer.Her.UserId}"),
            HttpStatusCode.Forbidden)).ShouldBe("not-granted");
        (await CodeOf(await server.As(Reader).GetAsync($"/api/meals?person={Off.UserId}"), HttpStatusCode.Forbidden))
            .ShouldBe("not-granted");
        (await CodeOf(await server.As(Reader).GetAsync($"/api/meals?person={SugaryServer.Neighbour.UserId}"), HttpStatusCode.Forbidden))
            .ShouldBe("not-granted");
    }

    // The grant alone opens the app: Sugary off for the reader, they read the others and have no log.
    [Fact]
    public async Task AReaderSugaryIsOffFor_ReadsTheOthers_AndHasNoLogOfTheirOwn()
    {
        await using var server = await SugaryServer.StartAsync(sugaryPeople: People(readerUsesSugary: false));
        await SavedAsync(server.As(SugaryServer.Her), Meal("her paella"));
        var reader = server.As(Reader);

        var session = (await reader.GetFromJsonAsync<JsonNode>("/api/session"))!;
        var hers = (await reader.GetFromJsonAsync<JsonArray>($"/api/meals?person={SugaryServer.Her.UserId}"))!;

        session["usesSugary"]!.GetValue<bool>().ShouldBeFalse();
        session["people"]!.AsArray().Select(p => p!["userId"]!.GetValue<string>())
            .ShouldBe([SugaryServer.Her.UserId, SugaryServer.Him.UserId]);
        hers.Single()!["notes"]!.GetValue<string>().ShouldBe("her paella");
        (await CodeOf(await reader.GetAsync("/api/meals"), HttpStatusCode.Forbidden)).ShouldBe("sugary-not-yours");
        (await CodeOf(await reader.PostAsJsonAsync("/api/meals", Meal("mine")), HttpStatusCode.Forbidden)).ShouldBe("sugary-not-yours");
    }
}