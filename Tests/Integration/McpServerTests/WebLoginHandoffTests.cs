using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Tools;
using McpServerWebSearch.Handoff;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Tests.Integration.Fixtures;
using static Tests.Integration.McpServerTests.WebIdentityServerHarness;

namespace Tests.Integration.McpServerTests;

// The handoff from both ends, with a browser that only records: the tool's refusals and the link it
// answers with, then the link's own rules — it shows a page to anyone, is spent by one claim, runs
// out, and binds everything after the claim to the browser that claimed it.
public class WebLoginHandoffTests(RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private const string PublicBase = "https://assistants.test";

    private readonly FakeHandoffBrowser _browser = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private WebIdentityServerHarness _server = null!;

    public async Task InitializeAsync() => _server = await StartAsync(
        configure: services =>
        {
            services.RemoveAll<IHandoffBrowser>();
            services.AddSingleton<IHandoffBrowser>(_browser);
            services.RemoveAll<IPersonContexts>();
            services.AddSingleton<IPersonContexts>(_browser);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_time);
        },
        settings: s => s with
        {
            RedisConnectionString = redis.ConnectionString,
            Handoff = s.Handoff with { PublicBaseUrl = PublicBase },
            Logins = s.Logins with { StateKey = StateKey }
        });

    private static readonly string StateKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private const string ShopState =
        """{"cookies":[{"name":"sid","value":"shop-session","domain":".shop.test","path":"/"}],"origins":[]}""";

    private IPersonStateStore Store => _server.Services.GetRequiredService<IPersonStateStore>();

    private async Task FinishHandoffAsync(string doneBody)
    {
        var path = PathOf(await StartHandoffAsync());
        var (client, _) = NewBrowserSession();
        (await client.PostAsync($"{path}/claim", null)).EnsureSuccessStatusCode();
        (await client.PostAsync("/browse/handoff/done", Json(doneBody))).EnsureSuccessStatusCode();
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private static Dictionary<string, object?> Login(string url = "https://shop.test/login") => new() { ["url"] = url };

    private async Task<string> StartHandoffAsync()
    {
        var envelope = Envelope(await _server.CallAsync("web_login", Login(), Context("fran_tg")));
        envelope["status"]!.GetValue<string>().ShouldBe("handoff_started");
        return envelope["link"]!.GetValue<string>();
    }

    private (HttpClient Client, CookieContainer Cookies) NewBrowserSession()
    {
        var cookies = new CookieContainer();
        var client = new HttpClient(new HttpClientHandler { CookieContainer = cookies })
        {
            BaseAddress = new Uri(_server.BaseAddress)
        };
        return (client, cookies);
    }

    private static string PathOf(string link) => new Uri(link).AbsolutePath;

    [Fact]
    public async Task ALogin_OpensAHandoffTabInThePersonsContext_AndAnswersALinkUnderTheBrowsePath()
    {
        var link = await StartHandoffAsync();

        link.ShouldStartWith($"{PublicBase}/browse/handoff/");
        var opened = _browser.OpenedTabs.ShouldHaveSingleItem();
        opened.Person.ShouldBe("fran");
        opened.Url.ShouldBe("https://shop.test/login");
        // The handoff never reaches the browse session's pool.
        _server.Browser.Browses.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(MessageOriginKind.Schedule)]
    [InlineData(MessageOriginKind.Watch)]
    [InlineData(MessageOriginKind.Login)]
    public async Task ALoginOnAFiresTurn_IsRefused_AndOpensNothing(MessageOriginKind kind)
    {
        var envelope = Envelope(await _server.CallAsync("web_login", Login(), Context("fran_tg", originKind: kind)));

        envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PermissionDenied);
        envelope["message"]!.GetValue<string>().ShouldContain("not by the person");
        _browser.OpenedTabs.ShouldBeEmpty();
    }

    [Fact]
    public async Task ALoginForAnUnmappedUser_IsRefused_AndOpensNothing()
    {
        var envelope = Envelope(await _server.CallAsync("web_login", Login(), Context("stranger")));

        envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.PermissionDenied);
        _browser.OpenedTabs.ShouldBeEmpty();
    }

    [Fact]
    public async Task TheLinksPage_IsShownToAnyGet_AndOnlyTheFirstClaimOpensIt()
    {
        var path = PathOf(await StartHandoffAsync());
        var (first, firstCookies) = NewBrowserSession();
        var (second, _) = NewBrowserSession();

        // A previewer's GET spends nothing.
        (await second.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await first.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var claim = await first.PostAsync($"{path}/claim", null);
        claim.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await claim.Content.ReadAsStringAsync()).ShouldContain("shop.test");
        firstCookies.GetAllCookies().Single(c => c.Name == HandoffEndpoints.ViewCookie).HttpOnly.ShouldBeTrue();

        (await second.PostAsync($"{path}/claim", null)).StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await second.GetAsync(path)).StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task ALinkNotClaimedInTenMinutes_IsRefused_AndItsTabIsGone()
    {
        var path = PathOf(await StartHandoffAsync());
        var tab = _browser.OpenedTabs.Single().Id;

        _time.Advance(TimeSpan.FromMinutes(10));
        await Eventually.Until(() => !_browser.IsHandoffOpen(tab), "the expired handoff's tab to close");

        var (client, _) = NewBrowserSession();
        (await client.PostAsync($"{path}/claim", null)).StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task AClaimedViewLeftOpen_RunsOutTooAndClosesItsTab()
    {
        var path = PathOf(await StartHandoffAsync());
        var tab = _browser.OpenedTabs.Single().Id;
        var (client, _) = NewBrowserSession();
        (await client.PostAsync($"{path}/claim", null)).EnsureSuccessStatusCode();

        _time.Advance(TimeSpan.FromMinutes(29));
        _browser.IsHandoffOpen(tab).ShouldBeTrue();
        _time.Advance(TimeSpan.FromMinutes(1));

        await Eventually.Until(() => !_browser.IsHandoffOpen(tab), "the abandoned view's tab to close");
        (await client.PostAsync("/browse/handoff/done", Json("{}"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // A ticket outliving its tab — the browser restarted under it — names a view that would be
    // blank, so the claim refuses.
    [Fact]
    public async Task AClaimForATabTheBrowserLost_IsRefused()
    {
        var path = PathOf(await StartHandoffAsync());
        await _browser.CloseHandoffAsync(_browser.OpenedTabs.Single().Id);

        var (client, _) = NewBrowserSession();
        (await client.PostAsync($"{path}/claim", null)).StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Done_AnswersOnlyTheClaimingBrowser_AndClosesTheTab()
    {
        var path = PathOf(await StartHandoffAsync());
        var tab = _browser.OpenedTabs.Single().Id;
        var (claimer, _) = NewBrowserSession();
        var (other, _) = NewBrowserSession();
        (await claimer.PostAsync($"{path}/claim", null)).EnsureSuccessStatusCode();

        (await other.PostAsync("/browse/handoff/done", Json("{}"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _browser.IsHandoffOpen(tab).ShouldBeTrue();

        (await claimer.PostAsync("/browse/handoff/done", Json("""{"remember":true}"""))).StatusCode
            .ShouldBe(HttpStatusCode.NoContent);
        _browser.IsHandoffOpen(tab).ShouldBeFalse();
        (await claimer.PostAsync("/browse/handoff/done", Json("{}"))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheLiveView_SendsFramesAndForwardsInput_OnlyToTheClaimingBrowser()
    {
        var path = PathOf(await StartHandoffAsync());
        var (claimer, cookies) = NewBrowserSession();
        (await claimer.PostAsync($"{path}/claim", null)).EnsureSuccessStatusCode();
        var live = new Uri($"{_server.BaseAddress.Replace("http", "ws")}/browse/handoff/live");

        using var stranger = new ClientWebSocket();
        await Should.ThrowAsync<WebSocketException>(() => stranger.ConnectAsync(live, CancellationToken.None));

        using var socket = new ClientWebSocket();
        socket.Options.Cookies = cookies;
        await socket.ConnectAsync(live, CancellationToken.None);
        var buffer = new byte[1024];
        var frame = await socket.ReceiveAsync(buffer, CancellationToken.None);
        frame.MessageType.ShouldBe(WebSocketMessageType.Binary);

        await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type":"text","value":"fran@example.test"}"""),
            WebSocketMessageType.Text, true, CancellationToken.None);
        await socket.SendAsync(Encoding.UTF8.GetBytes("""{"type":"mousedown","x":0.5,"y":0.25}"""),
            WebSocketMessageType.Text, true, CancellationToken.None);

        await Eventually.Until(() => _browser.Inputs.Count == 2, "both inputs to reach the tab");
        _browser.Inputs.ShouldBe([new HandoffInput.Text("fran@example.test"), new HandoffInput.MouseDown(0.5, 0.25)]);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<IReadOnlyList<ChannelMessageNotification>> DrainAsync() =>
        (await _server.Services.GetRequiredService<ChannelInbox>()
            .ReceiveAsync(ChannelProtocol.ChannelClientNamePrefix + "websearch", TimeSpan.Zero, CancellationToken.None))
        .Where(i => i.Kind == ChannelInboxItemKind.Message).Select(i => i.Message!).ToList();

    // Done wakes the agent where the login was asked for: the same conversation, agent and person,
    // answering to the same reply target, raised by a login.
    [Fact]
    public async Task Done_EmitsOneWake_IntoTheConversationThatAsked()
    {
        var envelope = Envelope(await _server.CallAsync("web_login", Login(),
            Context("fran_tg", conversationId: "tg-77", channelId: "telegram")));
        var path = PathOf(envelope["link"]!.GetValue<string>());
        var (client, _) = NewBrowserSession();
        (await client.PostAsync($"{path}/claim", null)).EnsureSuccessStatusCode();

        (await client.PostAsync("/browse/handoff/done", Json("{}"))).EnsureSuccessStatusCode();

        var wake = (await DrainAsync()).ShouldHaveSingleItem();
        wake.ConversationId.ShouldBe("tg-77");
        wake.AgentId.ShouldBe("jonas");
        wake.UserId.ShouldBe("fran_tg");
        wake.ReplyTo.ShouldBe([new ReplyTarget("telegram", "tg-77")]);
        wake.Origin!.Kind.ShouldBe(MessageOriginKind.Login);
        wake.Content.ShouldContain("shop.test");
        wake.Content.ShouldContain("logged in");
    }

    [Fact]
    public async Task AnAbandonedOrExpiredHandoff_EmitsNothing()
    {
        var abandoned = PathOf(await StartHandoffAsync());
        var (client, _) = NewBrowserSession();
        (await client.PostAsync($"{abandoned}/claim", null)).EnsureSuccessStatusCode();
        await StartHandoffAsync();

        _time.Advance(TimeSpan.FromMinutes(30));
        await Eventually.Until(() => _browser.OpenedTabs.All(t => !_browser.IsHandoffOpen(t.Id)),
            "both handoffs to run out");

        await Eventually.Settle();
        (await DrainAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task ATickedDone_RemembersTheSitesTheHandoffVisited_WithTheContextsState()
    {
        _browser.State = ShopState;
        _browser.Hosts = ["accounts.shop.test", "shop.test"];

        await FinishHandoffAsync("""{"remember":true}""");

        var record = (await Store.LoadAsync("fran")).ShouldNotBeNull();
        record.Sites.Select(site => site.Domain).ShouldBe(["shop.test"]);
        record.StorageState.ShouldContain("shop-session");
    }

    [Fact]
    public async Task AnUntickedDone_LeavesTheSiteAndItsCookiesOutOfTheRecord()
    {
        _browser.State = ShopState;
        _browser.Hosts = ["shop.test"];

        await FinishHandoffAsync("""{"remember":false}""");

        var record = await Store.LoadAsync("fran");
        (record?.Sites ?? []).ShouldBeEmpty();
        (record?.StorageState ?? "").ShouldNotContain("shop-session");
    }

    [Fact]
    public async Task UseOfARememberedSite_IsSavedOnceTheDebounceRunsOut()
    {
        _browser.State = ShopState;
        _browser.Hosts = ["shop.test"];
        await FinishHandoffAsync("""{"remember":true}""");
        _browser.State = ShopState.Replace("shop-session", "rotated-session");

        _browser.RaiseActivity("fran", "https://shop.test/orders");
        _time.Advance(TimeSpan.FromSeconds(5));

        await Eventually.Until(() => Store.LoadAsync("fran").GetAwaiter().GetResult()!.StorageState.Contains("rotated-session"),
            "the rotated cookie to be saved");
        (await Store.LoadAsync("fran"))!.Sites.Single().LastUsedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task TheClaim_SaysWhetherThisServerCanRemember()
    {
        var path = PathOf(await StartHandoffAsync());
        var (client, _) = NewBrowserSession();

        var claim = JsonNode.Parse(await (await client.PostAsync($"{path}/claim", null)).Content.ReadAsStringAsync())!;

        claim["canRemember"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Fact]
    public async Task ALoginForAPersonWhoseSavedLoginsWillNotOpen_IsRefusedNamingWhy()
    {
        _browser.OpenFails = new PersonStateUnreadableException("fran", "the configured key does not open them");

        var envelope = Envelope(await _server.CallAsync("web_login", Login(), Context("fran_tg")));

        envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.UnsupportedOperation);
        envelope["message"]!.GetValue<string>().ShouldContain("cannot be read");
    }

    // No tool the model can call touches the store: nothing reads a person's saved logins back and
    // nothing forgets one.
    [Fact]
    public async Task TheServersTools_AreTheWebToolsTheLoginAndTheChannelProtocolAlone()
    {
        var tools = await _server.ToolNamesAsync();

        tools.Order().ShouldBe(new[]
        {
            "channel_receive", "request_approval", "send_reply",
            "view_image", "web_action", "web_browse", "web_login", "web_search", "web_snapshot"
        }.Order());
    }
}