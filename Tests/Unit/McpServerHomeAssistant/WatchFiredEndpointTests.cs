using System.Net;
using System.Net.Http.Json;
using Domain.Channels;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Mcp.Hosting;
using McpServerHomeAssistant.Services;
using McpServerHomeAssistant.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpServerHomeAssistant;

// The watch callback at its HTTP boundary: what Home Assistant's rest_command posts, what status
// its trace shows, and what lands in the inbox the agent drains. A real ChannelInbox behind the
// real emitter, drained the way the agent's channel connection drains it.
public class WatchFiredEndpointTests
{
    private const string Subscriber = ChannelProtocol.ChannelClientNamePrefix + "homeassistant";
    private static readonly DateTimeOffset _now = new(2026, 9, 5, 3, 14, 0, TimeSpan.Zero);

    private sealed record Harness(HttpClient Client, ChannelInbox Inbox, FakeTimeProvider Clock)
    {
        public async Task<IReadOnlyList<ChannelMessageNotification>> DrainAsync() =>
            (await Inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None))
                .Where(i => i.Kind == ChannelInboxItemKind.Message).Select(i => i.Message!).ToList();

        public Task<HttpResponseMessage> FireAsync(object payload, string? token = "expected")
        {
            var request = new HttpRequestMessage(HttpMethod.Post, WatchFiredEndpoint.Path) { Content = JsonContent.Create(payload) };
            if (token is not null)
            {
                request.Headers.Add(WatchFiredEndpoint.TokenHeader, token);
            }
            return Client.SendAsync(request);
        }
    }

    private static async Task<Harness> BuildAsync(bool subscribed = true, string token = "expected", string[]? defaultDeliverTo = null)
    {
        var clock = new FakeTimeProvider(_now);
        var inbox = new ChannelInbox(clock);
        if (subscribed)
        {
            // A first poll is how production registers the agent.
            await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);
        }

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddSingleton(new McpSettings
            {
                HomeAssistant = new HomeAssistantConfiguration { BaseUrl = "http://home-assistant", Token = "x" },
                Announce = new AnnounceTokenSettings { Token = token },
                Delivery = new DeliverySettings { DefaultDeliverTo = defaultDeliverTo ?? ["signalr"] }
            })
            .AddSingleton(inbox)
            .AddSingleton(new ChannelNotificationEmitter(inbox, DeliveryPolicy.Broadcast))
            .AddSingleton<TimeProvider>(clock);
        var app = builder.Build();
        WatchFiredEndpoint.Map(app);
        await app.StartAsync();
        return new Harness(app.GetTestClient(), inbox, clock);
    }

    private static object Fire(string? deliverTo = "telegram", string? userId = "fran") => new
    {
        watchId = "laura-sugar-high",
        name = "Laura's sugar",
        agentId = "jonas",
        prompt = "Look into it and warn Fran.",
        deliverTo = deliverTo is null ? null : new[] { deliverTo },
        userId,
        entityId = "sensor.laura_glucose",
        friendlyName = "Laura glucose",
        fromState = "176",
        toState = "183",
        description = "numeric state of sensor.laura_glucose",
        firedAt = "2026-09-05T05:14:00+02:00"
    };

    [Fact]
    public async Task NoToken_Is401()
    {
        var harness = await BuildAsync();
        (await harness.FireAsync(Fire(), token: null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await harness.DrainAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task WrongToken_Is401()
    {
        var harness = await BuildAsync();
        (await harness.FireAsync(Fire(), token: "wrong")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // An unset secret refuses everyone rather than admitting anyone.
    [Fact]
    public async Task UnconfiguredToken_Is401ForEveryCaller()
    {
        var harness = await BuildAsync(token: "");
        (await harness.FireAsync(Fire(), token: "")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("""{"agentId":"jonas","prompt":"p"}""", "watchId")]
    [InlineData("""{"watchId":"w","prompt":"p"}""", "agentId")]
    [InlineData("""{"watchId":"w","agentId":"jonas"}""", "prompt")]
    [InlineData("""not json""", "not the watch-fired JSON")]
    public async Task MalformedPayload_Is400NamingWhatIsMissing(string body, string expected)
    {
        var harness = await BuildAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, WatchFiredEndpoint.Path)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add(WatchFiredEndpoint.TokenHeader, "expected");

        var response = await harness.Client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain(expected);
        (await harness.DrainAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task AFire_Is202_AndTheAgentDrainsTheComposedPrompt()
    {
        var harness = await BuildAsync();

        var response = await harness.FireAsync(Fire());

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var fire = (await harness.DrainAsync()).ShouldHaveSingleItem();
        fire.ConversationId.ShouldBe($"watch-laura-sugar-high-{_now.ToUnixTimeSeconds()}");
        fire.Sender.ShouldBe("watch");
        fire.AgentId.ShouldBe("jonas");
        fire.UserId.ShouldBe("fran");
        fire.Timestamp.ShouldBe(_now);
        fire.Content.Split('\n')[0].ShouldBe(
            "[watch \"Laura's sugar\" fired] Laura glucose (sensor.laura_glucose) went from 176 to 183 at 2026-09-05T05:14:00+02:00.");
        fire.Content.ShouldEndWith("Look into it and warn Fran.");
        fire.ReplyTo.ShouldBe([new ReplyTarget("telegram", null)]);
        fire.Origin.ShouldBe(new MessageOrigin(MessageOriginKind.Watch, null, "laura-sugar-high", "Laura's sugar"));
    }

    // The same parsing and coalescing a schedule's deliverTo gets: two satellites are one voice
    // target, and a fire naming no channel takes the shared default.
    [Fact]
    public async Task DeliverTo_IsCoalescedAsASchedulesIs_AndDefaultsFromTheSharedPolicy()
    {
        var harness = await BuildAsync(defaultDeliverTo: ["signalr"]);

        await harness.FireAsync(new
        {
            watchId = "w",
            agentId = "jonas",
            prompt = "p",
            deliverTo = new[] { "voice:office-01", "voice:office-02", "signalr" }
        });
        await harness.FireAsync(new { watchId = "w", agentId = "jonas", prompt = "p" });

        var fires = await harness.DrainAsync();
        fires[0].ReplyTo.ShouldBe([new ReplyTarget("voice", null, "office-01,office-02"), new ReplyTarget("signalr", null)]);
        fires[1].ReplyTo.ShouldBe([new ReplyTarget("signalr", null)]);
        fires[1].Origin!.Title.ShouldBe("w");
    }

    [Fact]
    public async Task AFireWithNoFacts_StillSaysWhatFired()
    {
        var harness = await BuildAsync();

        await harness.FireAsync(new { watchId = "w", name = "Template watch", agentId = "jonas", prompt = "p" });

        (await harness.DrainAsync()).Single().Content.Split('\n')[0]
            .ShouldBe("[watch \"Template watch\" fired] the home changed.");
    }

    // Nobody registered at all is the lost fire, and the trace must see it as one.
    [Fact]
    public async Task NoSubscriber_Is503SayingNoAgentIsConnected()
    {
        var harness = await BuildAsync(subscribed: false);

        var response = await harness.FireAsync(Fire());

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).ShouldContain("No agent is connected");
    }

    // A registered subscriber that has gone quiet — the agent mid-reconnect — is not a loss: its
    // copy is buffered and taken on the next poll, and the home is told the fire was accepted.
    [Fact]
    public async Task AStaleButRegisteredSubscriber_StillReceivesTheFire_AndTheHomeSees202()
    {
        var harness = await BuildAsync();
        harness.Clock.Advance(ChannelInbox._liveSubscriberFreshness + TimeSpan.FromMinutes(1));

        var response = await harness.FireAsync(Fire());

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await response.Content.ReadAsStringAsync()).ShouldContain("\"delivered\":false");
        (await harness.DrainAsync()).ShouldHaveSingleItem().Origin!.WatchId.ShouldBe("laura-sugar-high");
    }
}