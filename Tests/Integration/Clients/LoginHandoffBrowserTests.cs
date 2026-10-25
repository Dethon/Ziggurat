using System.Net;
using System.Net.WebSockets;
using System.Text;
using Domain.Contracts;
using Domain.Judgments;
using Domain.Tools.Web;
using Infrastructure.Clients.Browser;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Playwright;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Integration.McpServerTests;
using static Tests.Integration.McpServerTests.WebIdentityServerHarness;

namespace Tests.Integration.Clients;

// The whole handoff against real Camoufox, driven the way a person's browser drives it: the link,
// the claim, frames over the socket, clicks and typing back, Done — and then the agent's own
// `asMe` browse of the site is logged in.
[Collection(PlaywrightCollections.IsolatedSessions)]
public class LoginHandoffBrowserTests(IsolatedSessionBrowserFixture fixture, RedisFixture redis)
    : IClassFixture<RedisFixture>
{
    private const string Site = "https://handoff-site.test";

    // The two fields fill the top of the 1920x1080 viewport in known bands, so a click at a
    // fraction of the frame lands in one: the username across 0-200px, the password 300-500px. The
    // button is what lets Enter submit a form of two fields.
    private static Task ServeAsync(IRoute route) => route.FulfillAsync(new RouteFulfillOptions
    {
        ContentType = "text/html",
        Body = new Uri(route.Request.Url).AbsolutePath switch
        {
            "/login" =>
                """
                <!DOCTYPE html><html><body style="margin:0">
                <form id="f" onsubmit="event.preventDefault(); document.cookie='session=' + document.getElementById('user').value + '; path=/'; document.body.textContent='welcome'">
                  <input id="user" aria-label="Username" style="position:absolute;left:0;top:0;width:100%;height:200px;font-size:40px">
                  <input id="pass" type="password" aria-label="Password" style="position:absolute;left:0;top:300px;width:100%;height:200px;font-size:40px">
                  <button type="submit" style="position:absolute;left:0;top:600px">Sign in</button>
                </form></body></html>
                """,
            _ => "<!DOCTYPE html><html><body><p id='c'></p><script>document.getElementById('c').textContent='cookies:'+(document.cookie||'none')</script></body></html>"
        }
    });

    [Trait("Category", "External")]
    [SkippableFact]
    public async Task AHandedOffLogin_IsInheritedByTheNextAsMeBrowse()
    {
        Skip.IfNot(fixture.IsAvailable, "Camoufox unavailable.");

        var person = $"fran-{Guid.NewGuid():N}";
        var telegram = $"tg-{Guid.NewGuid():N}";
        var conversation = $"conv-{Guid.NewGuid():N}";
        await fixture.Browser.RouteOnContextAsync($"{Site}/**", ServeAsync, person);
        await fixture.Browser.RouteOnContextAsync($"{Site}/**", ServeAsync);

        await using var server = await StartAsync(
            people: [new PersonIdentity { Name = person, Telegram = telegram }],
            configure: services =>
            {
                services.RemoveAll<IWebBrowser>();
                services.RemoveAll<IHandoffBrowser>();
                services.AddSingleton<IWebBrowser>(fixture.Browser);
                services.AddSingleton<IHandoffBrowser>(fixture.Browser);
            },
            settings: s => s with
            {
                RedisConnectionString = redis.ConnectionString,
                Handoff = s.Handoff with { PublicBaseUrl = "https://assistants.test" }
            });
        var context = Context(telegram, conversation);
        var sessionId = $"jonas:{conversation}";
        try
        {
            var started = Envelope(await server.CallAsync(
                "web_login", new Dictionary<string, object?> { ["url"] = $"{Site}/login" }, context));
            var path = new Uri(started["link"]!.GetValue<string>()).AbsolutePath;

            // Nothing the agent holds reaches the handoff tab: its session has no tab at all.
            Envelope(await server.CallAsync("web_snapshot", [], context))["errorCode"].ShouldNotBeNull();

            var cookies = new CookieContainer();
            using var http = new HttpClient(new HttpClientHandler { CookieContainer = cookies })
            {
                BaseAddress = new Uri(server.BaseAddress)
            };
            (await http.PostAsync($"{path}/claim", null)).EnsureSuccessStatusCode();

            using var socket = new ClientWebSocket();
            socket.Options.Cookies = cookies;
            await socket.ConnectAsync(new Uri($"{server.BaseAddress.Replace("http", "ws")}/browse/handoff/live"),
                CancellationToken.None);
            var buffer = new byte[512 * 1024];
            var frame = await socket.ReceiveAsync(buffer, CancellationToken.None);
            frame.MessageType.ShouldBe(WebSocketMessageType.Binary);
            buffer[0].ShouldBe((byte)0xFF, "a frame is a JPEG");

            async Task SendAsync(string json) => await socket.SendAsync(
                Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
            await SendAsync("""{"type":"mousedown","x":0.5,"y":0.09}""");
            await SendAsync("""{"type":"mouseup","x":0.5,"y":0.09}""");
            await SendAsync("""{"type":"text","value":"fran"}""");
            await SendAsync("""{"type":"mousedown","x":0.5,"y":0.37}""");
            await SendAsync("""{"type":"mouseup","x":0.5,"y":0.37}""");
            await SendAsync("""{"type":"text","value":"s3cret"}""");
            await SendAsync("""{"type":"keydown","key":"Enter"}""");
            await SendAsync("""{"type":"keyup","key":"Enter"}""");

            // The typing landed once the person's context holds the cookie the form set.
            var probe = $"probe-{Guid.NewGuid():N}";
            await Eventually.Until(() => WhoAmI(probe, person).Contains("session=fran"),
                "the login typed through the view to set its cookie");
            await fixture.Browser.CloseSessionAsync(probe);

            (await http.PostAsync("/browse/handoff/done",
                new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

            var browse = await server.CallAsync("web_browse",
                new Dictionary<string, object?> { ["url"] = $"{Site}/whoami", ["asMe"] = true }, context);
            string.Join("\n", browse.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(t => t.Text))
                .ShouldContain("session=fran");

            var anonymous = await server.CallAsync("web_browse",
                new Dictionary<string, object?> { ["url"] = $"{Site}/whoami?anon" }, context);
            string.Join("\n", anonymous.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(t => t.Text))
                .ShouldNotContain("session=fran");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    private string WhoAmI(string sessionId, string person)
    {
        var result = fixture.Browser.NavigateAsync(new BrowseRequest(sessionId, $"{Site}/whoami?{Guid.NewGuid():N}")
        {
            Caller = JudgmentCaller.None,
            Person = person
        }).GetAwaiter().GetResult();
        return result.Content ?? "";
    }
}