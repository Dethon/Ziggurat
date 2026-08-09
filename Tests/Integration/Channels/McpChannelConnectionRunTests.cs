using System.ComponentModel;
using System.Net;
using System.Text.Json;
using Domain.DTOs.Channel;
using Infrastructure.Clients.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Integration.McpServers;
using Tests.Unit;

namespace Tests.Integration.Channels;

// A connection is started once and runs for its lifetime: connect, register the catalog, watch
// health, reconnect, re-register. The order lives in the thing the order is about, so this drives
// the real connection against a real server rather than asserting on a host's sequencing.
public class McpChannelConnectionRunTests
{
    private static readonly SemaphoreSlim _registered = new(0);
    private static int _registerCalls;
    private static int _unhealthy;

    private static readonly IReadOnlyList<AgentCatalogEntry> _catalog =
        [new AgentCatalogEntry("jonas", "Jonas", "general")];

    [Fact]
    public async Task RunAsync_RegistersTheCatalogAfterConnecting_AndAgainAfterAReconnect()
    {
        await ResetAsync();

        await using var server = await StartServerAsync();
        await using var connection = new McpChannelConnection(
            "test", healthCheckInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = connection.RunAsync(server.Endpoint, () => _catalog, cts.Token);

        await _registered.WaitAsync(cts.Token);
        Volatile.Read(ref _registerCalls).ShouldBe(1);

        // The next health tick finds the server unreachable, which is what drives a reconnect. It
        // recovers immediately afterwards, so the reconnect succeeds and re-registers.
        Interlocked.Exchange(ref _unhealthy, 1);

        await _registered.WaitAsync(cts.Token);
        Volatile.Read(ref _registerCalls).ShouldBeGreaterThanOrEqualTo(2);

        await cts.CancelAsync();
        await run;
    }


    // Attachment capability is discovered from the model provider and refreshed hourly, so the
    // catalog is not constant. A model that gains image support has to reach the channels while
    // the agent runs, and the registration the channel already has is how it learns.
    [Fact]
    public async Task RunAsync_TheCatalogChangesWhileConnected_RegistersItAgainWithoutAReconnect()
    {
        await ResetAsync();

        var catalog = _catalog;
        await using var server = await StartServerAsync();
        await using var connection = new McpChannelConnection(
            "test", healthCheckInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = connection.RunAsync(server.Endpoint, () => catalog, cts.Token);

        await _registered.WaitAsync(cts.Token);
        Volatile.Read(ref _registerCalls).ShouldBe(1);

        catalog =
        [
            new AgentCatalogEntry(
                "jonas", "Jonas", "general",
                DefaultModelAttachmentKinds: [AttachmentKind.Image])
        ];

        await _registered.WaitAsync(cts.Token);
        Volatile.Read(ref _registerCalls).ShouldBe(2);

        // The catalog is stable again, so nothing further is registered: a change re-registers,
        // a health tick on its own does not.
        await Task.Delay(300, cts.Token);
        Volatile.Read(ref _registerCalls).ShouldBe(2);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task RunAsync_TheServerIsNotThereYet_KeepsRetryingUntilItIs()
    {
        // A channel server that starts after the agent must still get connected to, so the first
        // connect backs off and retries rather than giving up.
        await ResetAsync();
        var port = TestPort.GetAvailable();
        var endpoint = $"http://localhost:{port}/mcp";

        await using var connection = new McpChannelConnection(
            "test", healthCheckInterval: TimeSpan.FromMilliseconds(50));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var run = connection.RunAsync(endpoint, () => _catalog, cts.Token);

        await using var server = await StartServerAsync(port);

        await _registered.WaitAsync(cts.Token);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task RunAsync_AnHttpCancellationTheRunNeverAskedFor_RetriesInsteadOfFaulting()
    {
        // HttpClient reports its own timeouts as TaskCanceledException — an
        // OperationCanceledException whose token the run never tripped. This server produces the
        // same shape mid-connect: it expires the session under the client, so the MCP transport
        // cancels its internal token while the connect call is still in flight. The run must count
        // that as one more failed attempt and retry, because a faulted run faults
        // ChannelConnectionHost's WhenAll and stops the whole agent.
        await ResetAsync();
        Interlocked.Exchange(ref _firstInitializeSeen, 0);

        var port = TestPort.GetAvailable();
        await using var server = await StartSessionExpiringServerAsync(port);
        await using var connection = new McpChannelConnection(
            "test", healthCheckInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = connection.RunAsync($"http://localhost:{port}/mcp", () => _catalog, cts.Token);

        var registered = _registered.WaitAsync(cts.Token);
        var first = await Task.WhenAny(run, registered);
        if (first == run)
        {
            await run; // Surfaces the exception that would have stopped the host.
        }
        await registered;
        Volatile.Read(ref _registerCalls).ShouldBe(1);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task RunAsync_RegistrationFailsButTheLinkIsHealthy_RetriesWithoutReconnecting()
    {
        // A register_agents that errors while list_tools still answers is a server-side hiccup,
        // not a dead link: the catalog must land on a later health tick through the same
        // connection, or the channel serves an empty catalog indefinitely.
        await ResetAsync();
        Interlocked.Exchange(ref _registerRejected, 0);
        Interlocked.Exchange(ref _clientConnects, 0);

        var port = TestPort.GetAvailable();
        await using var server = await StartFlakyRegisterServerAsync(port);
        await using var connection = new McpChannelConnection(
            "test", healthCheckInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = connection.RunAsync($"http://localhost:{port}/mcp", () => _catalog, cts.Token);

        await _registered.WaitAsync(cts.Token);
        Volatile.Read(ref _registerRejected).ShouldBe(1);
        Volatile.Read(ref _registerCalls).ShouldBe(1);
        Volatile.Read(ref _clientConnects).ShouldBe(1);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task RunAsync_RegisterAgentsAnswersWithAToolError_RetriesUntilTheCatalogIsAccepted()
    {
        // The other shape of a refused registration: the channel-side call-tool error filter turns
        // every non-cancellation exception into an IsError *result*, so a server-side failure
        // arrives as a value rather than a throw. A run that only watches for exceptions latches
        // "registered" on it and never asks again, leaving the channel serving an empty catalog.
        await ResetAsync();
        Interlocked.Exchange(ref _registerRefused, 0);

        await using var server = await StartRefusingRegisterServerAsync();
        await using var connection = new McpChannelConnection(
            "test", healthCheckInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = connection.RunAsync(server.Endpoint, () => _catalog, cts.Token);

        await _registered.WaitAsync(cts.Token);
        await _registered.WaitAsync(cts.Token);
        Volatile.Read(ref _registerCalls).ShouldBeGreaterThanOrEqualTo(2);

        await cts.CancelAsync();
        await run;
    }

    // "Not connected reports false" is about the link, not about the caller. The run's own token
    // tripping mid-probe is the shutdown it asked for: reported as false it becomes a failed health
    // check, and the run walks into a reconnect on a token that is already cancelled — which
    // returns without connecting anything, so everything after it works against a dead client while
    // believing the link is up.
    [Fact]
    public async Task IsHealthyAsync_TheCallersOwnTokenIsCancelled_ReportsTheAbortRatherThanIllHealth()
    {
        using var probeArrived = new SemaphoreSlim(0);
        using var probeHeld = new SemaphoreSlim(0);
        await using var server = await StartGatedListToolsServerAsync(probeArrived, probeHeld, gateFrom: 1);
        await using var connection = new McpChannelConnection("test");
        await connection.ConnectAsync(server.Endpoint, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var health = connection.IsHealthyAsync(cts.Token);
        (await probeArrived.WaitAsync(TimeSpan.FromSeconds(30))).ShouldBeTrue();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => health);
        probeHeld.Release();
    }

    [Fact]
    public async Task RunAsync_CancelledDuringTheHealthCheck_StopsWithoutCallingItAFailedCheck()
    {
        await ResetAsync();
        using var probeArrived = new SemaphoreSlim(0);
        using var probeHeld = new SemaphoreSlim(0);
        using var logs = CapturingLoggerProvider.ForLevel(LogLevel.Warning);

        // The registration's capability probe goes through; the first health check is held.
        await using var server = await StartGatedListToolsServerAsync(probeArrived, probeHeld, gateFrom: 2);
        await using var connection = new McpChannelConnection(
            "test",
            logger: new LoggerFactory([logs]).CreateLogger<McpChannelConnection>(),
            healthCheckInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = connection.RunAsync(server.Endpoint, () => _catalog, cts.Token);

        await _registered.WaitAsync(cts.Token);
        (await probeArrived.WaitAsync(TimeSpan.FromSeconds(30))).ShouldBeTrue();
        await cts.CancelAsync();
        probeHeld.Release();

        await run;
        logs.Messages.ShouldNotContain(m => m.Contains("health check failed"));
        Volatile.Read(ref _registerCalls).ShouldBe(1);
    }

    // Lets the first `gateFrom - 1` list_tools calls through and holds every one after them.
    private static Task<RunningServer> StartGatedListToolsServerAsync(
        SemaphoreSlim arrived, SemaphoreSlim held, int gateFrom)
    {
        var seen = 0;
        return InMemoryMcpServer.StartAsync(services => services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<RegisterTools>()
            .WithRequestFilters(filters => filters.AddListToolsFilter(next => async (context, ct) =>
            {
                if (Interlocked.Increment(ref seen) < gateFrom)
                {
                    return await next(context, ct);
                }

                arrived.Release();
                await held.WaitAsync(ct);
                return await next(context, ct);
            })));
    }

    private static Task<RunningServer> StartRefusingRegisterServerAsync() =>
        InMemoryMcpServer.StartAsync(services => services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<RefusingRegisterTools>());

    // Refuses the first ask the way a real channel server does — with an IsError result — and
    // accepts every one after it.
    private static int _registerRefused;

    [McpServerToolType]
    public sealed class RefusingRegisterTools
    {
        [McpServerTool(Name = ChannelProtocol.RegisterAgentsTool)]
        [Description("Take the agent catalog, refusing the first ask.")]
        public static CallToolResult Register(IReadOnlyList<AgentCatalogEntry> agents)
        {
            Interlocked.Increment(ref _registerCalls);
            var refused = Interlocked.Exchange(ref _registerRefused, 1) == 0;
            _registered.Release();
            return new CallToolResult
            {
                IsError = refused,
                Content = [new TextContentBlock { Text = refused ? "catalog rejected" : "ok" }]
            };
        }
    }

    private static async Task ResetAsync()
    {
        Interlocked.Exchange(ref _registerCalls, 0);
        Interlocked.Exchange(ref _unhealthy, 0);
        while (_registered.CurrentCount > 0)
        {
            await _registered.WaitAsync();
        }
    }

    private static Task<RunningServer> StartServerAsync(int? port = null) =>
        InMemoryMcpServer.StartAsync(services => services
        .AddMcpServer()
        .WithHttpTransport()
        .WithTools<RegisterTools>()
        .WithRequestFilters(filters => filters.AddListToolsFilter(next => (context, ct) =>
        {
            // One failed health ping, then healthy again.
            if (Interlocked.Exchange(ref _unhealthy, 0) == 1)
            {
                throw new InvalidOperationException("the channel server went away");
            }
            return next(context, ct);
        })), port);

    [McpServerToolType]
    public sealed class RegisterTools
    {
        [McpServerTool(Name = ChannelProtocol.RegisterAgentsTool)]
        [Description("Take the agent catalog.")]
        public static string Register(IReadOnlyList<AgentCatalogEntry> agents)
        {
            Interlocked.Increment(ref _registerCalls);
            _registered.Release();
            return "ok";
        }
    }

    // State for the session-expiry scenario. The MCP server here answers `server/discover` with a
    // JSON-RPC error so the client falls back to the session-minting initialize handshake, mints a
    // session id for the first initialize itself, then sabotages that first generation: the
    // standalone GET stream gets the 404 the client reads as "session expired" — cancelling the
    // transport's internal token — while the initialized notification is held in flight, so the
    // connect call dies with exactly an HttpClient-timeout-shaped TaskCanceledException.
    // State for the flaky-register scenario. Every client creation opens with exactly one
    // server/discover probe, so counting those counts connections: it is how the test tells a
    // same-connection retry from a reconnect. The rejection is an HTTP 500, because that is what
    // reaches the client as a thrown registration failure; a tool-level error would come back as
    // an IsError result instead.
    private static int _registerRejected;
    private static int _clientConnects;

    private static async Task<WebApplication> StartFlakyRegisterServerAsync(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<RegisterTools>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method))
            {
                context.Request.EnableBuffering();
                string body;
                using (var reader = new StreamReader(context.Request.Body, leaveOpen: true))
                {
                    body = await reader.ReadToEndAsync();
                }
                context.Request.Body.Position = 0;

                if (body.Contains("\"server/discover\""))
                {
                    Interlocked.Increment(ref _clientConnects);
                }
                else if (body.Contains($"\"{ChannelProtocol.RegisterAgentsTool}\"") &&
                    Interlocked.Exchange(ref _registerRejected, 1) == 0)
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    return;
                }
            }
            await next();
        });
        app.MapMcp("/mcp");
        await app.StartAsync();
        return app;
    }

    private const string ExpiringSession = "expiring-session";
    private static int _firstInitializeSeen;

    private static async Task<WebApplication> StartSessionExpiringServerAsync(int port)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<RegisterTools>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var inExpiringSession = context.Request.Headers["Mcp-Session-Id"] == ExpiringSession;
            if (HttpMethods.IsGet(context.Request.Method) && inExpiringSession)
            {
                // The 404 the client reads as "session expired": it cancels the transport's
                // internal token, under whatever call is in flight.
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (!HttpMethods.IsPost(context.Request.Method))
            {
                await next();
                return;
            }

            context.Request.EnableBuffering();
            string body;
            using (var reader = new StreamReader(context.Request.Body, leaveOpen: true))
            {
                body = await reader.ReadToEndAsync();
            }
            context.Request.Body.Position = 0;

            if (body.Contains("\"server/discover\""))
            {
                var id = JsonDocument.Parse(body).RootElement.GetProperty("id").GetInt64();
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync(
                    "{\"jsonrpc\":\"2.0\",\"id\":" + id +
                    ",\"error\":{\"code\":-32601,\"message\":\"Method not found\"}}");
                return;
            }

            if (body.Contains("\"method\":\"initialize\"") &&
                Interlocked.Exchange(ref _firstInitializeSeen, 1) == 0)
            {
                context.Response.Headers["Mcp-Session-Id"] = ExpiringSession;
            }
            else if (body.Contains("\"notifications/initialized\"") && inExpiringSession)
            {
                // If this raced ahead of the expiry it is held open, never answered: the only way
                // the client-side call ends is the internal cancellation the 404 above triggers.
                await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted);
                return;
            }

            await next();
        });
        app.MapMcp("/mcp");
        await app.StartAsync();
        return app;
    }
}