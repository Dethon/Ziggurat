using System.Net;
using Accounts.Passkeys;
using Accounts.Sessions;
using Microsoft.Playwright;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// Enrolling and signing in the way a person does, in Chromium with a virtual authenticator standing
// in for the phone's: a platform authenticator that keeps discoverable credentials and verifies its
// user. The service runs in-process on localhost, which the browser treats as a secure context.
public class PasskeyCeremoniesTests : IAsyncLifetime
{
    private const string CreateButton = "Create a passkey";
    private const string SignInButton = "Sign in with a passkey";

    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = Environment.GetEnvironmentVariable("PLAYWRIGHT_HEADLESS") != "false"
        });
    }

    public async Task DisposeAsync()
    {
        await _browser.CloseAsync();
        _playwright.Dispose();
    }

    [Fact]
    public async Task AMintedLink_EnrollsAPasskeyOnceAndThenRefuses()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (context, page) = await DeviceAsync();

        await EnrollAsync(server, page, fran);

        var session = await SessionOfAsync(context);
        var check = await server.VerifyAsync(session);
        check.StatusCode.ShouldBe(HttpStatusCode.OK);
        check.Headers.GetValues("X-Ziggurat-User").ShouldBe([fran.UserId]);

        var again = await page.GotoAsync(fran.Url.ToString());
        again!.Status.ShouldBe((int)HttpStatusCode.Gone);
        (await page.ContentAsync()).ShouldContain("can no longer be used");
        (await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = CreateButton }).CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task TheSessionCookie_IsHttpOnlySecureAndLax()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (context, page) = await DeviceAsync();

        await EnrollAsync(server, page, fran);

        var cookie = (await context.CookiesAsync()).Single(c => c.Name == SessionCookie.Name);
        cookie.HttpOnly.ShouldBeTrue();
        cookie.Secure.ShouldBeTrue();
        cookie.SameSite.ShouldBe(SameSiteAttribute.Lax);
        // Until revoked: the longest a browser keeps a cookie, which is 400 days.
        DateTimeOffset.FromUnixTimeSeconds((long)cookie.Expires).ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(399));
    }

    [Fact]
    public async Task SigningIn_AsksForNoUsernameAndReturnsSignedIn()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (context, page) = await DeviceAsync();
        await EnrollAsync(server, page, fran);
        await context.ClearCookiesAsync();
        var back = new Uri(server.Address, "/chat?topic=7").ToString();

        await page.GotoAsync(new Uri(server.Address, "/accounts/sign-in?returnUrl=" + Uri.EscapeDataString(back)).ToString());
        (await page.Locator("input").CountAsync()).ShouldBe(0);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = SignInButton }).ClickAsync();
        await page.WaitForURLAsync(back);

        var check = await server.VerifyAsync(await SessionOfAsync(context));
        check.StatusCode.ShouldBe(HttpStatusCode.OK);
        check.Headers.GetValues("X-Ziggurat-User").ShouldBe([fran.UserId]);
        check.Headers.GetValues("X-Ziggurat-Tenant").ShouldBe([fran.TenantId]);
        check.Headers.GetValues("X-Ziggurat-Roles").ShouldBe(["owner,operator"]);
    }

    // A crafted sign-in link must not hand a fresh session's redirect to a site that is not ours.
    [Fact]
    public async Task SigningIn_ReturnsOnlyToTheDeploymentsOwnOrigins()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (context, page) = await DeviceAsync();
        await EnrollAsync(server, page, fran);
        await context.ClearCookiesAsync();

        await page.GotoAsync(new Uri(server.Address,
            "/accounts/sign-in?returnUrl=" + Uri.EscapeDataString("https://elsewhere.example/steal")).ToString());
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = SignInButton }).ClickAsync();

        await page.WaitForURLAsync(server.Address.ToString());
    }

    [Fact]
    public async Task RevokingAPasskey_EndsEverySessionItBeganAndSignsInNoMore()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (context, page) = await DeviceAsync();
        await EnrollAsync(server, page, fran);
        var enrolled = await SessionOfAsync(context);
        await context.ClearCookiesAsync();
        await SignInAsync(server, page);
        var signedIn = await SessionOfAsync(context);
        var minted = await server.MintSessionAsync(fran.UserId);
        var passkeys = server.Service<PasskeyStore>();
        var passkey = (await passkeys.ListAsync(fran.UserId, CancellationToken.None)).ShouldHaveSingleItem();

        (await passkeys.RevokeAsync(fran.UserId, passkey.Id, CancellationToken.None)).ShouldBeTrue();

        (await server.VerifyAsync(enrolled, accept: "application/json")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await server.VerifyAsync(signedIn, accept: "application/json")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await server.VerifyAsync(minted)).StatusCode.ShouldBe(HttpStatusCode.OK, "a session the passkey did not begin");
        (await passkeys.ListAsync(fran.UserId, CancellationToken.None)).ShouldBeEmpty();

        await context.ClearCookiesAsync();
        await page.GotoAsync(new Uri(server.Address, "/accounts/sign-in").ToString());
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = SignInButton }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Status)).ToContainTextAsync("not recognised");
        (await context.CookiesAsync()).ShouldNotContain(c => c.Name == SessionCookie.Name);
    }

    // A recovery link adds a second passkey to the same person rather than making a new one.
    [Fact]
    public async Task ARecoveryLink_AddsAPasskeyToTheSamePerson()
    {
        await using var server = await AccountsServer.StartAsync();
        var fran = await server.MintOperatorAsync("Fran");
        var (_, phone) = await DeviceAsync();
        await EnrollAsync(server, phone, fran);
        var recovery = await server.MintOperatorAsync("Fran");
        var (laptopContext, laptop) = await DeviceAsync();

        await EnrollAsync(server, laptop, recovery);

        (await server.VerifyAsync(await SessionOfAsync(laptopContext))).Headers.GetValues("X-Ziggurat-User")
            .ShouldBe([fran.UserId]);
        (await server.Service<PasskeyStore>().ListAsync(fran.UserId, CancellationToken.None)).Count.ShouldBe(2);
    }

    // A browser with a platform authenticator that keeps discoverable credentials and verifies its
    // user, as a phone's does. The authenticator belongs to the page, so a ceremony after another
    // runs on the same page.
    private async Task<(IBrowserContext Context, IPage Page)> DeviceAsync()
    {
        var context = await _browser.NewContextAsync();
        var page = await context.NewPageAsync();
        var cdp = await context.NewCDPSessionAsync(page);
        await cdp.SendAsync("WebAuthn.enable");
        await cdp.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>
        {
            ["options"] = new
            {
                protocol = "ctap2",
                transport = "internal",
                hasResidentKey = true,
                hasUserVerification = true,
                isUserVerified = true
            }
        });
        return (context, page);
    }

    // After enrolling, the person lands on the deployment's front page, signed in.
    private static async Task EnrollAsync(AccountsServer server, IPage page, AccountsServer.MintedLink link)
    {
        await page.GotoAsync(link.Url.ToString());
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = CreateButton }).ClickAsync();
        await page.WaitForURLAsync(server.Address.ToString());
    }

    private static async Task SignInAsync(AccountsServer server, IPage page)
    {
        await page.GotoAsync(new Uri(server.Address, "/accounts/sign-in").ToString());
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = SignInButton }).ClickAsync();
        await page.WaitForURLAsync(server.Address.ToString());
    }

    private static async Task<string> SessionOfAsync(IBrowserContext context) =>
        (await context.CookiesAsync()).Single(c => c.Name == SessionCookie.Name).Value;
}