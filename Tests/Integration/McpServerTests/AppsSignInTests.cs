using System.Net;
using System.Web;
using Accounts.Sessions;
using Accounts.Settings;
using Domain.Apps;
using Domain.DTOs.Channel;
using Domain.Security;
using Domain.Security.Signing;
using McpServerApps.Serving;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// A person signed in to Ziggurat opens a built app's link and lands in it, signed in, with no second
// passkey: the app's origin sends a navigation with no app session of its own to the accounts
// service's app sign-in on the main host, which sends the person back with a code the apps server
// redeems, service to service, for an `apps` assertion it believes and nothing else; it then sets its
// own cookie. The app session is renewed by the session refresh and ends when the person's sessions
// end. Whether that person may open that app is the apps server's own decision.
public class AppsSignInTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    // The proxy's scheme, which the apps server names its origin by.
    private const string Https = "https";

    [Fact]
    public async Task ASessionlessNavigation_IsSentToTheAppSignIn_CarryingWhereItWasGoing()
    {
        await using var server = await AppsServer.StartAsync();
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        var host = server.Published("glucose", id);

        var response = await server.GetAsync(host, "/week?d=1", navigate: true);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var signIn = response.Headers.Location.ShouldNotBeNull();
        signIn.GetLeftPart(UriPartial.Path).ShouldBe(AppsServer.MainHost + AppSignIn.Path);
        HttpUtility.ParseQueryString(signIn.Query)[AppSignIn.ReturnUrlParameter].ShouldBe($"https://{host}/week?d=1");
        response.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
    }

    // Whether an app is there is nobody's to learn before signing in: a label naming no app is sent
    // the same way, and so is a navigation whose session ran out or was never this server's. Anything
    // but a page being opened is the one 404.
    [Fact]
    public async Task EverySessionlessNavigation_IsSentToSignIn_ThereOrNot_AndNothingElseIs()
    {
        await using var server = await AppsServer.StartAsync();
        var id = AppsServer.IdIn(await server.AppAsync(AppsServer.Her, "glucose"));
        var host = server.Published("glucose", id);
        var nowhere = $"glucose-zzzzzz.{AppsServer.AppsDomain}";

        (await server.GetAsync(nowhere, "/", navigate: true)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await server.GetAsync(host, "/", "no-such-session", navigate: true)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await server.GetAsync(host, "/app.js")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await server.GetAsync($"glucose.{AppsServer.AppsDomain}", "/", navigate: true)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ASignedInPerson_OpensTheirApp_ThroughTheAppSignIn_WithNoSecondPasskey()
    {
        await using var accounts = await AccountsAsync();
        var fran = await accounts.MintOperatorAsync("Fran");
        var author = new Account(fran.TenantId, fran.UserId);
        await using var apps = await AppsFor(accounts);
        var host = await PublishedAsync(apps, author);
        var session = await accounts.MintSessionAsync(fran.UserId);

        var cookie = await SignInAsync(apps, accounts, host, "/week?d=1", session);

        cookie.Location.ShouldBe("/week?d=1");
        cookie.SetCookie.ShouldStartWith($"{AppSessions.CookieName}=");
        cookie.SetCookie.ToLowerInvariant().ShouldContain("secure");
        cookie.SetCookie.ToLowerInvariant().ShouldContain("httponly");
        cookie.SetCookie.ToLowerInvariant().ShouldContain("samesite=lax");
        cookie.SetCookie.ToLowerInvariant().ShouldContain("path=/");
        cookie.SetCookie.ToLowerInvariant().ShouldNotContain("domain=");
        var page = await apps.GetAsync(host, "/", cookie.Token, navigate: true);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await page.Content.ReadAsStringAsync()).ShouldContain("<h1>Glucose</h1>");
    }

    // The accounts service vouches for who the person is; whether they may open this app is the apps
    // server's: anyone but its author, signed in all the same, gets the one 404.
    [Fact]
    public async Task SomebodyElseSignedIn_GetsThe404()
    {
        await using var accounts = await AccountsAsync();
        var fran = await accounts.MintOperatorAsync("Fran");
        var laura = await accounts.SeedMemberAsync("Laura");
        await using var apps = await AppsFor(accounts);
        var host = await PublishedAsync(apps, new Account(fran.TenantId, fran.UserId));

        var cookie = await SignInAsync(apps, accounts, host, "/", await accounts.MintSessionAsync(laura));

        (await apps.GetAsync(host, "/", cookie.Token, navigate: true)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // A code is redeemed once: the callback reloaded, or a code brought from elsewhere, signs nobody
    // in, says so, and is never sent round to sign in again.
    [Fact]
    public async Task ACodeRedeemedAlready_SignsNobodyIn()
    {
        await using var accounts = await AccountsAsync();
        var fran = await accounts.MintOperatorAsync("Fran");
        await using var apps = await AppsFor(accounts);
        var host = await PublishedAsync(apps, new Account(fran.TenantId, fran.UserId));
        var callback = await CallbackAsync(apps, accounts, host, "/", await accounts.MintSessionAsync(fran.UserId));
        (await apps.GetAsync(host, callback, navigate: true)).StatusCode.ShouldBe(HttpStatusCode.SeeOther);

        var again = await apps.GetAsync(host, callback, navigate: true);

        again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        again.Headers.Location.ShouldBeNull();
        again.Headers.Contains("Set-Cookie").ShouldBeFalse();
    }

    // A code bound to one origin opens no other, the same app's preview included.
    [Fact]
    public async Task ACodeBroughtToAnotherOrigin_SignsNobodyIn()
    {
        await using var accounts = await AccountsAsync();
        var fran = await accounts.MintOperatorAsync("Fran");
        await using var apps = await AppsFor(accounts);
        var host = await PublishedAsync(apps, new Account(fran.TenantId, fran.UserId));
        var callback = await CallbackAsync(apps, accounts, host, "/", await accounts.MintSessionAsync(fran.UserId));
        var preview = host.Insert(host.IndexOf('.'), AppOrigin.PreviewSuffix);

        (await apps.GetAsync(preview, callback, navigate: true)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await apps.GetAsync(host, callback, navigate: true)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // Every way a person's sessions end is announced, and the apps server ends every app session of
    // theirs at once: a page reloaded after signing out elsewhere is sent to sign in.
    [Fact]
    public async Task SigningOut_EndsThePersonsAppSessions()
    {
        await using var accounts = await AccountsAsync(redis.ConnectionString);
        var fran = await accounts.MintOperatorAsync("Fran");
        await using var apps = await AppsFor(accounts, redis.ConnectionString);
        var host = await PublishedAsync(apps, new Account(fran.TenantId, fran.UserId));
        var session = await accounts.MintSessionAsync(fran.UserId);
        var cookie = await SignInAsync(apps, accounts, host, "/", session);
        (await apps.GetAsync(host, "/", cookie.Token, navigate: true)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await accounts.SignedIn(session).PostAsync("/accounts/account", "/accounts/sign-out")).StatusCode
            .ShouldBe(HttpStatusCode.SeeOther);

        await Eventually.Until(async ValueTask<bool> () =>
            (await apps.GetAsync(host, "/", cookie.Token, navigate: true)).StatusCode == HttpStatusCode.Redirect,
            "the app session to end with the sessions it was made from");
    }

    // Untold — an announcement missed — the app session still stops once its assertion is due for
    // renewal: the session refresh answers nobody for a revoked session, and nothing is served on it.
    [Fact]
    public async Task ARevokedSession_IsNeverRenewed()
    {
        await using var accounts = await AccountsAsync();
        var fran = await accounts.MintOperatorAsync("Fran");
        await using var apps = await AppsFor(accounts);
        var host = await PublishedAsync(apps, new Account(fran.TenantId, fran.UserId));
        var phone = (await accounts.Service<SessionStore>().IssueAsync(fran.UserId, null, "phone", CancellationToken.None))!;
        var cookie = await SignInAsync(apps, accounts, host, "/", phone.Token);
        await accounts.Service<SessionStore>().RevokeAsync(fran.UserId, phone.Id, CancellationToken.None);
        (await apps.GetAsync(host, "/", cookie.Token, navigate: true)).StatusCode.ShouldBe(HttpStatusCode.OK,
            "until its assertion is due, nothing asks");

        accounts.Clock.Advance(apps.Settings.Sessions.AssertionKept);

        (await apps.GetAsync(host, "/", cookie.Token, navigate: true)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await apps.GetAsync(host, "/", cookie.Token)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // A live session's assertion is renewed as it comes due, so the app session always holds one the
    // agent would believe, for the session it was made from.
    [Fact]
    public async Task ALiveSession_IsRenewedAsItsAssertionComesDue()
    {
        await using var accounts = await AccountsAsync();
        var fran = await accounts.MintOperatorAsync("Fran");
        await using var apps = await AppsFor(accounts);
        var host = await PublishedAsync(apps, new Account(fran.TenantId, fran.UserId));
        var phone = (await accounts.Service<SessionStore>().IssueAsync(fran.UserId, null, "phone", CancellationToken.None))!;
        var cookie = await SignInAsync(apps, accounts, host, "/", phone.Token);
        var origin = AppOrigin.Parse(host, AppsServer.AppsDomain)!;
        var first = apps.Sessions.Find(cookie.Token, origin).ShouldNotBeNull().Assertion;

        accounts.Clock.Advance(accounts.Settings.AssertionLifetime + TimeSpan.FromMinutes(1));

        (await apps.GetAsync(host, "/", cookie.Token, navigate: true)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var renewed = apps.Sessions.Find(cookie.Token, origin).ShouldNotBeNull().Assertion;
        renewed.ShouldNotBe(first);
        var claims = (await accounts.VerifyAssertionAsync(renewed)).Claims.ShouldNotBeNull();
        claims.ChannelId.ShouldBe(AppChannels.Apps);
        claims.SessionId.ShouldBe(phone.Id);
    }

    // An outage is not a revocation, and not a renewal either: nothing is served on an assertion the
    // service could not renew, and the session is kept for when it answers again.
    [Fact]
    public async Task AnAccountsServiceThatCannotBeAsked_ServesNothing_AndEndsNothing()
    {
        var accounts = await AccountsAsync();
        var fran = await accounts.MintOperatorAsync("Fran");
        await using var apps = await AppsFor(accounts);
        var host = await PublishedAsync(apps, new Account(fran.TenantId, fran.UserId));
        var cookie = await SignInAsync(apps, accounts, host, "/", await accounts.MintSessionAsync(fran.UserId));
        var clock = accounts.Clock;
        await accounts.DisposeAsync();

        clock.Advance(apps.Settings.Sessions.AssertionKept);

        (await apps.GetAsync(host, "/", cookie.Token, navigate: true)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        apps.Sessions.Find(cookie.Token, AppOrigin.Parse(host, AppsServer.AppsDomain)!).ShouldNotBeNull();
    }

    private static Task<AccountsServer> AccountsAsync(string? redisConnection = null) =>
        AccountsServer.StartAsync(s => s with
        {
            AppsDomain = AppsServer.AppsDomain,
            // The suite's accounts key, which the apps server's verifier holds (TestAssertions).
            Signing = new SigningKeySettings { PrivateKey = TestSigningKeys.Accounts },
            Callers = new Dictionary<string, string> { ["mcp-apps"] = McpTestSecret.Gate.SharedSecret },
            Redis = new RedisSettings { ConnectionString = redisConnection ?? "" }
        });

    // The apps server pointed at that accounts service, for its browser route and its internal API,
    // on its clock.
    private static Task<AppsServer> AppsFor(AccountsServer accounts, string? redisConnection = null) =>
        AppsServer.StartAsync(
            s => s with
            {
                PublicBaseUrl = accounts.Address.ToString().TrimEnd('/'),
                Accounts = s.Accounts with { BaseUrl = accounts.Address.ToString() },
                RedisConnectionString = redisConnection ?? ""
            },
            services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(accounts.Clock)));

    private static async Task<string> PublishedAsync(AppsServer apps, Account author)
    {
        var id = AppsServer.IdIn(await apps.AppAsync(author, "glucose"));
        await apps.CreateAsync(author, "/glucose/index.html", "<h1>Glucose</h1>");
        await apps.ExecAsync(author, "/glucose", "./publish");
        return apps.Published("glucose", id);
    }

    // The browser's round trip up to its arrival back on the app's origin: the app's redirect, the
    // accounts service's answer to this person's session cookie. The callback's path and query.
    private static async Task<string> CallbackAsync(AppsServer apps, AccountsServer accounts, string host, string path, string session)
    {
        var toSignIn = await apps.GetAsync(host, path, navigate: true);
        toSignIn.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        using var request = new HttpRequestMessage(HttpMethod.Get, toSignIn.Headers.Location);
        request.Headers.Add("Cookie", $"{SessionCookie.Name}={session}");
        request.Headers.Accept.ParseAdd("text/html");
        var back = await accounts.Client().SendAsync(request);
        back.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var callback = back.Headers.Location.ShouldNotBeNull();
        callback.Scheme.ShouldBe(Https);
        callback.Host.ShouldBe(host);
        return callback.PathAndQuery;
    }

    private static async Task<(string Token, string SetCookie, string Location)> SignInAsync(
        AppsServer apps, AccountsServer accounts, string host, string path, string session)
    {
        var arrived = await apps.GetAsync(host, await CallbackAsync(apps, accounts, host, path, session), navigate: true);
        arrived.StatusCode.ShouldBe(HttpStatusCode.SeeOther);
        var setCookie = arrived.Headers.GetValues("Set-Cookie").ShouldHaveSingleItem();
        var token = setCookie.Split(';')[0][(AppSessions.CookieName.Length + 1)..];
        return (token, setCookie, arrived.Headers.Location.ShouldNotBeNull().OriginalString);
    }
}