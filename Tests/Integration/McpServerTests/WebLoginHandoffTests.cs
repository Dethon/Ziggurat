using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Tools;
using Infrastructure.Clients.Browser;
using Infrastructure.StateManagers;
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

    // The class shares one database and the people are fixed names, so each test starts with
    // nobody's logins remembered.
    public async Task InitializeAsync()
    {
        await redis.Connection.GetDatabase().KeyDeleteAsync(["websearch:person:fran", "websearch:person:ana"]);
        _server = await StartServerAsync();
    }

    private Task<WebIdentityServerHarness> StartServerAsync() => StartAsync(
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

    private const string BankState =
        """{"cookies":[{"name":"sid","value":"bank-session","domain":".bank.test","path":"/"}],"origins":[]}""";

    private IPersonStateStore Store => _server.Services.GetRequiredService<IPersonStateStore>();

    private async Task FinishHandoffAsync(string doneBody, string userId = "fran_tg", string url = "https://shop.test/login")
    {
        var envelope = Envelope(await _server.CallAsync("web_login", Login(url), Context(userId)));
        var path = PathOf(envelope["link"]!.GetValue<string>());
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
    public async Task ATickedDone_RemembersTheLoginOfTheSiteHandedOff_WithTheContextsState()
    {
        _browser.State = ShopState;
        _browser.Hosts = ["accounts.shop.test", "shop.test"];

        await FinishHandoffAsync("""{"remember":true}""");

        var record = (await Store.LoadAsync("fran")).ShouldNotBeNull();
        var login = record.Logins.ShouldHaveSingleItem();
        login.Site.ShouldBe("shop.test");
        login.Domains.ShouldBe(["shop.test"]);
        record.StorageState.ShouldContain("shop-session");
    }

    [Fact]
    public async Task AnUntickedDone_LeavesTheSiteAndItsCookiesOutOfTheRecord()
    {
        _browser.State = ShopState;
        _browser.Hosts = ["shop.test"];

        await FinishHandoffAsync("""{"remember":false}""");

        var record = await Store.LoadAsync("fran");
        (record?.Logins ?? []).ShouldBeEmpty();
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
        (await Store.LoadAsync("fran"))!.Logins.Single().LastUsedAt.ShouldNotBeNull();
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
            "view_image", "web_action", "web_browse", "web_login", "web_logins", "web_search", "web_snapshot"
        }.Order());
    }

    private async Task<HttpClient> OpenLoginsPageAsync(string userId)
    {
        var envelope = Envelope(await _server.CallAsync("web_logins", [], Context(userId)));
        var link = envelope["link"]!.GetValue<string>();
        link.ShouldStartWith($"{PublicBase}/browse/logins/");
        var (client, _) = NewBrowserSession();
        (await client.GetAsync(PathOf(link))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.PostAsync($"{PathOf(link)}/claim", null)).EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<IReadOnlyList<string>> SitesAsync(HttpClient page) =>
        JsonNode.Parse(await page.GetStringAsync("/browse/logins/sites"))!.AsArray()
            .Select(site => site!["site"]!.GetValue<string>()).ToList();

    [Fact]
    public async Task TheLoginsPage_ListsTheAskingPersonsSitesAndNobodyElses()
    {
        _browser.State = ShopState;
        _browser.Hosts = ["shop.test"];
        await FinishHandoffAsync("""{"remember":true}""");
        _browser.Hosts = ["bank.test"];
        await FinishHandoffAsync("""{"remember":false}""", userId: "ana_tg", url: "https://bank.test/login");

        var fran = await OpenLoginsPageAsync("fran_tg");
        (await SitesAsync(fran)).ShouldBe(["shop.test"]);
        JsonNode.Parse(await fran.GetStringAsync("/browse/logins/sites"))![0]!["domains"]!.AsArray()
            .Select(domain => domain!.GetValue<string>()).ShouldBe(["shop.test"]);
        (await SitesAsync(await OpenLoginsPageAsync("ana_tg"))).ShouldBe(["bank.test"]);
    }

    [Fact]
    public async Task Forget_DropsTheSiteFromTheLiveContextAndTheRecord()
    {
        _browser.State = ShopState;
        _browser.Hosts = ["shop.test"];
        await FinishHandoffAsync("""{"remember":true}""");
        var page = await OpenLoginsPageAsync("fran_tg");

        (await page.PostAsync("/browse/logins/forget", Json("""{"site":"shop.test"}"""))).EnsureSuccessStatusCode();

        _browser.State.ShouldNotBeNull().ShouldNotContain("shop-session");
        var record = (await Store.LoadAsync("fran")).ShouldNotBeNull();
        record.Logins.ShouldBeEmpty();
        record.StorageState.ShouldNotContain("shop-session");
        (await SitesAsync(page)).ShouldBeEmpty();
    }

    [Fact]
    public async Task TheLoginsLink_OpensOnce_AndRunsOutInTenMinutes()
    {
        var once = PathOf(Envelope(await _server.CallAsync("web_logins", [], Context("fran_tg")))["link"]!.GetValue<string>());
        var late = PathOf(Envelope(await _server.CallAsync("web_logins", [], Context("fran_tg")))["link"]!.GetValue<string>());
        var (first, _) = NewBrowserSession();
        var (second, _) = NewBrowserSession();

        (await first.PostAsync($"{once}/claim", null)).EnsureSuccessStatusCode();
        (await second.PostAsync($"{once}/claim", null)).StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await second.GetAsync("/browse/logins/sites")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        _time.Advance(TimeSpan.FromMinutes(10));
        (await second.PostAsync($"{late}/claim", null)).StatusCode.ShouldBe(HttpStatusCode.Gone);
        (await first.GetAsync("/browse/logins/sites")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TheLoginsLink_IsRefusedForAnUnmappedUser()
    {
        Envelope(await _server.CallAsync("web_logins", [], Context("stranger")))["errorCode"]!
            .GetValue<string>().ShouldBe(ToolError.Codes.PermissionDenied);
    }

    // nabu cannot speak a link, so a voice login puts it in a new WebChat conversation of the
    // person's — whose end-of-stream Web Push pings them — and tells nabu so.
    [Fact]
    public async Task AVoiceLogin_PutsTheLinkInANewWebChatConversationOfThePersons()
    {
        var envelope = Envelope(await _server.CallAsync("web_login", Login(),
            Context("fran", conversationId: "voice-9", channelId: "voice", address: "kitchen-01")));

        envelope["status"]!.GetValue<string>().ShouldBe("handoff_started");
        envelope["deliveredTo"]!.GetValue<string>().ShouldBe("webchat");
        envelope["link"].ShouldBeNull();
        envelope["hint"]!.GetValue<string>().ShouldContain("WebChat");

        var fire = (await DrainAsync()).ShouldHaveSingleItem();
        fire.ReplyTo.ShouldBe([new ReplyTarget("signalr", null)]);
        fire.ConversationId.ShouldNotBe("voice-9");
        fire.Sender.ShouldBe("Fran");
        fire.UserId.ShouldBe("Fran");
        fire.AgentId.ShouldBe("jonas");
        fire.Origin!.Kind.ShouldBe(MessageOriginKind.Login);
        fire.Content.ShouldContain($"{PublicBase}/browse/handoff/");
    }

    [Fact]
    public async Task ATextLogin_EmitsNoWebChatFire()
    {
        await StartHandoffAsync();

        await Eventually.Settle();
        (await DrainAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task AVoiceLoginForAPersonWithNoWebChatName_IsRefusedNamingThat_AndOpensNothing()
    {
        var envelope = Envelope(await _server.CallAsync("web_login", Login(),
            Context("mo", channelId: "voice", address: "kitchen-01")));

        envelope["errorCode"].ShouldNotBeNull();
        envelope["message"]!.GetValue<string>().ShouldContain("WebChat");
        _browser.OpenedTabs.ShouldBeEmpty();
        (await DrainAsync()).ShouldBeEmpty();
    }

    // The answer comes back on the satellite the request came from: the wake keeps the voice
    // target with its address, and the voice channel's own announcement path speaks it there.
    [Fact]
    public async Task AVoiceLoginsWake_AnswersToTheSatelliteItCameFrom()
    {
        var envelope = Envelope(await _server.CallAsync("web_login", Login(),
            Context("fran", conversationId: "voice-9", channelId: "voice", address: "kitchen-01")));
        var link = (await DrainAsync()).Single().Content.Split(' ', '\n').First(w => w.StartsWith(PublicBase));
        var (client, _) = NewBrowserSession();
        (await client.PostAsync($"{PathOf(link.TrimEnd('.', ')'))}/claim", null)).EnsureSuccessStatusCode();

        (await client.PostAsync("/browse/handoff/done", Json("{}"))).EnsureSuccessStatusCode();

        var wake = (await DrainAsync()).ShouldHaveSingleItem();
        wake.ConversationId.ShouldBe("voice-9");
        wake.ReplyTo.ShouldBe([new ReplyTarget("voice", "voice-9", "kitchen-01")]);
    }

    // The tab died under the view — the browser restarted — so there is no login to keep and
    // nothing to wake the agent about.
    [Fact]
    public async Task DoneAfterTheTabWasLost_IsRefused_AndWakesNobody()
    {
        var path = PathOf(await StartHandoffAsync());
        var (client, _) = NewBrowserSession();
        (await client.PostAsync($"{path}/claim", null)).EnsureSuccessStatusCode();
        await _browser.CloseHandoffAsync(_browser.OpenedTabs.Single().Id);

        (await client.PostAsync("/browse/handoff/done", Json("{}"))).StatusCode.ShouldBe(HttpStatusCode.Gone);

        await Eventually.Settle();
        (await DrainAsync()).ShouldBeEmpty();
    }

    // A login nobody finished stays in the context only until the browser restarts: the save the
    // next use of a remembered one triggers leaves its cookies out.
    [Fact]
    public async Task AnAbandonedHandoffsSite_IsLeftOutOfTheNextSave()
    {
        _browser.State = BankState;
        _browser.Hosts = ["bank.test"];
        await FinishHandoffAsync("""{"remember":true}""", url: "https://bank.test/login");
        _browser.State =
            """{"cookies":[{"name":"sid","value":"bank-rotated","domain":".bank.test","path":"/"},{"name":"sid","value":"shop-session","domain":".shop.test","path":"/"}],"origins":[]}""";
        _browser.Hosts = ["shop.test"];
        await StartHandoffAsync();
        _time.Advance(TimeSpan.FromMinutes(10));
        await Eventually.Until(() => _browser.OpenedTabs.All(t => !_browser.IsHandoffOpen(t.Id)), "the handoff to run out");

        await Eventually.Until(() =>
        {
            _browser.RaiseActivity("fran", "https://bank.test/");
            _time.Advance(TimeSpan.FromSeconds(5));
            return Store.LoadAsync("fran").GetAwaiter().GetResult()!.StorageState.Contains("bank-rotated");
        }, "a save to run");

        (await Store.LoadAsync("fran"))!.StorageState.ShouldNotContain("shop-session");
    }

    [Fact]
    public async Task Forget_IsRefusedWhileTheirHandoffIsOpen()
    {
        _browser.State = ShopState;
        _browser.Hosts = ["shop.test"];
        await FinishHandoffAsync("""{"remember":true}""");
        await StartHandoffAsync();
        var page = await OpenLoginsPageAsync("fran_tg");

        (await page.PostAsync("/browse/logins/forget", Json("""{"site":"shop.test"}"""))).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);
        (await Store.LoadAsync("fran"))!.Logins.Select(login => login.Site).ShouldBe(["shop.test"]);
    }

    [Theory]
    [InlineData("telegram", MessageOriginKind.Schedule)]
    [InlineData("voice", null)]
    public async Task TheLoginsLink_IsRefusedWhereNobodyCanOpenIt(string channelId, MessageOriginKind? origin)
    {
        var envelope = Envelope(await _server.CallAsync("web_logins", [],
            Context("fran_tg", channelId: channelId, originKind: origin)));

        envelope["errorCode"].ShouldNotBeNull();
        envelope["link"].ShouldBeNull();
    }

    [Fact]
    public async Task TheLiveView_RefusesASocketFromAnotherSite()
    {
        var path = PathOf(await StartHandoffAsync());
        var (claimer, cookies) = NewBrowserSession();
        (await claimer.PostAsync($"{path}/claim", null)).EnsureSuccessStatusCode();

        using var socket = new ClientWebSocket();
        socket.Options.Cookies = cookies;
        socket.Options.SetRequestHeader("Origin", "https://evil.example");

        await Should.ThrowAsync<WebSocketException>(() => socket.ConnectAsync(
            new Uri($"{_server.BaseAddress.Replace("http", "ws")}/browse/handoff/live"), CancellationToken.None));
    }

    // The login page's own requests must not arm a save that writes the half-made login while the
    // person is still typing it.
    [Fact]
    public async Task NothingIsSavedWhileTheHandoffIsOpen()
    {
        _browser.State = ShopState;

        await StartHandoffAsync();
        _time.Advance(TimeSpan.FromSeconds(5));
        _browser.RaiseActivity("fran", "https://shop.test/login?step=2");
        _time.Advance(TimeSpan.FromSeconds(5));

        await Eventually.Settle();
        (await Store.LoadAsync("fran")).ShouldBeNull();
    }
}