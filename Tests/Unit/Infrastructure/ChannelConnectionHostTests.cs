using System.Collections.Concurrent;
using System.Threading.Channels;
using Agent.App;
using Agent.Settings;
using Domain.DTOs.Channel;
using Infrastructure.Agents;
using Infrastructure.Clients.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.Infrastructure;

// All the host still owns is which connections have an endpoint and what that endpoint is. The
// sequence a connection is driven in belongs to the connection now, and is asserted against a real
// server in Tests/Integration/Channels/McpChannelConnectionRunTests.cs.
public class ChannelConnectionHostTests
{
    private readonly NullLogger<ChannelConnectionHost> _logger = new();

    private static readonly IReadOnlyList<AgentCatalogEntry> _catalog =
        [new AgentCatalogEntry("jonas", "Jonas", "general")];

    [Fact]
    public async Task ExecuteAsync_EveryConnectionHasAnEndpoint_RunsThemAll()
    {
        var first = new FakeMcpChannelConnection("ch-1");
        var second = new FakeMcpChannelConnection("ch-2");
        var endpoints = new[]
        {
            new ChannelEndpoint { ChannelId = "ch-1", Endpoint = "http://localhost:9001" },
            new ChannelEndpoint { ChannelId = "ch-2", Endpoint = "http://localhost:9002" }
        };
        var sut = new ChannelConnectionHost(endpoints, [first, second], () => _catalog, _logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = sut.StartAsync(cts.Token);

        await first.WaitForRunAsync(cts.Token);
        await second.WaitForRunAsync(cts.Token);

        first.Endpoint.ShouldBe("http://localhost:9001");
        second.Endpoint.ShouldBe("http://localhost:9002");
        first.Agents.ShouldBe(_catalog);
    }

    [Fact]
    public async Task ExecuteAsync_AConnectionWithNoEndpoint_IsNeverRun()
    {
        var configured = new FakeMcpChannelConnection("ch-1");
        var unconfigured = new FakeMcpChannelConnection("ch-2");
        var endpoints = new[] { new ChannelEndpoint { ChannelId = "ch-1", Endpoint = "http://localhost:9001" } };
        var sut = new ChannelConnectionHost(endpoints, [configured, unconfigured], () => _catalog, _logger);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = sut.StartAsync(cts.Token);

        await configured.WaitForRunAsync(cts.Token);
        unconfigured.RunCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_AnEndpointWithNoConnection_WarnsNamingTheOrphan()
    {
        // A typo'd ChannelId in configuration means that endpoint is silently never run; the
        // warning is the only trace an operator gets.
        var connection = new FakeMcpChannelConnection("ch-1");
        var endpoints = new[]
        {
            new ChannelEndpoint { ChannelId = "ch-1", Endpoint = "http://localhost:9001" },
            new ChannelEndpoint { ChannelId = "ch-ghost", Endpoint = "http://localhost:9002" }
        };
        var warnings = CapturingLoggerProvider.ForLevel(LogLevel.Warning);
        using var factory = LoggerFactory.Create(builder => builder.AddProvider(warnings));
        var sut = new ChannelConnectionHost(
            endpoints, [connection], () => _catalog, factory.CreateLogger<ChannelConnectionHost>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = sut.StartAsync(cts.Token);
        await connection.WaitForRunAsync(cts.Token);

        warnings.Messages.ShouldContain(m => m.Contains("ch-ghost"));
    }

    // A channel server checks the first tool call it takes against the keys this process publishes,
    // so a connection dialled before the agent listens has its calls refused until the server may
    // fetch again. The host dials once the agent is listening, and not before.
    [Fact]
    public async Task ExecuteAsync_BeforeTheAgentIsListening_DialsNothing()
    {
        var connection = new FakeMcpChannelConnection("ch-1");
        var endpoints = new[] { new ChannelEndpoint { ChannelId = "ch-1", Endpoint = "http://localhost:9001" } };
        using var listening = new CancellationTokenSource();
        var sut = new ChannelConnectionHost(endpoints, [connection], () => _catalog, _logger, listening.Token);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = sut.StartAsync(cts.Token);
        await Eventually.Settle();
        connection.RunCount.ShouldBe(0);

        await listening.CancelAsync();
        await connection.WaitForRunAsync(cts.Token);
        connection.RunCount.ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_AUrlEndpoint_IsRunPresentingItsServersCredential()
    {
        var connection = new FakeMcpChannelConnection("ch-1");
        var endpoints = new[] { new ChannelEndpoint { ChannelId = "ch-1", Endpoint = "http://mcp-channel:8080/mcp" } };
        var sut = new ChannelConnectionHost(
            endpoints, [connection], () => _catalog, _logger,
            credentials: new McpCredentials(new Dictionary<string, string> { ["mcp-channel"] = "its-secret" }));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = sut.StartAsync(cts.Token);
        await connection.WaitForRunAsync(cts.Token);

        connection.Secret.ShouldBe("its-secret");
    }

    // A household server's channel is not dialled: its connection runs once per opening of its
    // tunnel, lets go when the opening closes, and runs again on the next, never two at once.
    [Fact]
    public async Task ExecuteAsync_AnEndpointNamingAHouseholdServer_RunsOncePerOpeningOfItsTunnel()
    {
        var connection = new FakeMcpChannelConnection("homeassistant");
        var servers = new FakeOwnChannelServers();
        var endpoints = new[] { new ChannelEndpoint { ChannelId = "homeassistant", Endpoint = "homeassistant" } };
        var sut = new ChannelConnectionHost(
            endpoints, [], () => _catalog, _logger, ownServers: servers, householdConnection: (_, _) => connection);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = sut.StartAsync(cts.Token);
        await Eventually.Until(() => servers.Watched == "homeassistant", "the host follows the server's openings");

        using var first = servers.Open("http://homeassistant.home.tunnel/mcp");
        await Eventually.Until(() => connection.Running == 1, "the first opening is run");
        await first.CancelAsync();
        await Eventually.Until(() => connection.Running == 0, "the closed opening's run ends");
        using var replaced = servers.Open("http://homeassistant.home.tunnel/mcp#2");
        await Eventually.Until(() => connection.RunCount == 2, "the next opening is run");
        servers.Open("http://homeassistant.home.tunnel/mcp#3").Dispose();
        await Eventually.Until(() => connection.RunCount == 3, "a newer opening replaces the one still open");

        connection.Runs.ShouldBe(
            ["http://homeassistant.home.tunnel/mcp", "http://homeassistant.home.tunnel/mcp#2", "http://homeassistant.home.tunnel/mcp#3"]);
        connection.MostAtOnce.ShouldBe(1);
        await sut.StopAsync(CancellationToken.None);
        connection.Running.ShouldBe(0);
    }

    // One household's reconnect is its own: an old run slow to let go holds up that household's next
    // run alone, never another household's opening.
    [Fact]
    public async Task ExecuteAsync_AHouseholdsSlowReconnect_NeverHoldsUpAnotherHouseholdsOpening()
    {
        var letsGo = new TaskCompletionSource();
        var casa = new FakeMcpChannelConnection("voice") { LetsGo = letsGo };
        var home = new FakeMcpChannelConnection("voice");
        var servers = new FakeOwnChannelServers();
        var endpoints = new[] { new ChannelEndpoint { ChannelId = "voice", Endpoint = "voice" } };
        var sut = new ChannelConnectionHost(
            endpoints, [], () => _catalog, _logger, ownServers: servers,
            householdConnection: (_, household) => household.HouseholdId == "casa" ? casa : home);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = sut.StartAsync(cts.Token);
        await Eventually.Until(() => servers.Watched == "voice", "the host follows the server's openings");

        using var first = servers.Open("http://voice.casa.tunnel/mcp", "casa");
        await Eventually.Until(() => casa.Running == 1, "Casa's first opening is run");
        using var replacement = servers.Open("http://voice.casa.tunnel/mcp#2", "casa");
        using var homeOpening = servers.Open("http://voice.home.tunnel/mcp", "home");

        await Eventually.Until(() => home.Running == 1, "the home's opening runs while Casa's old run is still letting go");
        casa.RunCount.ShouldBe(1);
        letsGo.SetResult();
        await Eventually.Until(() => casa.RunCount == 2, "Casa's replacement runs once its old run let go");
        casa.MostAtOnce.ShouldBe(1);
        await sut.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Constructor_TwoEndpointsForOneChannelId_FailsNamingTheDuplicate()
    {
        // A duplicated entry stops the agent either way; the difference is whether an operator can
        // tell which one to fix. Building the endpoint map alone threw with no channel id in the
        // message, and only once the host was already running.
        var connection = new FakeMcpChannelConnection("ch-1");
        var endpoints = new[]
        {
            new ChannelEndpoint { ChannelId = "ch-1", Endpoint = "http://localhost:9001" },
            new ChannelEndpoint { ChannelId = "ch-1", Endpoint = "http://localhost:9002" }
        };

        var error = Should.Throw<InvalidOperationException>(
            () => new ChannelConnectionHost(endpoints, [connection], () => _catalog, _logger));

        error.Message.ShouldContain("ch-1");
        connection.RunCount.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_Cancelled_StopsWithoutThrowing()
    {
        var fake = new FakeMcpChannelConnection("ch-1");
        var endpoints = new[] { new ChannelEndpoint { ChannelId = "ch-1", Endpoint = "http://localhost:9001" } };
        var sut = new ChannelConnectionHost(endpoints, [fake], () => _catalog, _logger);

        using var cts = new CancellationTokenSource();
        var task = sut.StartAsync(cts.Token);

        await fake.WaitForRunAsync(cts.Token);

        await cts.CancelAsync();
        await sut.StopAsync(CancellationToken.None);

        await task; // should not throw
    }
}

internal sealed class FakeMcpChannelConnection(string channelId) : IMcpChannelConnection
{
    private readonly TaskCompletionSource _started =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<string> _runs = new();
    private int _runCount;
    private int _running;
    private int _mostAtOnce;

    public string ChannelId { get; } = channelId;
    public int RunCount => Volatile.Read(ref _runCount);
    public string? Endpoint { get; private set; }
    public string? Secret { get; private set; }
    public IReadOnlyList<AgentCatalogEntry>? Agents { get; private set; }

    // Every run's endpoint, in the order they started.
    public IReadOnlyList<string> Runs => [.. _runs];

    public int Running => Volatile.Read(ref _running);

    public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

    // A run that, once cancelled, lets go only when this completes: a connection slow to close.
    public TaskCompletionSource? LetsGo { get; init; }

    public async Task RunAsync(
        McpServerEndpoint endpoint, Func<IReadOnlyList<AgentCatalogEntry>> catalog, CancellationToken ct)
    {
        Endpoint = endpoint.Address;
        Secret = endpoint.Secret;
        Agents = catalog();
        var running = Interlocked.Increment(ref _running);
        InterlockedMax(ref _mostAtOnce, running);
        _runs.Enqueue(endpoint.Address);
        Interlocked.Increment(ref _runCount);
        _started.TrySetResult();

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            // A run returns when it is cancelled; it does not fault.
            if (LetsGo is not null)
            {
                await LetsGo.Task;
            }
        }
        finally
        {
            Interlocked.Decrement(ref _running);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    public Task WaitForRunAsync(CancellationToken ct) => _started.Task.WaitAsync(ct);
}

internal sealed class FakeOwnChannelServers : IOwnChannelServers
{
    private readonly Channel<OwnServerOpening> _openings = Channel.CreateUnbounded<OwnServerOpening>();
    private string? _watched;

    public string? Watched => Volatile.Read(ref _watched);

    // An opening of the server, open until the returned source is cancelled.
    public CancellationTokenSource Open(string address, string household = "home")
    {
        var closed = new CancellationTokenSource();
        _openings.Writer.TryWrite(new OwnServerOpening(McpServerEndpoint.Configured(address), closed.Token, ChannelHousehold.Own(household, household == "home")));
        return closed;
    }

    public IAsyncEnumerable<OwnServerOpening> WatchAsync(string serverName, CancellationToken ct)
    {
        Volatile.Write(ref _watched, serverName);
        return _openings.Reader.ReadAllAsync(ct);
    }
}