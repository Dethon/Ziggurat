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
// sends /browse/* to, down the household's web search tunnel to its pages listener. The web search
// server is the real one, told only a hub, a credential and its name; its link is minted by a
// web_login the agent calls through the same tunnel, and everything after is driven at the relay.
public class BrowsePagesTests(RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private const string AgentCredential = "agent-credential";
    private const string Fran = "a3f09c1d7e2b4c6a8d5e0f1b2c3d4e5f";

    private const string ShopState =
        """{"cookies":[{"name":"sid","value":"shop-session","domain":".shop.test","path":"/"}],"origins":[]}""";

    private readonly RecordingMetricsPublisher _metrics = new();
    private readonly FakeHandoffBrowser _browser = new();
    private readonly string _keyDirectory = Path.Combine(Path.GetTempPath(), "tunnel-tests", Guid.NewGuid().ToString("N"));
    private AccountsServer _accounts = null!;
    private RelayHost _relay = null!;
    private SigningKeyStore _keys = null!;
    private string _household = null!;

    public async Task InitializeAsync()
    {
        _accounts = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential },
            Redis = new() { ConnectionString = redis.ConnectionString }
        });
        _household = (await _accounts.MintOperatorAsync("Fran")).TenantId;
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
        await using var server = await StartWebSearchAsync();
        var link = await CallAsync("web_login", new() { ["url"] = "https://shop.test/login" });
        var (browser, cookies) = NewBrowserSession();

        (await browser.GetAsync(link)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await browser.PostAsync($"{link}/claim", null)).EnsureSuccessStatusCode();
        using var socket = new ClientWebSocket();
        socket.Options.Cookies = cookies;
        // The page's own origin, which the server compares with the host the browser asked for.
        socket.Options.SetRequestHeader("Origin", _relay.Address);
        await socket.ConnectAsync(new Uri($"{_relay.Address.Replace("http", "ws")}/browse/handoff/live"), CancellationToken.None);
        var frame = await socket.ReceiveAsync(new byte[1024], CancellationToken.None);
        await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type":"text","value":"fran@example.test"}"""),
            WebSocketMessageType.Text, true, CancellationToken.None);

        frame.MessageType.ShouldBe(WebSocketMessageType.Binary);
        await Eventually.Until(() => _browser.Inputs.Count == 1, "the input to reach the tab");
        _browser.Inputs.ShouldBe([new HandoffInput.Text("fran@example.test")]);
        (await browser.PostAsync("/browse/handoff/done", Json("""{"remember":false}"""))).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
    }

    // Behind the proxy the browser spoke https: the claim's cookie says Secure because the proxy's
    // X-Forwarded-Proto reaches the page as the proxy set it.
    [Fact]
    public async Task AClaimBehindTheProxy_BindsASecureCookie()
    {
        await using var server = await StartWebSearchAsync();
        var link = await CallAsync("web_login", new() { ["url"] = "https://shop.test/login" });
        using var proxied = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = new Uri(_relay.Address) };
        using var claim = new HttpRequestMessage(HttpMethod.Post, $"{link}/claim");
        claim.Headers.Add("X-Forwarded-Proto", "https");

        var answer = await proxied.SendAsync(claim);

        answer.EnsureSuccessStatusCode();
        answer.Headers.GetValues("Set-Cookie").ShouldHaveSingleItem().ShouldContain("secure", Case.Insensitive);
    }

    [Fact]
    public async Task TheLoginsPage_ShowsAndForgetsRememberedLoginsThroughTheTunnel()
    {
        await using var server = await StartWebSearchAsync();
        _browser.State = ShopState;
        _browser.Hosts = ["shop.test"];
        var handoff = await CallAsync("web_login", new() { ["url"] = "https://shop.test/login" });
        var (person, _) = NewBrowserSession();
        (await person.PostAsync($"{handoff}/claim", null)).EnsureSuccessStatusCode();
        (await person.PostAsync("/browse/handoff/done", Json("""{"remember":true}"""))).EnsureSuccessStatusCode();
        var logins = await CallAsync("web_logins", []);
        var (page, _) = NewBrowserSession();

        (await page.GetAsync(logins)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await page.PostAsync($"{logins}/claim", null)).EnsureSuccessStatusCode();
        (await SitesAsync(page)).ShouldBe(["shop.test"]);
        (await page.PostAsync("/browse/logins/forget", Json("""{"site":"shop.test"}"""))).EnsureSuccessStatusCode();
        (await SitesAsync(page)).ShouldBeEmpty();
    }

    [Fact]
    public async Task ABrowsePageWithTheWebSearchOffline_AnswersAPageSayingSo()
    {
        var (browser, _) = NewBrowserSession();

        var answer = await browser.GetAsync("/browse/handoff/anything");

        answer.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        answer.Content.Headers.ContentType!.MediaType.ShouldBe("text/html");
        (await answer.Content.ReadAsStringAsync()).ShouldContain("offline");
    }

    private async Task<WebIdentityServerHarness> StartWebSearchAsync()
    {
        var credential = await _accounts.MintTunnelCredentialAsync(_household, HouseholdServerNames.WebSearch);
        var server = await WebIdentityServerHarness.StartAsync(
            configure: services =>
            {
                services.RemoveAll<IHandoffBrowser>();
                services.AddSingleton<IHandoffBrowser>(_browser);
                services.RemoveAll<IPersonContexts>();
                services.AddSingleton<IPersonContexts>(_browser);
                services.RemoveAll<ILoginSites>();
                services.AddSingleton<ILoginSites>(sp => sp.GetRequiredService<PersonLogins>());
            },
            settings: s => s with
            {
                Mcp = new McpGateSettings
                {
                    Tunnel = TunnelledServer.Tunnel(_relay.Hub, credential.Credential, HouseholdServerNames.WebSearch)
                },
                RedisConnectionString = redis.ConnectionString,
                Handoff = s.Handoff with { PublicBaseUrl = _relay.Address },
                Logins = s.Logins with { StateKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }
            });
        await Eventually.Until(
            () => _relay.Registry.Find(_household, HouseholdServerNames.WebSearch) is not null, "web search's tunnel opens");
        return server;
    }

    // A tool call of Fran's as the agent makes it, through the tunnel; the path of the link it answers.
    private async Task<string> CallAsync(string tool, Dictionary<string, object?> arguments)
    {
        await using var agent = await McpClientManager.CreateAsync(
            "jonas", "fran", "test", [_relay.OwnServers.Resolve(_household, HouseholdServerNames.WebSearch)!],
            new McpClientHandlers(), callTokens: new CallTokens(_keys, TimeProvider.System));
        var turn = WebIdentityServerHarness.FromChatClient(new Account(_household, Fran)) with
        {
            Identity = IdentityClaims.Person(Fran, _household, ChannelIds.SignalR, [])
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

    private static async Task<IReadOnlyList<string>> SitesAsync(HttpClient page) =>
        JsonNode.Parse(await page.GetStringAsync("/browse/logins/sites"))!.AsArray()
            .Select(site => site!["site"]!.GetValue<string>()).ToList();

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
}