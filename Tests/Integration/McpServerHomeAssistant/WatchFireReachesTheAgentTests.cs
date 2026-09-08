using System.Net;
using System.Net.Http.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Channels;
using McpServerHomeAssistant.Modules;
using McpServerHomeAssistant.Services;
using McpServerHomeAssistant.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.Eval.Fixtures;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerHomeAssistant;

// The whole bridge on this side: the real Home Assistant server, registered as it ships, a real
// agent channel connection dialled into it, and a fire posted the way the home's rest_command posts
// it. What comes out of the connection is what the monitor would build a turn from.
public class WatchFireReachesTheAgentTests
{
    [Fact]
    public async Task AFirePostedByTheHome_ReachesTheConnectedAgent_AsAWatchOriginatedMessage()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureMcp(new McpSettings
        {
            HomeAssistant = new HomeAssistantConfiguration { BaseUrl = "http://home-assistant.test", Token = FakeHomeAssistant.Token },
            Announce = new AnnounceTokenSettings { Token = "secret" },
            Delivery = new DeliverySettings { DefaultDeliverTo = ["signalr"] }
        });
        var home = new FakeHomeAssistant();
        builder.Services.AddHttpClient(nameof(IHomeAssistantClient)).ConfigurePrimaryHttpMessageHandler(() => home);
        var app = builder.Build();
        app.MapMcp("/mcp");
        WatchFiredEndpoint.Map(app);
        await app.StartAsync(cts.Token);

        try
        {
            await using var connection = new McpChannelConnection("homeassistant", healthCheckInterval: TimeSpan.FromMilliseconds(200));
            var run = connection.RunAsync($"http://localhost:{port}/mcp",
                () => [new AgentCatalogEntry("jonas", "Jonas", "the assistant")], cts.Token);
            var reading = connection.Messages.FirstAsync(cts.Token).AsTask();

            // The first poll registers the subscriber; a fire before it is answered 503.
            using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
            http.DefaultRequestHeaders.Add(WatchFiredEndpoint.TokenHeader, "secret");
            await Eventually.Until(async () =>
            {
                var response = await http.PostAsJsonAsync(WatchFiredEndpoint.Path, new
                {
                    watchId = "laura-sugar-high",
                    name = "Laura's sugar",
                    agentId = "jonas",
                    prompt = "Look into it.",
                    deliverTo = new[] { "telegram" },
                    entityId = "sensor.glucosa_laura",
                    friendlyName = "Glucosa Laura",
                    fromState = "176",
                    toState = "183"
                }, cts.Token);
                return response.StatusCode == HttpStatusCode.Accepted;
            }, "the agent's first poll to register it, so the fire is accepted");

            var message = await reading;
            message.ChannelId.ShouldBe("homeassistant");
            message.AgentId.ShouldBe("jonas");
            message.Sender.ShouldBe("watch");
            message.Content.ShouldStartWith("[watch \"Laura's sugar\" fired] Glucosa Laura (sensor.glucosa_laura) went from 176 to 183");
            message.Content.ShouldEndWith("Look into it.");
            message.ReplyTo.ShouldBe([new ReplyTarget("telegram", null)]);
            // The title a minted conversation takes, so the answer lands in one named after the watch.
            message.Origin.ShouldBe(new MessageOrigin(MessageOriginKind.Watch, null, "laura-sugar-high", "Laura's sugar"));

            await cts.CancelAsync();
            try
            { await run; }
            catch (OperationCanceledException) { }
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
            TestPort.Release(port);
        }
    }
}