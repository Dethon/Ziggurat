using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Sugary;
using Domain.Security.Sealing;
using McpServerSugary.Glucose;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// A person links the LibreLinkUp follower account her glucose comes from. Linking logs in and lists
// the people the account follows, so a refusal is said before anything is kept, in a code the app can
// word; one person followed is chosen, several are offered. Her password is kept sealed for her and
// never answered by any route.
public class SugaryGlucoseLinkApiTests
{
    private const string Email = "ana@example.com";
    private const string Password = "correct horse battery";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static readonly string _key = Convert.ToBase64String(SealingKey.Generate().Export());

    private static Task<SugaryServer> StartAsync(FakeLibreLinkUp fake, string? sealingKey = null) =>
        SugaryServer.StartAsync(
            configure: s => s with { SealingKey = sealingKey ?? _key },
            services: c => c.Replace(ServiceDescriptor.Singleton(new LibreLinkUpClient(
                LibreLinkUpClient.CreateHttp(), fake.Regions, TimeProvider.System,
                NullLogger<LibreLinkUpClient>.Instance))));

    private static JsonObject Credentials(string email = Email, string password = Password, string? region = null) =>
        new() { ["email"] = email, ["password"] = password, ["region"] = region };

    private static async Task<JsonNode> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonNode>(_json))!;

    private static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await ReadAsync(response))["code"]?.GetValue<string>();

    private static string? Text(JsonNode? node, string field) => node![field]?.GetValue<string>();

    private static async Task<GlucoseLink?> RowOf(SugaryServer server, global::Domain.DTOs.Channel.Account person)
    {
        await using var db = server.Services.GetRequiredService<SugaryStore>().For(person);
        return await db.GlucoseLinks.AsNoTracking().Include(l => l.Connections).SingleOrDefaultAsync();
    }

    [Fact]
    public async Task OnePersonFollowed_IsChosenAndTheLinkKept()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        fake.AddAccount(Email, Password).Follow("patient-1", "Ana", "García");
        await using var server = await StartAsync(fake);
        var client = server.As(SugaryServer.Her);

        var linked = await client.PutAsJsonAsync("/api/glucose/link", Credentials(region: "eu"));

        linked.StatusCode.ShouldBe(HttpStatusCode.OK);
        var link = await ReadAsync(linked);
        Text(link, "email").ShouldBe(Email);
        Text(link, "region").ShouldBe("eu");
        Text(link, "patientId").ShouldBe("patient-1");
        Text(link, "name").ShouldBe("Ana García");
        Text(link, "unit").ShouldBe("mgdl");
        link["targetLow"]!.GetValue<int>().ShouldBe(70);
        link["targetHigh"]!.GetValue<int>().ShouldBe(180);
        link["lastFailure"].ShouldBeNull();

        var read = await client.GetAsync("/api/glucose/link");
        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        Text(await ReadAsync(read), "patientId").ShouldBe("patient-1");
    }

    // The account's region is the one LibreLinkUp placed it at, not whatever she typed or left out.
    [Fact]
    public async Task TheRegion_IsTheOneTheAccountWasFoundAt()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        fake.AddAccount(Email, Password, region: "us").Follow("patient-1", "Ana", "García");
        await using var server = await StartAsync(fake);

        var linked = await server.As(SugaryServer.Her).PutAsJsonAsync("/api/glucose/link", Credentials());

        linked.StatusCode.ShouldBe(HttpStatusCode.OK);
        Text(await ReadAsync(linked), "region").ShouldBe("us");
        (await RowOf(server, SugaryServer.Her))!.Region.ShouldBe("us");
    }

    [Fact]
    public async Task SeveralPeopleFollowed_AreOfferedAndTheOneSheChoosesIsKept()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        var account = fake.AddAccount(Email, Password);
        account.Follow("patient-1", "Ana", "García");
        account.Follow("patient-2", "Leo", "García");
        await using var server = await StartAsync(fake);
        var client = server.As(SugaryServer.Her);

        var linked = await ReadAsync(await client.PutAsJsonAsync("/api/glucose/link", Credentials()));

        var requests = fake.Requests.Count;
        linked["patientId"].ShouldBeNull();
        linked["connections"]!.AsArray().Select(c => (Text(c, "patientId"), Text(c, "name")))
            .ShouldBe([("patient-1", "Ana García"), ("patient-2", "Leo García")], ignoreOrder: true);

        var chosen = await client.PutAsJsonAsync("/api/glucose/link/connection", new JsonObject { ["patientId"] = "patient-2" });

        chosen.StatusCode.ShouldBe(HttpStatusCode.OK);
        Text(await ReadAsync(chosen), "name").ShouldBe("Leo García");
        (await RowOf(server, SugaryServer.Her))!.PatientId.ShouldBe("patient-2");
        fake.Requests.Count.ShouldBe(requests, "choosing asks LibreLinkUp nothing");
    }

    [Fact]
    public async Task AConnectionNotOffered_IsRefused()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        var account = fake.AddAccount(Email, Password);
        account.Follow("patient-1", "Ana", "García");
        account.Follow("patient-2", "Leo", "García");
        await using var server = await StartAsync(fake);
        var client = server.As(SugaryServer.Her);
        await client.PutAsJsonAsync("/api/glucose/link", Credentials());

        var chosen = await client.PutAsJsonAsync("/api/glucose/link/connection", new JsonObject { ["patientId"] = "patient-9" });

        chosen.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(chosen)).ShouldBe("unknown-connection");
        (await RowOf(server, SugaryServer.Her))!.PatientId.ShouldBeNull();
    }

    [Fact]
    public async Task ChoosingWithNoLink_IsNotLinked()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        await using var server = await StartAsync(fake);

        var chosen = await server.As(SugaryServer.Her)
            .PutAsJsonAsync("/api/glucose/link/connection", new JsonObject { ["patientId"] = "patient-1" });

        chosen.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(chosen)).ShouldBe("not-linked");
    }

    [Fact]
    public async Task RefusedCredentials_AreSaidAndNothingIsKept()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        fake.AddAccount(Email, Password).Follow("patient-1", "Ana", "García");
        await using var server = await StartAsync(fake);
        var client = server.As(SugaryServer.Her);

        var linked = await client.PutAsJsonAsync("/api/glucose/link", Credentials(password: "wrong"));

        linked.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(linked)).ShouldBe("credentials-refused");
        (await RowOf(server, SugaryServer.Her)).ShouldBeNull();
        var read = await client.GetAsync("/api/glucose/link");
        read.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(read)).ShouldBe("not-linked");
    }

    [Fact]
    public async Task AStepToAccept_IsSaidWithItsName()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        var account = fake.AddAccount(Email, Password);
        account.Follow("patient-1", "Ana", "García");
        account.Step = "tou";
        await using var server = await StartAsync(fake);

        var linked = await server.As(SugaryServer.Her).PutAsJsonAsync("/api/glucose/link", Credentials());

        linked.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await ReadAsync(linked);
        Text(problem, "code").ShouldBe("step-required");
        Text(problem, "component").ShouldBe("tou");
        (await RowOf(server, SugaryServer.Her)).ShouldBeNull();
    }

    [Fact]
    public async Task AnAccountFollowingNobody_IsSaid()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        fake.AddAccount(Email, Password);
        await using var server = await StartAsync(fake);

        var linked = await server.As(SugaryServer.Her).PutAsJsonAsync("/api/glucose/link", Credentials());

        linked.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(linked)).ShouldBe("no-connections");
        (await RowOf(server, SugaryServer.Her)).ShouldBeNull();
    }

    [Fact]
    public async Task LibreLinkUpUnreachable_IsUnavailable()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        fake.AddAccount(Email, Password).Follow("patient-1", "Ana", "García");
        await using var server = await StartAsync(fake);
        fake.InterruptNext(HttpStatusCode.InternalServerError);

        var linked = await server.As(SugaryServer.Her).PutAsJsonAsync("/api/glucose/link", Credentials());

        linked.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await CodeOf(linked)).ShouldBe("unavailable");
        (await RowOf(server, SugaryServer.Her)).ShouldBeNull();
    }

    [Fact]
    public async Task AskedTooOften_IsRateLimitedWithTheWait()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        fake.AddAccount(Email, Password).Follow("patient-1", "Ana", "García");
        await using var server = await StartAsync(fake);
        fake.InterruptNext(HttpStatusCode.TooManyRequests, null, ("Retry-After", "120"));

        var linked = await server.As(SugaryServer.Her).PutAsJsonAsync("/api/glucose/link", Credentials());

        linked.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await CodeOf(linked)).ShouldBe("rate-limited");
        linked.Headers.RetryAfter?.Delta.ShouldBe(TimeSpan.FromMinutes(2));
    }

    [Theory]
    [InlineData("", Password)]
    [InlineData(Email, "")]
    [InlineData(Email, null)]
    public async Task MissingCredentials_AreRefusedWithoutAskingLibreLinkUp(string email, string? password)
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        await using var server = await StartAsync(fake);

        var linked = await server.As(SugaryServer.Her).PutAsJsonAsync("/api/glucose/link",
            new JsonObject { ["email"] = email, ["password"] = password });

        linked.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(linked)).ShouldBe("missing-credentials");
        fake.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task ARegionThatIsNoCode_IsRefusedWithoutAskingLibreLinkUp()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        await using var server = await StartAsync(fake);

        var linked = await server.As(SugaryServer.Her)
            .PutAsJsonAsync("/api/glucose/link", Credentials(region: "evil.example.com/"));

        linked.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(linked)).ShouldBe("unknown-region");
        fake.Requests.ShouldBeEmpty();
    }

    // A wrong password typed over a working link leaves the link as it was: her glucose keeps coming.
    [Fact]
    public async Task ARefusedRelink_LeavesTheLinkAsItWas()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        fake.AddAccount(Email, Password).Follow("patient-1", "Ana", "García");
        await using var server = await StartAsync(fake);
        var client = server.As(SugaryServer.Her);
        await client.PutAsJsonAsync("/api/glucose/link", Credentials());
        var before = await RowOf(server, SugaryServer.Her);

        var relinked = await client.PutAsJsonAsync("/api/glucose/link", Credentials(password: "wrong"));

        relinked.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var after = await RowOf(server, SugaryServer.Her);
        after!.SealedPassword.ShouldBe(before!.SealedPassword);
        after.PatientId.ShouldBe("patient-1");
    }

    // Linking again (a new password, another account) keeps her unit and range and the person she
    // chose, where the account still follows them.
    [Fact]
    public async Task ARelink_KeepsHerSettingsAndHerChoice()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        var account = fake.AddAccount(Email, Password);
        account.Follow("patient-1", "Ana", "García");
        account.Follow("patient-2", "Leo", "García");
        await using var server = await StartAsync(fake);
        var client = server.As(SugaryServer.Her);
        await client.PutAsJsonAsync("/api/glucose/link", Credentials());
        await client.PutAsJsonAsync("/api/glucose/link/connection", new JsonObject { ["patientId"] = "patient-2" });
        await client.PutAsJsonAsync("/api/glucose/settings",
            new JsonObject { ["unit"] = "mmol", ["targetLow"] = 80, ["targetHigh"] = 160 });

        var relinked = await ReadAsync(await client.PutAsJsonAsync("/api/glucose/link", Credentials()));

        Text(relinked, "patientId").ShouldBe("patient-2");
        Text(relinked, "unit").ShouldBe("mmol");
        relinked["targetLow"]!.GetValue<int>().ShouldBe(80);
        relinked["targetHigh"]!.GetValue<int>().ShouldBe(160);
    }

    [Fact]
    public async Task ThePassword_IsKeptSealedForHerAndNoRouteAnswersIt()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        fake.AddAccount(Email, Password).Follow("patient-1", "Ana", "García");
        await using var server = await StartAsync(fake);
        var client = server.As(SugaryServer.Her);

        var linked = await client.PutAsJsonAsync("/api/glucose/link", Credentials());
        var read = await client.GetAsync("/api/glucose/link");
        var settings = await client.PutAsJsonAsync("/api/glucose/settings",
            new JsonObject { ["unit"] = "mgdl", ["targetLow"] = 70, ["targetHigh"] = 180 });
        var chosen = await client.PutAsJsonAsync("/api/glucose/link/connection",
            new JsonObject { ["patientId"] = "patient-1" });

        foreach (var response in new[] { linked, read, settings, chosen })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).ShouldNotContain(Password);
            (await ReadAsync(response))["password"].ShouldBeNull();
        }

        var row = (await RowOf(server, SugaryServer.Her))!;
        var credentials = server.Services.GetRequiredService<GlucoseCredentials>();
        credentials.Open(SugaryServer.Her, row.SealedPassword).ShouldBe(Password);
        credentials.Open(SugaryServer.Him, row.SealedPassword).ShouldBeNull();
        new GlucoseCredentials(SealingKey.Generate()).Open(SugaryServer.Her, row.SealedPassword).ShouldBeNull();
    }

    [Fact]
    public async Task HerLink_IsHersAlone()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        fake.AddAccount(Email, Password).Follow("patient-1", "Ana", "García");
        await using var server = await StartAsync(fake);
        await server.As(SugaryServer.Her).PutAsJsonAsync("/api/glucose/link", Credentials());

        var his = await server.As(SugaryServer.Him).GetAsync("/api/glucose/link");

        his.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await server.As(SugaryServer.Him).DeleteAsync("/api/glucose/link")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RowOf(server, SugaryServer.Her)).ShouldNotBeNull();
    }

    [Fact]
    public async Task TwoLinksAtOnce_KeepOneLink()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        fake.AddAccount(Email, Password).Follow("patient-1", "Ana", "García");
        await using var server = await StartAsync(fake);
        var client = server.As(SugaryServer.Her);

        var both = await Task.WhenAll(
            client.PutAsJsonAsync("/api/glucose/link", Credentials()),
            client.PutAsJsonAsync("/api/glucose/link", Credentials()));

        both.Select(r => r.StatusCode).ShouldAllBe(s => s == HttpStatusCode.OK);
        await using var db = await server.Services.GetRequiredService<IDbContextFactory<SugaryDbContext>>()
            .CreateDbContextAsync();
        (await db.GlucoseLinks.IgnoreQueryFilters().CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Settings_AreKept()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        fake.AddAccount(Email, Password).Follow("patient-1", "Ana", "García");
        await using var server = await StartAsync(fake);
        var client = server.As(SugaryServer.Her);
        await client.PutAsJsonAsync("/api/glucose/link", Credentials());

        var saved = await client.PutAsJsonAsync("/api/glucose/settings",
            new JsonObject { ["unit"] = "mmol", ["targetLow"] = 72, ["targetHigh"] = 162 });

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var read = await ReadAsync(await client.GetAsync("/api/glucose/link"));
        Text(read, "unit").ShouldBe("mmol");
        read["targetLow"]!.GetValue<int>().ShouldBe(72);
        read["targetHigh"]!.GetValue<int>().ShouldBe(162);
    }

    [Theory]
    [InlineData(39, 180)]
    [InlineData(70, 401)]
    [InlineData(180, 180)]
    [InlineData(180, 70)]
    [InlineData(null, 180)]
    public async Task ARangeOutsideWhatAMeterReads_IsRefused(int? low, int? high)
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        fake.AddAccount(Email, Password).Follow("patient-1", "Ana", "García");
        await using var server = await StartAsync(fake);
        var client = server.As(SugaryServer.Her);
        await client.PutAsJsonAsync("/api/glucose/link", Credentials());

        var saved = await client.PutAsJsonAsync("/api/glucose/settings",
            new JsonObject { ["unit"] = "mgdl", ["targetLow"] = low, ["targetHigh"] = high });

        saved.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(saved)).ShouldBe("bad-range");
        (await RowOf(server, SugaryServer.Her))!.TargetLow.ShouldBe(70);
    }

    [Fact]
    public async Task AnUnknownUnit_IsRefused()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        fake.AddAccount(Email, Password).Follow("patient-1", "Ana", "García");
        await using var server = await StartAsync(fake);
        var client = server.As(SugaryServer.Her);
        await client.PutAsJsonAsync("/api/glucose/link", Credentials());

        var saved = await client.PutAsJsonAsync("/api/glucose/settings",
            new JsonObject { ["unit"] = "grains", ["targetLow"] = 70, ["targetHigh"] = 180 });

        saved.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CodeOf(saved)).ShouldBe("unknown-unit");
    }

    [Fact]
    public async Task SettingsWithNoLink_AreNotLinked()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        await using var server = await StartAsync(fake);

        var saved = await server.As(SugaryServer.Her).PutAsJsonAsync("/api/glucose/settings",
            new JsonObject { ["unit"] = "mgdl", ["targetLow"] = 70, ["targetHigh"] = 180 });

        saved.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await CodeOf(saved)).ShouldBe("not-linked");
    }

    // Her other phone changed the unit while this one's sheet was open on the range: both stay.
    [Fact]
    public async Task AStaleSettingsSave_KeepsWhatAnotherPhoneSaved()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        fake.AddAccount(Email, Password).Follow("patient-1", "Ana", "García");
        await using var server = await StartAsync(fake);
        var client = server.As(SugaryServer.Her);
        await client.PutAsJsonAsync("/api/glucose/link", Credentials());
        var loaded = new JsonObject { ["unit"] = "mgdl", ["targetLow"] = 70, ["targetHigh"] = 180 };
        await client.PutAsJsonAsync("/api/glucose/settings",
            new JsonObject { ["unit"] = "mmol", ["targetLow"] = 70, ["targetHigh"] = 180, ["loaded"] = loaded.DeepClone() });

        var saved = await client.PutAsJsonAsync("/api/glucose/settings",
            new JsonObject { ["unit"] = "mgdl", ["targetLow"] = 80, ["targetHigh"] = 180, ["loaded"] = loaded.DeepClone() });

        saved.StatusCode.ShouldBe(HttpStatusCode.OK);
        var read = await ReadAsync(saved);
        Text(read, "unit").ShouldBe("mmol");
        read["targetLow"]!.GetValue<int>().ShouldBe(80);
    }

    [Fact]
    public async Task ASettingsSaveBothPhonesChanged_IsRefusedAsAClash()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        fake.AddAccount(Email, Password).Follow("patient-1", "Ana", "García");
        await using var server = await StartAsync(fake);
        var client = server.As(SugaryServer.Her);
        await client.PutAsJsonAsync("/api/glucose/link", Credentials());
        var loaded = new JsonObject { ["unit"] = "mgdl", ["targetLow"] = 70, ["targetHigh"] = 180 };
        await client.PutAsJsonAsync("/api/glucose/settings",
            new JsonObject { ["unit"] = "mgdl", ["targetLow"] = 75, ["targetHigh"] = 180, ["loaded"] = loaded.DeepClone() });

        var saved = await client.PutAsJsonAsync("/api/glucose/settings",
            new JsonObject { ["unit"] = "mgdl", ["targetLow"] = 80, ["targetHigh"] = 180, ["loaded"] = loaded.DeepClone() });

        saved.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await RowOf(server, SugaryServer.Her))!.TargetLow.ShouldBe(75);
    }

    // Unlinking forgets the account; the rest of her log is hers as before.
    [Fact]
    public async Task Unlinking_ForgetsTheLinkAndLeavesHerLog()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        fake.AddAccount(Email, Password).Follow("patient-1", "Ana", "García");
        await using var server = await StartAsync(fake);
        var client = server.As(SugaryServer.Her);
        await client.PutAsJsonAsync("/api/glucose/link", Credentials());
        await client.PostAsJsonAsync("/api/pens",
            new JsonObject { ["kind"] = "rapid", ["insulin"] = "Fiasp", ["openedOn"] = "2026-10-01", ["days"] = 28 });
        await using (var db = server.Services.GetRequiredService<SugaryStore>().For(SugaryServer.Her))
        {
            db.GlucoseReadings.Add(new GlucoseReading
            {
                At = DateTimeOffset.UtcNow, MgPerDl = 120, Trend = global::Domain.DTOs.Sugary.GlucoseTrend.Steady,
                Source = GlucoseSource.Current
            });
            db.GlucoseSensors.Add(new GlucoseSensor { Serial = "SN-1", ActivatedAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var unlinked = await client.DeleteAsync("/api/glucose/link");

        unlinked.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await RowOf(server, SugaryServer.Her)).ShouldBeNull();
        (await client.GetAsync("/api/glucose/link")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await client.GetFromJsonAsync<JsonArray>("/api/pens"))!.Count.ShouldBe(1);
        await using (var db = server.Services.GetRequiredService<SugaryStore>().For(SugaryServer.Her))
        {
            (await db.GlucoseReadings.CountAsync()).ShouldBe(1, "her readings are her log's, and stay");
            (await db.GlucoseSensors.CountAsync()).ShouldBe(1);
        }

        (await client.DeleteAsync("/api/glucose/link")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // A server started without the key links nobody, and says so; the rest of Sugary works.
    [Fact]
    public async Task WithoutASealingKey_LinkingIsOffAndTheRestWorks()
    {
        await using var fake = await FakeLibreLinkUp.StartAsync();
        fake.AddAccount(Email, Password).Follow("patient-1", "Ana", "García");
        await using var server = await StartAsync(fake, sealingKey: "");
        var client = server.As(SugaryServer.Her);

        var linked = await client.PutAsJsonAsync("/api/glucose/link", Credentials());
        var read = await client.GetAsync("/api/glucose/link");
        var pen = await client.PostAsJsonAsync("/api/pens",
            new JsonObject { ["kind"] = "rapid", ["insulin"] = "Fiasp", ["openedOn"] = "2026-10-01", ["days"] = 28 });

        linked.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await CodeOf(linked)).ShouldBe("linking-off");
        read.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await CodeOf(read)).ShouldBe("linking-off");
        fake.Requests.ShouldBeEmpty();
        pen.StatusCode.ShouldBe(HttpStatusCode.Created);
    }
}