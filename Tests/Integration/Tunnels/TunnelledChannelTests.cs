using System.Net;
using System.Net.Http.Json;
using Agent.App;
using Agent.Settings;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Security;
using Domain.Security.Signing;
using Domain.Tools.HomeAssistant.Vfs;
using Infrastructure.Agents.Mcp;
using Infrastructure.Clients.Channels;
using McpServerHomeAssistant.Modules;
using McpServerHomeAssistant.Services;
using McpServerHomeAssistant.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Eval.Fixtures;
using Tests.Integration.Fixtures;
using Tests.Unit;

namespace Tests.Integration.Tunnels;

// A household's channel server on its tunnel, and the agent's channel connection run the way the
// deployment runs it: an endpoint naming the server, followed through the relay's openings of the
// home household's tunnel of that name. Everything the channel does — the long-poll, a reply, a
// fire's delegation — crosses the real tunnel.
public class TunnelledChannelTests(RedisFixture redis) : IClassFixture<RedisFixture>, IAsyncLifetime
{
    private const string AgentCredential = "agent-credential";
    private const string FranId = "a3f09c1d7e2b4c6a8d5e0f1b2c3d4e5f";

    private static readonly IReadOnlyList<AgentCatalogEntry> _catalog = [new AgentCatalogEntry("jonas", "Jonas", "the assistant")];

    private readonly CancellationTokenSource _cts = new(TimeSpan.FromSeconds(60));
    private AccountsServer _accounts = null!;
    private RelayHost _relay = null!;
    private string _household = null!;

