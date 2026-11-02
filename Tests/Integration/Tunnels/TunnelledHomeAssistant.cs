using System.Net;
using System.Net.Http.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Security;
using Domain.Security.Signing;
using Domain.Tools.HomeAssistant.Vfs;
using Infrastructure.Agents.Mcp;
using McpServerHomeAssistant.Modules;
using McpServerHomeAssistant.Services;
using McpServerHomeAssistant.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Eval.Fixtures;

namespace Tests.Integration.Tunnels;

// The real Home Assistant server over a household's tunnel, in front of the eval's fake home: a
// person's turn writes a watch through the tunnel as the agent's filesystem writes it, and the home's
// callback fires it.
internal static class TunnelledHomeAssistant
{
    public const string AnnounceToken = "secret";

    public static async Task<IAsyncDisposable> StartAsync(int port, string hub, string credential)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureMcp(new McpSettings
        {
            HomeAssistant = new HomeAssistantConfiguration { BaseUrl = "http://home-assistant.test", Token = FakeHomeAssistant.Token },
            Announce = new AnnounceTokenSettings { Token = AnnounceToken },
            Delivery = TestDelegations.Delivery,
            Mcp = new McpGateSettings { Tunnel = TunnelledServer.Quick(hub, credential, "homeassistant") }
        });
        var home = new FakeHomeAssistant();
        builder.Services.AddHttpClient(nameof(IHomeAssistantClient)).ConfigurePrimaryHttpMessageHandler(() => home);
        var app = builder.Build();
        app.MapMcp("/mcp");
        WatchFiredEndpoint.Map(app);
        await app.StartAsync();
        return new Stopping(app);
    }

    public static string LauraWatch(IReadOnlyList<string> deliverTo) => $$"""
        {"name":"Laura's sugar","triggers":[{"trigger":"numeric_state","entity_id":"sensor.glucosa_laura","above":180}],
         "effects":[{"kind":"prompt","prompt":"Look into it and warn Fran."}],"deliverTo":{{System.Text.Json.JsonSerializer.Serialize(deliverTo)}}}
        """;

    // A person's turn writing the watch, as the agent's filesystem writes it: through the household's
    // tunnel, signing the call and the delegation for them.
    public static async Task WriteWatchAsync(RelayHost relay, Account person, string watch, CancellationToken ct)
    {
        var endpoint = relay.OwnServers.Resolve(person.TenantId, "homeassistant")!;
        await using var client = await McpClient.CreateAsync(
            McpClientManager.Signed(
                new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(endpoint.Address) }, endpoint.Through!, ownsHttpClient: false),
                endpoint.Server!, McpTestSecret.CallTokens),
            cancellationToken: ct);
        var turn = new ConversationContext("jonas", "conv-1", person.UserId, new ReplyTarget(ChannelIds.SignalR, "conv-1"),
            Principal: new Principal(ChannelIds.SignalR, person.UserId, person),
            Identity: IdentityClaims.Person(person.UserId, person.TenantId, ChannelIds.SignalR, ["member"]));
        (await new McpFileSystemBackend(client, HaFileSystem.Name, null).As(turn)
                .CreateAsync("/watches/laura-sugar-high/watch.json", watch, false, true, ct))
            .ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
    }

    // The home's automation calling back when the watch's trigger fires.
    public static async Task FireAsync(int port, IReadOnlyList<string> deliverTo, CancellationToken ct)
    {
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        http.DefaultRequestHeaders.Add(WatchFiredEndpoint.TokenHeader, AnnounceToken);
        await Eventually.Until(async () =>
                (await http.PostAsJsonAsync(WatchFiredEndpoint.Path, new
                {
                    watchId = "laura-sugar-high",
                    name = "Laura's sugar",
                    agentId = "jonas",
                    prompt = "Look into it and warn Fran.",
                    promptIndex = 0,
                    deliverTo,
                    entityId = "sensor.glucosa_laura",
                    toState = "183"
                }, ct)).StatusCode == HttpStatusCode.Accepted,
            "the agent's first poll through the tunnel registers it, so the fire is accepted");
    }

    private sealed class Stopping(WebApplication app) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }
}