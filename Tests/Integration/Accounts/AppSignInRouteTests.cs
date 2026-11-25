using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Web;
using Accounts.Sessions;
using Accounts.Store;
using Domain.Apps;
using Domain.DTOs.Channel;
using Domain.Security;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// A built app's origin has no session of the deployment's, so a navigation there without one comes
// to this route on the main host. The person's own session is checked here (signing in with a passkey
// first if there is none), and they go back to the app's origin with a code: single-use, short-lived
// and bound to that origin, which only the apps server redeems, service to service, for an assertion
// on the `apps` channel naming the session it came from.
public class AppSignInRouteTests
{
    private const string AppsDomain = "apps.example";
    private const string Glucose = "https://glucose-k3x9p2.apps.example";
    private const string AppsCredential = "apps-credential";
    private const string SignalRCredential = "signalr-credential";

    private static Task<AccountsServer> StartAsync() =>
        AccountsServer.StartAsync(s => s with
        {
            AppsDomain = AppsDomain,
            Callers = new Dictionary<string, string>
            {
                ["mcp-apps"] = AppsCredential,
                ["mcp-channel-signalr"] = SignalRCredential
            }
        });

    [Fact]
    public async Task ASignedInPerson_GoesBackToTheAppWithACode_ThatRedeemsToTheirAppsAssertion()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (session, sessionId) = await SessionAsync(server, fran.UserId);

        var response = await OpenAsync(server, $"{Glucose}/week?d=1", session);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var back = response.Headers.Location.ShouldNotBeNull();
        back.GetLeftPart(UriPartial.Path).ShouldBe(Glucose + AppSignIn.CallbackPath);
        var query = HttpUtility.ParseQueryString(back.Query);
        query[AppSignIn.PathParameter].ShouldBe("/week?d=1");

        var redeemed = await RedeemAsync(server, query[AppSignIn.CodeParameter]!, Glucose);

