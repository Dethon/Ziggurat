using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Accounts.Sessions;
using Accounts.Store;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Infrastructure.Clients.Accounts;
using McpServerSugary.Channel;
using McpServerSugary.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Tests.Integration.Fixtures;
using GlucoseSource = Domain.DTOs.Sugary.GlucoseSource;
using GlucoseTrend = Domain.DTOs.Sugary.GlucoseTrend;
using GlucoseUnit = Domain.DTOs.Sugary.GlucoseUnit;

namespace Tests.Integration.McpServerTests;

// A home screen widget cannot hold a session or send a header, so it reads one person's glucose by a
// token in its address. The token opens these two read-only answers and nothing else, is kept only as
// a hash, and lives no longer than what vouched for it: the session that made it, the household
// running, Sugary being on for the person, and a reader's grant over the log it names.
public class SugaryWidgetApiTests
{
    private const string Session = "session-1";

    private static readonly Account Reader = new("household-home", "user-reader");

    private sealed class Sessions : ISessionAccounts
    {
        public bool Ended { get; set; }
        public bool Down { get; set; }
        public bool Suspended { get; set; }

        public Task<ResolvedAccount?> SessionAsync(string userId, string sessionId, CancellationToken ct)
        {
            if (Down)
            {
                throw new HttpRequestException("The accounts service could not answer");
            }

            if (Ended)
            {
                return Task.FromResult<ResolvedAccount?>(null);
            }

            var account = new Account("household-home", userId);
            return Task.FromResult<ResolvedAccount?>(new ResolvedAccount(
                account, "Somebody", TestAssertions.Of(account, SugaryChannel.ChannelId, ["member"], sessionId: sessionId), Suspended));
        }
    }

    private static List<SugaryPerson> People() =>
    [
        new(SugaryServer.Her.UserId, "Her", ReadsEveryLog: false, UsesSugary: true),
        new(SugaryServer.Him.UserId, "Him", ReadsEveryLog: false, UsesSugary: true),
        new(Reader.UserId, "Reader", ReadsEveryLog: true, UsesSugary: true)
    ];

    private static Task<SugaryServer> StartAsync(Sessions sessions, List<SugaryPerson>? people = null) =>
        SugaryServer.StartAsync(
            sugaryPeople: new HouseholdSugaryPeople(people ?? People()),
            services: s => s.Replace(ServiceDescriptor.Singleton<ISessionAccounts>(sessions)));

    private static async Task<string> MintAsync(SugaryServer server, Account? person = null, string? ofPerson = null)
    {
        var address = ofPerson is null ? "/api/widget" : $"/api/widget?person={ofPerson}";
        var response = await server.As(person ?? SugaryServer.Her, sessionId: Session).PutAsync(address, null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!["token"]!.GetValue<string>();
    }

    private static GlucoseReading MinutesAgo(double minutes, double mgPerDl, GlucoseTrend trend = GlucoseTrend.Steady) =>
        new() { At = DateTimeOffset.UtcNow.AddMinutes(-minutes), MgPerDl = mgPerDl, Trend = trend, Source = GlucoseSource.Graph };

    private static async Task SeedAsync(SugaryServer server, Account person, params GlucoseReading[] readings)
    {
        await using var db = server.Services.GetRequiredService<SugaryStore>().For(person);
        db.GlucoseReadings.AddRange(readings);
        await db.SaveChangesAsync();
    }

    private static Task<HttpResponseMessage> NowAsync(SugaryServer server, string token, string query = "") =>
        server.Browser().GetAsync($"/widget/{token}/now.json{query}");

    private static async Task<JsonNode> NowBodyAsync(SugaryServer server, string token, string query = "")
    {
        var response = await NowAsync(server, token, query);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
    }

    [Fact]
    public async Task TheWidget_ReadsHerNewestReading_WithNoSignIn()
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);
        var newest = MinutesAgo(2, 212, GlucoseTrend.RisingFast);
        await SeedAsync(server, SugaryServer.Her, MinutesAgo(7, 180), newest);
        var token = await MintAsync(server);