    public async Task InitializeAsync()
    {
        _accounts = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential },
            Redis = new() { ConnectionString = redis.ConnectionString }
        });
        // The operator's household is the home one, which the deployment's channel endpoints name.
        _household = (await _accounts.MintOperatorAsync("Fran")).TenantId;
        // The suite's agent key, so the calls the test signs and the delegations it checks are the
        // agent's own as far as the tunnelled server is concerned.
        _relay = await RelayHost.StartAsync(
            _accounts, AgentCredential, redis.ConnectionString, TestSigningKeys.Store(TestSigningKeys.Agent),
            new RecordingMetricsPublisher());
    }

    public async Task DisposeAsync()
    {
        await _cts.CancelAsync();
        await _relay.DisposeAsync();
        await _accounts.DisposeAsync();
        _cts.Dispose();
    }

    [Fact]
    public async Task ATunnelledChannelsNotification_ReachesTheAgent_AndItsReplyReachesTheServerThroughTheTunnel()
    {
        var credential = await _accounts.MintTunnelCredentialAsync(_household, "websearch");
        await using var server = await TunnelledChannelServer.StartAsync(_relay.Hub, credential.Credential, "websearch");
        await using var connection = Connection("websearch");
        await using var running = await RunAsync(connection, "websearch");

        var message = await DeliveredAsync(server, connection, "The login is done.");
        await connection.SendReplyAsync(new SendReplyParams
        {
            ConversationId = message.ConversationId,
            Content = "Thanks, carrying on.",
            ContentType = ReplyContentType.Text,
            IsComplete = true
        }, _cts.Token);

        message.ChannelId.ShouldBe("websearch");
        server.Replies.ShouldContain("Thanks, carrying on.");
    }

    [Fact]
    public async Task AChannel_IsDroppedWhenItsTunnelCloses_AndConnectedAgainWhenItReopens()
    {
        var credential = await _accounts.MintTunnelCredentialAsync(_household, "websearch");
        await using var connection = Connection("websearch");
        await using var running = await RunAsync(connection, "websearch");
        var first = await TunnelledChannelServer.StartAsync(_relay.Hub, credential.Credential, "websearch");
        await DeliveredAsync(first, connection, "before");

        await first.DisposeAsync();
        await Eventually.Until(async () => await IsNotConnectedAsync(connection), "the channel is dropped with its tunnel");
        await using var second = await TunnelledChannelServer.StartAsync(_relay.Hub, credential.Credential, "websearch");

        (await DeliveredAsync(second, connection, "after")).Content.ShouldBe("after");
    }

    // A watch Fran's turn set through the tunnel fires at her home, and the fire the agent reads off
    // the tunnel carries the delegation the agent signed for her, checked the way the conversation
    // group checks it.
    [Fact]
    public async Task AWatchFireThroughTheTunnel_ActsForThePersonWhoSetTheWatch()
    {
        var fran = new Account(_household, FranId);
        var credential = await _accounts.MintTunnelCredentialAsync(_household, "homeassistant");
        var port = TestPort.GetAvailable();
        await using var home = await TunnelledHomeAssistant.StartAsync(port, _relay.Hub, credential.Credential);
        await using var connection = Connection("homeassistant");
        await using var running = await RunAsync(connection, "homeassistant");
        await Eventually.Until(() => _relay.Registry.Find(_household, "homeassistant") is not null, "the home's tunnel opens");
        await TunnelledHomeAssistant.WriteWatchAsync(_relay, fran, TunnelledHomeAssistant.LauraWatch(["telegram"]), _cts.Token);
        var reading = connection.Messages.FirstAsync(_cts.Token).AsTask();

        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
        http.DefaultRequestHeaders.Add(WatchFiredEndpoint.TokenHeader, "secret");
        await Eventually.Until(async () =>
            (await http.PostAsJsonAsync(WatchFiredEndpoint.Path, new
            {
                watchId = "laura-sugar-high",
                name = "Laura's sugar",
                agentId = "jonas",
                prompt = "Look into it and warn Fran.",
                promptIndex = 0,
                deliverTo = new[] { "telegram" },
                entityId = "sensor.glucosa_laura",
                toState = "183"
            }, _cts.Token)).StatusCode == HttpStatusCode.Accepted,
            "the agent's first poll through the tunnel registers it, so the fire is accepted");
        var fire = await reading;

        fire.ChannelId.ShouldBe("homeassistant");
        (await TestDelegations.Verifier().VerifyFireAsync(fire, _cts.Token)).ShouldBeOfType<FireAdmission.Delegated>()
            .Identity.Account.ShouldBe(fran);
        TestPort.Release(port);
    }

    private static McpChannelConnection Connection(string channelId) =>
        new(channelId, healthCheckInterval: TimeSpan.FromMilliseconds(200), callTokens: McpTestSecret.CallTokens);

    private async Task<Running> RunAsync(McpChannelConnection connection, string serverName)
    {
        var host = new ChannelConnectionHost(
            [new ChannelEndpoint { ChannelId = connection.ChannelId, Endpoint = serverName }],
            [], () => _catalog, NullLogger<ChannelConnectionHost>.Instance,
            ownServers: _relay.ChannelServers, householdConnection: (_, _) => connection);
        await host.StartAsync(_cts.Token);
        return new Running(host);
    }

    // The broadcast emit says whether anyone polls: only once the agent's long-poll through the
    // tunnel has registered it is a notification taken.
    private async Task<ChannelMessage> DeliveredAsync(TunnelledChannelServer server, McpChannelConnection connection, string content)
    {
        var reading = connection.Messages.FirstAsync(_cts.Token).AsTask();
        await Eventually.Until(
            async () => await server.Emitter.EmitAsync(
                new ChannelMessageNotification { ConversationId = "conv-1", Sender = "websearch", Content = content, AgentId = "jonas" },
                _cts.Token),
            "the agent polls the channel through its tunnel");
        return await reading.WaitAsync(_cts.Token);
    }

    // Not connected is a reply that throws before it is sent (ADR 0011); one that fails on its way
    // down a closing tunnel is still a connection that has not let go.
    private async Task<bool> IsNotConnectedAsync(McpChannelConnection connection)
    {
        try
        {
            await connection.SendReplyAsync(new SendReplyParams
            {
                ConversationId = "conv-1", Content = "x", ContentType = ReplyContentType.Text, IsComplete = true
            }, _cts.Token);
            return false;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private sealed class Running(ChannelConnectionHost host) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await host.StopAsync(CancellationToken.None);
            host.Dispose();
        }
    }
}