        redeemed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answer = (await redeemed.Content.ReadFromJsonAsync<JsonObject>())!;
        answer["displayName"]!.GetValue<string>().ShouldBe("Fran");
        var claims = (await server.VerifyAssertionAsync(answer["assertion"]!.GetValue<string>())).Claims.ShouldNotBeNull();
        claims.ChannelId.ShouldBe(AppChannels.Apps);
        claims.Account.ShouldBe(new Account(fran.TenantId, fran.UserId));
        claims.SessionId.ShouldBe(sessionId);
    }

    // Nothing but a built app's origin under the apps domain is a return: not the main host, not a
    // look-alike, not the bare domain. Refused before anything is issued.
    [Theory]
    [InlineData("https://assistants.example/")]
    [InlineData("https://evil.com/")]
    [InlineData("https://glucose-k3x9p2.apps.example.evil.com/")]
    [InlineData("https://apps.example/")]
    [InlineData("http://glucose-k3x9p2.apps.example/")]
    [InlineData("")]
    public async Task AReturnThatIsNoBuiltAppsOrigin_IsRefused(string returnUrl)
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (session, _) = await SessionAsync(server, fran.UserId);

        var response = await OpenAsync(server, returnUrl, session);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.Location.ShouldBeNull();
    }

    // A deployment that names no apps domain signs nobody in to an app.
    [Fact]
    public async Task NoAppsDomain_RefusesEveryReturn()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (session, _) = await SessionAsync(server, fran.UserId);

        (await OpenAsync(server, $"{Glucose}/", session)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // Signed out, the person signs in first and comes back here, this route's own address carried
    // as sign-in's return so the app's address survives the trip.
    [Fact]
    public async Task ASignedOutPerson_SignsInFirst_AndComesBackHere()
    {
        await using var server = await StartAsync();

        var response = await OpenAsync(server, $"{Glucose}/week?d=1&x=2", session: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var signIn = response.Headers.Location.ShouldNotBeNull();
        signIn.AbsolutePath.ShouldBe("/accounts/sign-in");
        var back = HttpUtility.ParseQueryString(signIn.Query)["returnUrl"].ShouldNotBeNull();
        back.ShouldStartWith(AppSignIn.Path + "?");
        HttpUtility.ParseQueryString(back[back.IndexOf('?')..])[AppSignIn.ReturnUrlParameter]
            .ShouldBe($"{Glucose}/week?d=1&x=2");
        global::Accounts.Pages.ReturnUrls.Resolve(server.Settings, back).ShouldBe(back);
    }

    [Fact]
    public async Task ACode_IsRedeemedOnce()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var code = await CodeAsync(server, fran.UserId, Glucose);

        (await RedeemAsync(server, code, Glucose)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await RedeemAsync(server, code, Glucose)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // Bound to the origin it was issued for: redeemed for any other, it opens nothing, and it is spent.
    [Fact]
    public async Task ACode_RedeemsOnlyForTheOriginItWasIssuedTo()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var code = await CodeAsync(server, fran.UserId, Glucose);

        (await RedeemAsync(server, code, "https://glucose-k3x9p2-preview.apps.example")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RedeemAsync(server, code, Glucose)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ACode_RunsOut()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var code = await CodeAsync(server, fran.UserId, Glucose);

        server.Clock.Advance(server.Settings.AppSignInCodeLifetime + TimeSpan.FromSeconds(1));

        (await RedeemAsync(server, code, Glucose)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // The code stands for a session, which is asked again as it is redeemed: one signed out meanwhile
    // signs nobody in.
    [Fact]
    public async Task ACodeWhoseSessionEnded_RedeemsToNobody()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (session, sessionId) = await SessionAsync(server, fran.UserId);
        var code = Code(await OpenAsync(server, $"{Glucose}/", session));

        await server.Service<SessionStore>().RevokeAsync(fran.UserId, sessionId, CancellationToken.None);

        (await RedeemAsync(server, code, Glucose)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // Only the apps server redeems: the chat hub's credential asks about its own channel, not apps.
    [Fact]
    public async Task AnotherCallersCredential_IsRefused_AndSpendsNothing()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var code = await CodeAsync(server, fran.UserId, Glucose);

        (await RedeemAsync(server, code, Glucose, SignalRCredential)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await RedeemAsync(server, code, Glucose, "not-a-credential")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await RedeemAsync(server, code, Glucose)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // As every page here: a suspended household's person is told why and issued nothing.
    [Fact]
    public async Task ASuspendedHouseholdsPerson_IsToldSo_AndIssuedNoCode()
    {
        await using var server = await StartAsync();
        var (household, person) = await server.SeedHouseholdAsync("Casa", "Laura");
        var (session, _) = await SessionAsync(server, person);
        await using (var db = await server.Service<IDbContextFactory<AccountsDbContext>>().CreateDbContextAsync())
        {
            await db.Tenants.Where(t => t.Id == household)
                .ExecuteUpdateAsync(t => t.SetProperty(x => x.SuspendedAt, server.Clock.GetUtcNow()));
        }

        var response = await OpenAsync(server, $"{Glucose}/", session);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Headers.Location.ShouldBeNull();
    }

    // The apps server keeps an app session vouched for as the chat hub keeps a connection: by the
    // session refresh, answered on `apps` for the session the code came from while it lives.
    [Fact]
    public async Task TheAppsServer_RenewsBySessionRefresh_OnItsOwnChannel()
    {
        await using var server = await StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (_, sessionId) = await SessionAsync(server, fran.UserId);

        var renewed = await RefreshAsync(server, fran.UserId, sessionId, AppsCredential, AppChannels.Apps);
        await server.Service<SessionStore>().RevokeAsync(fran.UserId, sessionId, CancellationToken.None);
        var revoked = await RefreshAsync(server, fran.UserId, sessionId, AppsCredential, AppChannels.Apps);

        renewed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var claims = (await server.VerifyAssertionAsync(
            (await renewed.Content.ReadFromJsonAsync<JsonObject>())!["assertion"]!.GetValue<string>())).Claims.ShouldNotBeNull();
        claims.ChannelId.ShouldBe(AppChannels.Apps);
        claims.SessionId.ShouldBe(sessionId);
        revoked.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // A caller names the channel it asks for, which it must be allowed: a host run hands every server
    // one credential, which is then the chat hub's and the apps server's at once and must still get
    // the channel it asked for. The apps server's own is refused the chat hub's.
    [Fact]
    public async Task ARefreshNamingItsChannel_IsAnsweredOnIt_OnlyForACallerThatMayAskAboutIt()
    {
        await using var server = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string>
            {
                ["mcp-channel-signalr"] = "local-mcp-secret", ["mcp-apps"] = "local-mcp-secret", ["mcp-sugary"] = AppsCredential
            }
        });
        var fran = await server.MintOperatorAsync("Fran");
        var (_, sessionId) = await SessionAsync(server, fran.UserId);

        var shared = await RefreshAsync(server, fran.UserId, sessionId, "local-mcp-secret", AppChannels.Apps);
        var unnamed = await RefreshAsync(server, fran.UserId, sessionId, "local-mcp-secret", channel: null);

        shared.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ChannelOfAsync(server, shared)).ShouldBe(AppChannels.Apps);
        (await ChannelOfAsync(server, unnamed)).ShouldBe(ChannelIds.SignalR);
        (await RefreshAsync(server, fran.UserId, sessionId, AppsCredential, ChannelIds.SignalR)).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await RefreshAsync(server, fran.UserId, sessionId, "local-mcp-secret", "voice")).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }

    private static async Task<string?> ChannelOfAsync(AccountsServer server, HttpResponseMessage response) =>
        (await server.VerifyAssertionAsync((await response.Content.ReadFromJsonAsync<JsonObject>())!["assertion"]!.GetValue<string>()))
        .Claims?.ChannelId;

    private static async Task<(string Cookie, string Id)> SessionAsync(AccountsServer server, string userId)
    {
        var issued = (await server.Service<SessionStore>().IssueAsync(userId, null, "phone", CancellationToken.None)).ShouldNotBeNull();
        return (issued.Token, issued.Id);
    }

    private static async Task<string> CodeAsync(AccountsServer server, string userId, string origin)
    {
        var (session, _) = await SessionAsync(server, userId);
        return Code(await OpenAsync(server, $"{origin}/", session));
    }

    private static string Code(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        return HttpUtility.ParseQueryString(response.Headers.Location.ShouldNotBeNull().Query)[AppSignIn.CodeParameter]
            .ShouldNotBeNull();
    }

    // The browser arriving from an app origin, with its session cookie when it holds one.
    private static Task<HttpResponseMessage> OpenAsync(AccountsServer server, string returnUrl, string? session)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"{AppSignIn.Path}?{AppSignIn.ReturnUrlParameter}={Uri.EscapeDataString(returnUrl)}");
        request.Headers.Accept.ParseAdd("text/html");
        if (session is not null)
        {
            request.Headers.Add("Cookie", $"{SessionCookie.Name}={session}");
        }

        return server.Client().SendAsync(request);
    }

    private static Task<HttpResponseMessage> RedeemAsync(
        AccountsServer server, string code, string origin, string credential = AppsCredential)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/accounts/internal/app-sign-in")
        {
            Content = JsonContent.Create(new { code, origin })
        };
        request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(credential));
        return server.Client().SendAsync(request);
    }

    private static Task<HttpResponseMessage> RefreshAsync(
        AccountsServer server, string userId, string sessionId, string credential, string? channel)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/accounts/internal/session")
        {
            Content = JsonContent.Create(new { userId, sessionId, channel })
        };
        request.Headers.TryAddWithoutValidation("Authorization", SharedSecret.Header(credential));
        return server.Client().SendAsync(request);
    }
}