        var now = await NowBodyAsync(server, token);

        now["value"]!.GetValue<string>().ShouldBe("212");
        now["unit"]!.GetValue<string>().ShouldBe("mg/dL");
        now["arrow"]!.GetValue<string>().ShouldBe("↑");
        now["band"]!.GetValue<string>().ShouldBe("high");
        // Whole seconds in UTC, the ISO shape KWGT's date parser documents: it works out the age itself.
        var at = now["at"]!.GetValue<string>();
        at.ShouldMatch(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$");
        DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture)
            .ShouldBe(newest.At, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task TheWidget_ReadsInHerUnit()
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);
        await SeedAsync(server, SugaryServer.Her, MinutesAgo(1, 108.1));
        await using (var db = server.Services.GetRequiredService<SugaryStore>().For(SugaryServer.Her))
        {
            db.GlucoseLinks.Add(new GlucoseLink
            {
                Email = "ana@example.com",
                SealedPassword = [1],
                PatientId = "patient-1",
                LinkedAt = DateTimeOffset.UtcNow,
                Unit = GlucoseUnit.Mmol
            });
            await db.SaveChangesAsync();
        }

        var now = await NowBodyAsync(server, await MintAsync(server));

        now["value"]!.GetValue<string>().ShouldBe("6.0");
        now["unit"]!.GetValue<string>().ShouldBe("mmol/L");
        now["band"]!.GetValue<string>().ShouldBe("inRange");
    }

    [Fact]
    public async Task NoReadingYet_SaysSo_NeverAValue()
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);

        var now = await NowBodyAsync(server, await MintAsync(server));

        now["at"].ShouldBeNull();
        now["mgPerDl"].ShouldBeNull();
        now["value"]!.GetValue<string>().ShouldBe("--");
        now["band"]!.GetValue<string>().ShouldBe("none");
    }

    [Fact]
    public async Task AnAnswer_IsNeverKeptByACache()
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);

        var response = await NowAsync(server, await MintAsync(server));

        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task TheToken_IsKeptOnlyAsAHash()
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);

        var token = await MintAsync(server);

        // Read once: SQLite removes its -shm and -wal files as connections close, so one listed a moment
        // ago may be gone by the time it is opened, and a second enumeration would list them again.
        var stored = Directory.GetFiles(server.Directory, "sugary.db*")
            .Select(ReadIfStillThere)
            .OfType<string>()
            .ToList();
        stored.ShouldNotBeEmpty();
        stored.ShouldAllBe(contents => !contents.Contains(token));
    }

    private static string? ReadIfStillThere(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return Encoding.Latin1.GetString(copy.ToArray());
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    [Fact]
    public async Task AnUnknownToken_Is404()
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);

        (await NowAsync(server, "not-a-token")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MintingAgain_EndsTheTokenBefore()
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);
        var first = await MintAsync(server);

        var second = await MintAsync(server);

        second.ShouldNotBe(first);
        (await NowAsync(server, first)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await NowAsync(server, second)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TheAppSeesWhetherAWidgetExists_AndCanRemoveIt_NeverItsToken()
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);
        var phone = server.As(SugaryServer.Her, sessionId: Session);
        (await phone.GetFromJsonAsync<JsonNode>("/api/widget"))!["createdAt"].ShouldBeNull();
        var token = await MintAsync(server);

        var state = (await phone.GetFromJsonAsync<JsonNode>("/api/widget"))!;
        var removed = await phone.DeleteAsync("/api/widget");

        state["createdAt"].ShouldNotBeNull();
        state["token"].ShouldBeNull();
        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await NowAsync(server, token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ARequestNamingNoSession_MintsNothing()
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);

        var response = await server.As(SugaryServer.Her).PutAsync("/api/widget", null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonNode>())!["code"]!.GetValue<string>().ShouldBe("no-session");
    }

    [Fact]
    public async Task TheSessionEnding_EndsTheToken_ForGood()
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);
        var token = await MintAsync(server);

        sessions.Ended = true;
        var ended = await NowAsync(server, token);
        sessions.Ended = false;
        var after = await NowAsync(server, token);

        ended.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        after.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnAccountsOutage_ShowsNothing_AndKeepsTheToken()
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);
        var token = await MintAsync(server);

        sessions.Down = true;
        var during = await NowAsync(server, token);
        sessions.Down = false;
        var after = await NowAsync(server, token);

        during.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        after.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ASuspendedHousehold_ReadsNothing_UntilResumed()
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);
        var token = await MintAsync(server);

        sessions.Suspended = true;
        var during = await NowAsync(server, token);
        sessions.Suspended = false;
        var after = await NowAsync(server, token);

        during.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        after.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SugaryTurnedOffForHer_ReadsNothing()
    {
        var sessions = new Sessions();
        var people = People();
        await using var server = await StartAsync(sessions, people);
        var token = await MintAsync(server);

        people[0] = people[0] with { UsesSugary = false };

        (await NowAsync(server, token)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AReadersToken_ReadsThePersonItNames_UntilTheGrantIsWithdrawn()
    {
        var sessions = new Sessions();
        var people = People();
        await using var server = await StartAsync(sessions, people);
        await SeedAsync(server, SugaryServer.Her, MinutesAgo(1, 99));
        await SeedAsync(server, Reader, MinutesAgo(1, 140));
        var token = await MintAsync(server, Reader, SugaryServer.Her.UserId);

        var read = await NowBodyAsync(server, token);
        people[2] = people[2] with { ReadsEveryLog = false };
        var withdrawn = await NowAsync(server, token);

        read["value"]!.GetValue<string>().ShouldBe("99");
        withdrawn.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SomebodyWithoutTheGrant_MintsNoTokenForAnotherLog()
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);

        var response = await server.As(SugaryServer.Him, sessionId: Session)
            .PutAsync($"/api/widget?person={SugaryServer.Her.UserId}", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // The address is the whole credential, so nothing added to it may widen what it reads.
    [Fact]
    public async Task APersonAddedToTheAddress_SaysNothing()
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);
        await SeedAsync(server, SugaryServer.Her, MinutesAgo(1, 99));
        await SeedAsync(server, SugaryServer.Him, MinutesAgo(1, 140));
        var token = await MintAsync(server);

        var now = await NowBodyAsync(server, token, $"?person={SugaryServer.Him.UserId}");

        now["value"]!.GetValue<string>().ShouldBe("99");
    }

    // The same stops, asked of the accounts service itself over its wire on Sugary's own credential.
    [Fact]
    public async Task AgainstTheAccountsService_ATokenLivesWithItsSession_AndWaitsOutASuspension()
    {
        const string credential = "sugary-credential";
        await using var accounts = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["mcp-sugary"] = credential }
        });
        var fran = await accounts.MintOperatorAsync("Fran");
        var sessions = accounts.Service<SessionStore>();
        var phone = (await sessions.IssueAsync(fran.UserId, null, "phone", CancellationToken.None)).ShouldNotBeNull();
        var client = new AccountsResolverClient(new HttpClient { BaseAddress = accounts.Address }, credential);
        await using var server = await SugaryServer.StartAsync(
            services: s => s.Replace(ServiceDescriptor.Singleton<ISessionAccounts>(client)));
        var person = new Account(fran.TenantId, fran.UserId);
        var minted = await server.As(person, sessionId: phone.Id).PutAsync("/api/widget", null);
        minted.StatusCode.ShouldBe(HttpStatusCode.OK, await minted.Content.ReadAsStringAsync());
        var token = (await minted.Content.ReadFromJsonAsync<JsonNode>())!["token"]!.GetValue<string>();

        var live = await NowAsync(server, token);
        await SuspendAsync(accounts, suspended: true);
        var suspended = await NowAsync(server, token);
        await SuspendAsync(accounts, suspended: false);
        var resumed = await NowAsync(server, token);
        await sessions.RevokeAsync(fran.UserId, phone.Id, CancellationToken.None);
        var revoked = await NowAsync(server, token);

        live.StatusCode.ShouldBe(HttpStatusCode.OK);
        suspended.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        resumed.StatusCode.ShouldBe(HttpStatusCode.OK);
        revoked.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task SuspendAsync(AccountsServer accounts, bool suspended)
    {
        await using var db = await accounts.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync();
        await db.Tenants.ExecuteUpdateAsync(t => t.SetProperty(
            x => x.SuspendedAt, suspended ? accounts.Clock.GetUtcNow() : (DateTimeOffset?)null));
    }

    [Fact]
    public async Task TheCurve_IsAnImageOfTheSizeAsked()
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);
        await SeedAsync(server, SugaryServer.Her,
            [.. Enumerable.Range(0, 24).Select(i => MinutesAgo(i * 5, 120 + (i * 3)))]);
        var token = await MintAsync(server);

        var response = await server.Browser().GetAsync($"/widget/{token}/curve.png?hours=3&width=400&height=160");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var png = await response.Content.ReadAsByteArrayAsync();
        png[..8].ShouldBe(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)).ShouldBe(400);
        BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)).ShouldBe(160);
    }

    [Theory]
    [InlineData("hours=5")]
    [InlineData("width=10")]
    [InlineData("height=90000")]
    [InlineData("theme=neon")]
    public async Task ACurveNobodyOffers_IsRefused(string query)
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);
        var token = await MintAsync(server);

        (await server.Browser().GetAsync($"/widget/{token}/curve.png?{query}")).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TheCard_IsAnImageOfTheSizeAsked_WhateverElseItsAddressCarries()
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);
        await SeedAsync(server, SugaryServer.Her, MinutesAgo(2, 142));
        var token = await MintAsync(server);

        var response = await server.Browser()
            .GetAsync($"/widget/{token}/card.png?hours=3&width=400&height=160&t=2611232253");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        var png = await response.Content.ReadAsByteArrayAsync();
        BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)).ShouldBe(400);
        BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)).ShouldBe(160);
    }

    [Fact]
    public async Task TheCard_ChangesWhenANewReadingArrives()
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);
        await SeedAsync(server, SugaryServer.Her, MinutesAgo(10, 142));
        var token = await MintAsync(server);
        var before = await server.Browser().GetByteArrayAsync($"/widget/{token}/card.png?hours=3");

        await SeedAsync(server, SugaryServer.Her, MinutesAgo(1, 65, GlucoseTrend.FallingFast));
        var after = await server.Browser().GetByteArrayAsync($"/widget/{token}/card.png?hours=3");

        after.ShouldNotBe(before);
    }

    [Fact]
    public async Task TheCard_WithNoReadings_IsStillAnImage()
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);
        var token = await MintAsync(server);

        var response = await server.Browser().GetAsync($"/widget/{token}/card.png");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
    }

    [Theory]
    [InlineData("hours=5")]
    [InlineData("width=10")]
    [InlineData("height=90000")]
    [InlineData("theme=neon")]
    public async Task ACardNobodyOffers_IsRefused(string query)
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);
        var token = await MintAsync(server);

        (await server.Browser().GetAsync($"/widget/{token}/card.png?{query}")).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnUnknownToken_GetsNoCard()
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);

        (await server.Browser().GetAsync("/widget/not-a-token/card.png")).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task TheTokenOpensNothingOfTheApi()
    {
        var sessions = new Sessions();
        await using var server = await StartAsync(sessions);
        var token = await MintAsync(server);

        var browser = server.Browser();
        browser.DefaultRequestHeaders.Authorization = new("Bearer", token);

        (await browser.GetAsync("/api/glucose/current")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await server.Browser().GetAsync($"/api/glucose/current?token={token}")).StatusCode
            .ShouldBe(HttpStatusCode.Unauthorized);
    }
}