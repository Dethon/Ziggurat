using Agent.App;
using Agent.Settings;
using Domain.DTOs.Channel;
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
    private int _runCount;

    public string ChannelId { get; } = channelId;
    public int RunCount => Volatile.Read(ref _runCount);
    public string? Endpoint { get; private set; }
    public IReadOnlyList<AgentCatalogEntry>? Agents { get; private set; }

    public async Task RunAsync(
        string endpoint, Func<IReadOnlyList<AgentCatalogEntry>> catalog, CancellationToken ct)
    {
        Endpoint = endpoint;
        Agents = catalog();
        Interlocked.Increment(ref _runCount);
        _started.TrySetResult();

        try
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        catch (OperationCanceledException)
        {
            // A run returns when it is cancelled; it does not fault.
        }
    }

    public Task WaitForRunAsync(CancellationToken ct) => _started.Task.WaitAsync(ct);
}