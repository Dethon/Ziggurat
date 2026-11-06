using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Tunnel;
using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.Agents.Mcp;
using Infrastructure.Clients.Browser;
using Infrastructure.StateManagers;
using McpServerWebSearch.Handoff;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Integration.McpServerTests;
using Tests.Unit;

namespace Tests.Integration.Tunnels;

// The browse pages as a person's browser gets them from a link: through the relay, which the proxy
// sends /browse/* to, down the web search tunnel of the household the link names to its pages
// listener. The web search server is the real one, told only a hub, a credential and its name; its
// link is minted by a web_login the agent calls through the same tunnel, and everything after is
// driven at the relay. The servers of two households share the test's Redis, so a link one minted is
// live in the other's one-time links too: what tells them apart is the handoff each holds in memory,
// and the browser each records input on.
public class BrowsePagesTests(RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private const string AgentCredential = "agent-credential";
    private const string Fran = "a3f09c1d7e2b4c6a8d5e0f1b2c3d4e5f";

    private const string ShopState =
        """{"cookies":[{"name":"sid","value":"shop-session","domain":".shop.test","path":"/"}],"origins":[]}""";

    private readonly RecordingMetricsPublisher _metrics = new();
    private readonly FakeHandoffBrowser _browser = new();
    private readonly FakeHandoffBrowser _casaBrowser = new();
    private readonly string _keyDirectory = Path.Combine(Path.GetTempPath(), "tunnel-tests", Guid.NewGuid().ToString("N"));
    private AccountsServer _accounts = null!;
    private RelayHost _relay = null!;
    private SigningKeyStore _keys = null!;
    private string _household = null!;
    private Account _lucia = null!;

    public async Task InitializeAsync()
    {
        _accounts = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential },
            Redis = new() { ConnectionString = redis.ConnectionString }
        });
        _household = (await _accounts.MintOperatorAsync("Fran")).TenantId;
        var (casa, lucia) = await _accounts.SeedHouseholdAsync("Casa", "Lucía");
        _lucia = new Account(casa, lucia);
        await redis.Connection.GetDatabase().KeyDeleteAsync($"websearch:person:{Fran}");
        _keys = SigningKeyStore.Open(new SigningKeySettings { KeyDirectory = _keyDirectory });
        _relay = await RelayHost.StartAsync(_accounts, AgentCredential, redis.ConnectionString, _keys, _metrics);
    }

    public async Task DisposeAsync()
    {
        await _relay.DisposeAsync();
        await _accounts.DisposeAsync();
        Directory.Delete(_keyDirectory, recursive: true);
    }

    [Fact]
    public async Task AHandoffLinkItsClaimAndItsLiveView_WorkThroughTheTunnel()
    {
        await using var server = await StartWebSearchAsync(_household, _browser);
        var link = await CallAsync(Home, "web_login", new() { ["url"] = "https://shop.test/login" });
        var (browser, cookies) = NewBrowserSession();

        (await browser.GetAsync(link)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await browser.PostAsync($"{link}/claim", null)).EnsureSuccessStatusCode();
        using var view = await TypeIntoTheLiveViewAsync(_household, cookies, "fran@example.test");

        await Eventually.Until(() => _browser.Inputs.Count == 1, "the input to reach the tab");
        _browser.Inputs.ShouldBe([new HandoffInput.Text("fran@example.test")]);
        (await browser.PostAsync($"{Pages(_household)}/handoff/done", Json("""{"remember":false}"""))).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
    }

    // Behind the proxy the browser spoke https: the claim's cookie says Secure because the proxy's
    // X-Forwarded-Proto reaches the page as the proxy set it. It is sent back to its own household's
    // pages alone, so no other household's web search is ever handed this view's key.
    [Fact]
    public async Task AClaimBehindTheProxy_BindsASecureCookie_ToItsHouseholdsPages()
    {
        await using var server = await StartWebSearchAsync(_household, _browser);
        var link = await CallAsync(Home, "web_login", new() { ["url"] = "https://shop.test/login" });
        using var proxied = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(_relay.Address) };
        using var claim = new HttpRequestMessage(HttpMethod.Post, $"{link}/claim");
        claim.Headers.Add("X-Forwarded-Proto", "https");

        var answer = await proxied.SendAsync(claim);

        answer.EnsureSuccessStatusCode();
        var cookie = answer.Headers.GetValues("Set-Cookie").ShouldHaveSingleItem();
        cookie.ShouldContain("secure", Case.Insensitive);
        cookie.ShouldContain($"path={Pages(_household)};", Case.Insensitive);
    }

    [Fact]
    public async Task TheLoginsPage_ShowsAndForgetsRememberedLoginsThroughTheTunnel()
    {
        await using var server = await StartWebSearchAsync(_household, _browser);
        _browser.State = ShopState;
        _browser.Hosts = ["shop.test"];
        var handoff = await CallAsync(Home, "web_login", new() { ["url"] = "https://shop.test/login" });
        var (person, _) = NewBrowserSession();
        (await person.PostAsync($"{handoff}/claim", null)).EnsureSuccessStatusCode();
        (await person.PostAsync($"{Pages(_household)}/handoff/done", Json("""{"remember":true}"""))).EnsureSuccessStatusCode();
        var logins = await CallAsync(Home, "web_logins", new());
        var (page, _) = NewBrowserSession();

        (await page.GetAsync(logins)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await page.PostAsync($"{logins}/claim", null)).EnsureSuccessStatusCode();
        (await SitesAsync(page, _household)).ShouldBe(["shop.test"]);
        (await page.PostAsync($"{Pages(_household)}/logins/forget", Json("""{"site":"shop.test"}"""))).EnsureSuccessStatusCode();
        (await SitesAsync(page, _household)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ABrowsePageWithTheWebSearchOffline_AnswersAPageSayingSo()
    {
        var (browser, _) = NewBrowserSession();

        var answer = await browser.GetAsync($"{Pages(_household)}/handoff/anything");

        answer.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        answer.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        (await answer.Content.ReadAsStringAsync()).ShouldContain("offline");
    }

    // Both households run a web search; Lucía's link names Casa, and its claim and live view are
    // answered by Casa's server — the home's holds no such handoff, and its tab never sees the input.
    [Fact]
    public async Task AnotherHouseholdsLink_OpensAPageServedByItsOwnWebSearch()
    {
        await using var home = await StartWebSearchAsync(_household, _browser);
        await using var casa = await StartWebSearchAsync(_lucia.TenantId, _casaBrowser);
        var link = await CallAsync(_lucia, "web_login", new() { ["url"] = "https://shop.test/login" });
        var (browser, cookies) = NewBrowserSession();

        link.ShouldStartWith($"{Pages(_lucia.TenantId)}/handoff/");
        (await browser.GetAsync(link)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await browser.PostAsync($"{link}/claim", null)).EnsureSuccessStatusCode();
        using var view = await TypeIntoTheLiveViewAsync(_lucia.TenantId, cookies, "lucia@example.test");

        await Eventually.Until(() => _casaBrowser.Inputs.Count == 1, "the input to reach Casa's tab");
        _casaBrowser.Inputs.ShouldBe([new HandoffInput.Text("lucia@example.test")]);
        _browser.Inputs.ShouldBeEmpty();
        (await browser.PostAsync($"{Pages(_lucia.TenantId)}/handoff/done", Json("""{"remember":false}"""))).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
    }

    // Casa's web search goes away after minting the link; the home's stays. The link is answered by
    // the relay's offline page — never by the home's web search, which would show the link's page,
    // since the two share a Redis here.
    [Fact]
    public async Task AnotherHouseholdsLink_WithItsWebSearchNotConnected_ShowsTheOfflinePage_NeverTheHomes()
    {
        await using var home = await StartWebSearchAsync(_household, _browser);
        string link;
        await using (await StartWebSearchAsync(_lucia.TenantId, _casaBrowser))
        {
            link = await CallAsync(_lucia, "web_login", new() { ["url"] = "https://shop.test/login" });
        }

        await Eventually.Until(
            () => _relay.Registry.Find(_lucia.TenantId, HouseholdServerNames.WebSearch) is null, "Casa's web search to go");
        var (browser, _) = NewBrowserSession();

        var answer = await browser.GetAsync(link);

        answer.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        answer.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        (await answer.Content.ReadAsStringAsync()).ShouldContain("Your household's browser is offline");
    }

    // A link minted before links named a household — a kit web search not yet rebuilt still mints
    // them — is the home's, as every link then was: with the home's web search connected the answer
    // is that server's (which, rebuilt, has no page there), not the relay's offline page.
    [Fact]
    public async Task ALinkNamingNoHousehold_IsForwardedToTheHomesWebSearch()
    {
        await using var server = await StartWebSearchAsync(_household, _browser);
        var (browser, _) = NewBrowserSession();

        var answer = await browser.GetAsync("/browse/handoff/anything");

        answer.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // A household's web search runs on its own machine, and its owner may swap it: a page it serves under
    // /browse is same-origin with the chat client and the admin API, so its own script would act as
    // whoever opened the link, and a cookie of its own on Path=/ would sign them in as somebody else.
    // The relay sends its answer under the deployment's policy instead — the pinned scripts alone — and
    // keeps only the page's view cookies on that household's pages.
    [Fact]
    public async Task AHouseholdsOwnWebSearch_RunsNoScriptOfItsOwn_AndSetsNoCookieBeyondItsPages()
    {
        var pages = Pages(_lucia.TenantId);
        await using var hostile = await StartHostileWebSearchAsync(_lucia.TenantId);
        using var raw = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(_relay.Address) };

        var answer = await raw.GetAsync($"{pages}/evil");

        answer.StatusCode.ShouldBe(HttpStatusCode.OK);
        answer.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem().ShouldBe(BrowsePagePolicy.ContentSecurityPolicy);
        answer.Headers.Contains("Clear-Site-Data").ShouldBeFalse();
        answer.Headers.GetValues("X-Content-Type-Options").ShouldHaveSingleItem().ShouldBe("nosniff");
        answer.Headers.GetValues("Set-Cookie").ShouldHaveSingleItem().ShouldStartWith($"{BrowsePagePolicy.ViewCookie}=kept;");
        var script = (await answer.Content.ReadAsStringAsync()).Split("<script>")[1].Split("</script>")[0];
        BrowsePagePolicy.ContentSecurityPolicy.ShouldNotContain(BrowsePagePolicy.HashOf(script));
    }

    // The deployment's own pages still run under that policy: every script the handoff and logins pages
    // carry is one the policy pins, and their link's values travel as data beside them.
    [Fact]
    public async Task TheHandoffAndLoginsPages_RunOnlyThePinnedScripts()
    {
        await using var server = await StartWebSearchAsync(_household, _browser);
        var (browser, _) = NewBrowserSession();

        foreach (var link in new[]
                 {
                     await CallAsync(Home, "web_login", new() { ["url"] = "https://shop.test/login" }),
                     await CallAsync(Home, "web_logins", new())
                 })
        {
            var answer = await browser.GetAsync(link);
            answer.StatusCode.ShouldBe(HttpStatusCode.OK);
            answer.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem().ShouldBe(BrowsePagePolicy.ContentSecurityPolicy);
            var html = await answer.Content.ReadAsStringAsync();
            var scripts = System.Text.RegularExpressions.Regex.Matches(html, "<script>(.*?)</script>",
                System.Text.RegularExpressions.RegexOptions.Singleline).Select(m => m.Groups[1].Value).ToList();
            scripts.ShouldHaveSingleItem();
            BrowsePagePolicy.ScriptHashes.ShouldContain(BrowsePagePolicy.HashOf(scripts[0]), link);
            html.ShouldContain($"<script type=\"application/json\" id=\"{BrowsePagePolicy.DataElementId}\">");
            html.ShouldContain(link.Split('/').Last());
        }
    }

    // In a real browser on the relay's origin: the handoff page's own script claims its link and names
    // the site, while a household server's script of its own is refused by the policy.
    [Fact]
    public async Task InABrowser_TheHandoffPagesScriptRuns_AndAHouseholdServersOwnDoesNot()
    {
        await using var server = await StartWebSearchAsync(_household, _browser);
        await using var hostile = await StartHostileWebSearchAsync(_lucia.TenantId);
        var link = await CallAsync(Home, "web_login", new() { ["url"] = "https://shop.test/login" });
        using var playwright = await Microsoft.Playwright.Playwright.CreateAsync();
        await using var chromium = await playwright.Chromium.LaunchAsync(new Microsoft.Playwright.BrowserTypeLaunchOptions
        {
            Headless = Environment.GetEnvironmentVariable("PLAYWRIGHT_HEADLESS") != "false"
        });
        var page = await chromium.NewPageAsync();
        var refused = new System.Collections.Concurrent.ConcurrentQueue<string>();
        page.Console += (_, message) => refused.Enqueue(message.Text);

        await page.GotoAsync(_relay.Address + link);
        await Microsoft.Playwright.Assertions.Expect(page.Locator("#title")).ToHaveTextAsync("Log in to shop.test");
        refused.ShouldNotContain(m => m.Contains("Content Security Policy"));

        await page.GotoAsync(_relay.Address + Pages(_lucia.TenantId) + "/evil");
        await Eventually.Until(
            () => refused.Any(m => m.Contains("Content Security Policy") && m.Contains("script")),
            "the browser to refuse the household server's own script");
        (await page.TitleAsync()).ShouldBe("kit");
    }

    // A web search whose owner replaced it: its pages answer with a script and cookies of its own, and
    // with headers that would loosen the page's policy or wipe the origin's cookies.
    private async Task<TunnelledServer> StartHostileWebSearchAsync(string household)
    {
        var credential = await _accounts.MintTunnelCredentialAsync(household, HouseholdServerNames.WebSearch);
        var pages = Pages(household);
        var hostile = await TunnelledServer.StartAsync(
            _relay.Hub, credential.Credential, HouseholdServerNames.WebSearch,
            listeners: new Dictionary<string, int> { [TunnelProtocol.PagesTag] = TestPort.GetAvailable() },
            routes: app => app.MapGet($"{pages}/evil", (HttpContext http) =>
            {
                http.Response.Headers.Append("Set-Cookie", "ziggurat_session=the-owners-own; Path=/; HttpOnly");
                http.Response.Headers.Append("Set-Cookie", $"{BrowsePagePolicy.ViewCookie}=kept; Path={pages}; HttpOnly");
                http.Response.Headers.Append("Set-Cookie", $"{BrowsePagePolicy.LoginsCookie}=wider; Path=/browse; HttpOnly");
                http.Response.Headers.Append("Content-Security-Policy", "script-src * 'unsafe-inline'");
                http.Response.Headers.Append("Clear-Site-Data", "\"cookies\"");
                return Results.Content(
                    "<title>kit</title><script>document.title = 'ran'; fetch('/accounts/admin/household/members', {headers: {'X-Requested-With': 'ziggurat-admin'}})</script>",
                    "text/html");
            }));
        await Eventually.Until(
            () => _relay.Registry.Find(household, HouseholdServerNames.WebSearch) is not null, "the web search's tunnel opens");
        return hostile;
    }

    private Account Home => new(_household, Fran);

    private static string Pages(string household) => BrowsePagePath.For(household);

    // Open until the caller is done with it: a socket closed straight after the send can take the
    // input with it before the server reads it.
    private async Task<ClientWebSocket> TypeIntoTheLiveViewAsync(string household, CookieContainer cookies, string text)
    {
        var socket = new ClientWebSocket();
        socket.Options.Cookies = cookies;
        // The page's own origin, which the server compares with the host the browser asked for.
        socket.Options.SetRequestHeader("Origin", _relay.Address);
        await socket.ConnectAsync(
            new Uri($"{_relay.Address.Replace("http", "ws")}{Pages(household)}/handoff/live"), CancellationToken.None);
        var frame = await socket.ReceiveAsync(new byte[1024], CancellationToken.None);
        frame.MessageType.ShouldBe(WebSocketMessageType.Binary);
        await socket.SendAsync(Encoding.UTF8.GetBytes($$"""{"type":"text","value":"{{text}}"}"""),
            WebSocketMessageType.Text, true, CancellationToken.None);
        return socket;
    }

    private async Task<WebIdentityServerHarness> StartWebSearchAsync(string household, FakeHandoffBrowser browser)
    {
        var credential = await _accounts.MintTunnelCredentialAsync(household, HouseholdServerNames.WebSearch);
        var server = await WebIdentityServerHarness.StartAsync(
            configure: services =>
            {
                services.RemoveAll<IHandoffBrowser>();
                services.AddSingleton<IHandoffBrowser>(browser);
                services.RemoveAll<IPersonContexts>();
                services.AddSingleton<IPersonContexts>(browser);
                services.RemoveAll<ILoginSites>();
                services.AddSingleton<ILoginSites>(sp => sp.GetRequiredService<PersonLogins>());
            },
            settings: s => s with
            {
                Mcp = new McpGateSettings
                {
                    Tunnel = TunnelledServer.Quick(_relay.Hub, credential.Credential, HouseholdServerNames.WebSearch)
                },
                RedisConnectionString = redis.ConnectionString,
                Handoff = s.Handoff with { PublicBaseUrl = _relay.Address },
                Logins = s.Logins with { StateKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }
            });
        await Eventually.Until(
            () => _relay.Registry.Find(household, HouseholdServerNames.WebSearch) is not null, "web search's tunnel opens");
        return server;
    }

    // A tool call of the person's as the agent makes it, through their household's tunnel; the path
    // of the link it answers.
    private async Task<string> CallAsync(Account person, string tool, Dictionary<string, object?> arguments)
    {
        await using var agent = await McpClientManager.CreateAsync(
            "jonas", person.UserId, "test", [_relay.OwnServers.Resolve(person.TenantId, HouseholdServerNames.WebSearch)!],
            new McpClientHandlers(), callTokens: new CallTokens(_keys, TimeProvider.System));
        var turn = WebIdentityServerHarness.FromChatClient(person) with
        {
            Identity = IdentityClaims.Person(person.UserId, person.TenantId, ChannelIds.SignalR, [])
        };
        var result = await agent.Clients.Single().CallToolAsync(new CallToolRequestParams
        {
            Name = tool,
            Arguments = arguments.ToDictionary(a => a.Key, a => System.Text.Json.JsonSerializer.SerializeToElement(a.Value)),
            Meta = WebIdentityServerHarness.MetaFor(turn)
        });
        return new Uri(WebIdentityServerHarness.Envelope(result)["link"]!.GetValue<string>()).AbsolutePath;
    }

    private (HttpClient Client, CookieContainer Cookies) NewBrowserSession()
    {
        var cookies = new CookieContainer();
        return (new HttpClient(new HttpClientHandler { CookieContainer = cookies }) { BaseAddress = new Uri(_relay.Address) }, cookies);
    }

    private static async Task<IReadOnlyList<string>> SitesAsync(HttpClient page, string household) =>
        JsonNode.Parse(await page.GetStringAsync($"{Pages(household)}/logins/sites"))!.AsArray()
            .Select(site => site!["site"]!.GetValue<string>()).ToList();

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}