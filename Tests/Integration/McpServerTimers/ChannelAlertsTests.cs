using System.Collections.Concurrent;
using System.ComponentModel;
using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Voice;
using Domain.Monitor;
using Domain.Security;
using Domain.Security.Signing;
using Domain.Tools.Timers.Vfs;
using Infrastructure.Clients.Channels;
using Infrastructure.Timers;
using Mcp.Hosting;
using McpServerTimers.Modules;
using McpServerTimers.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Integration.McpServerTimers;

// A household with no satellites has timers all the same: one with no target rings on its owner's
// channels. The timers server holds no channel, so it raises the alert in its inbox as a fire is
// raised; the agent's connections read it, and the agent's relay offers it on the person's chat
// client and Telegram — here two channel servers that show alerts — and settles it everywhere on the
// first dismissal. The timers server is real, served over MCP; so are the agent's connections.
public sealed class ChannelAlertsTests : IAsyncLifetime
{
    private static readonly Account _fran = new("0a1b", "f1");
    private static readonly Account _ana = new("0c2d", "a1");

    // TimerFireService's own poll cadence.
    private static readonly TimeSpan _poll = TimeSpan.FromSeconds(1);

    private readonly ArmedClock _clock = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<IAsyncDisposable> _owned = [];
    private RunningServer _timers = null!;
    private AlertProbe _webchat = null!;
    private AlertProbe _telegram = null!;

    public async Task InitializeAsync()
    {
        _timers = await InMemoryMcpServer.StartAsync(services =>
        {
            services.ConfigureTimers(new TimerSettings { Mcp = McpTestSecret.Gate });
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock));
        });
        (_webchat, var webchat) = await StartChannelAsync();
        (_telegram, var telegram) = await StartChannelAsync();

        var connections = new[]
        {
            await ConnectAsync("timers", _timers.Endpoint),
            await ConnectAsync("signalr", webchat.Endpoint),
            await ConnectAsync("telegram", telegram.Endpoint)
        };
        _ = new AlertRelay(connections, new RingingAlerts(), NullLogger<AlertRelay>.Instance, TimeProvider.System).RunAsync(_stopping.Token);

        // The timers server raises a ring only for an agent polling as it fires (gate-on-live).
        await Eventually.Until(
            () => _timers.App.Services.GetRequiredService<ChannelInbox>().HasLiveSubscriber(),
            "the agent's connection to poll the timers server");
    }

    public async Task DisposeAsync()
    {
        await _stopping.CancelAsync();
        foreach (var owned in Enumerable.Reverse(_owned))
        {
            await owned.DisposeAsync();
        }

        await _timers.DisposeAsync();
    }

    [Fact]
    public async Task ATimerWithNoSatellite_RingsOnItsOwnersChannels_AndADismissalOnOneStopsItOnAll()
    {
        await ArmAsync(_fran, """{"durationSeconds": 2, "text": "pasta is ready"}""");

        await AdvanceUntilAsync(() => !_webchat.Offers.IsEmpty && !_telegram.Offers.IsEmpty, "the alert to be offered on both channels");
        var offer = _webchat.Offers.ShouldHaveSingleItem();
        offer.Owner.ShouldBe(_fran);
        offer.Text.ShouldBe("pasta is ready");
        offer.AgentId.ShouldBe("jonas");
        _telegram.Offers.ShouldHaveSingleItem().AlertId.ShouldBe(offer.AlertId);
        _webchat.Dismiss(offer.AlertId);

        await Eventually.Until(() => _telegram.CalledOff.Contains(offer.AlertId), "the Telegram offer to be called off");
        _telegram.Settles.ShouldHaveSingleItem().ShouldBe((offer.AlertId, true));
        _webchat.Settles.ShouldBeEmpty();
        // The timers server was told too, so the timer is no longer anything to silence.
        (await DismissAsync(_fran)).ShouldContain("nothing is ringing on the household's chat clients or Telegram");
    }

    // "Stop the timer" typed in any chat runs ./dismiss on the timers server, and every channel still
    // showing the alert stops.
    [Fact]
    public async Task ATimerStoppedByDismiss_StopsOnEveryChannelShowingIt()
    {
        await ArmAsync(_fran, """{"durationSeconds": 2, "text": "pasta is ready"}""");
        await AdvanceUntilAsync(() => !_webchat.Offers.IsEmpty && !_telegram.Offers.IsEmpty, "the alert to be offered on both channels");
        var alertId = _webchat.Offers.Single().AlertId;

        (await DismissAsync(_fran)).ShouldContain("timer \"pasta is ready\"");

        await Eventually.Until(
            () => _webchat.CalledOff.Contains(alertId) && _telegram.CalledOff.Contains(alertId),
            "both offers to be called off");
        _webchat.Settles.ShouldHaveSingleItem().ShouldBe((alertId, true));
        _telegram.Settles.ShouldHaveSingleItem().ShouldBe((alertId, true));
    }

    // What one household has ringing is that household's to silence.
    [Fact]
    public async Task AnotherHouseholdsDismiss_LeavesTheAlertRinging()
    {
        await ArmAsync(_fran, """{"durationSeconds": 2, "text": "pasta is ready"}""");
        await AdvanceUntilAsync(() => !_webchat.Offers.IsEmpty, "the alert to be offered");

        (await DismissAsync(_ana)).ShouldContain("nothing is ringing on the household's chat clients or Telegram");

        await Eventually.Settle();
        _webchat.Settles.ShouldBeEmpty();
        _webchat.CalledOff.ShouldBeEmpty();
    }

    // The mount as the registrar hands it a person's call: as the person the call's token names.
    private TimerFileSystem As(Account person) =>
        (TimerFileSystem)_timers.App.Services.GetRequiredService<TimerFileSystem>().For(new FileSystemCaller(
            new ConversationContext("jonas", "conv-1", person.UserId, new ReplyTarget("signalr", "conv-1"),
                Principal: new Principal("signalr", person.UserId, person),
                Identity: IdentityClaims.Person(person.UserId, person.TenantId, "signalr", [])),
            null));

    private async Task ArmAsync(Account person, string spec) =>
        (await As(person).CreateAsync("/pasta/timer.json", spec, false, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCreateResult>.Ok>();

    // What ./dismiss says. These households run no voice server, so with nothing ringing on their
    // channels the answer is that, beside the satellites it could not reach.
    private async Task<string> DismissAsync(Account person) =>
        await As(person).ExecAsync("/", "./dismiss", null, CancellationToken.None) switch
        {
            FsResult<FsExecResult>.Ok ok => ok.Value.Stdout,
            FsResult<FsExecResult>.Err err => err.Error.Message,
            var other => throw new InvalidOperationException($"unexpected {other}")
        };

    private async Task<(AlertProbe, RunningServer)> StartChannelAsync()
    {
        var probe = new AlertProbe();
        var server = await InMemoryMcpServer.StartAsync(services => services
            .AddSingleton(probe)
            .AddToolServer(new ChannelSettings())
            .AddChannelServer(DeliveryPolicy.Broadcast, noOutboundSurface: true)
            .WithTools<AlertProbeTools>());
        _owned.Add(server);
        return (probe, server);
    }

    private async Task<McpChannelConnection> ConnectAsync(string channelId, string endpoint)
    {
        var connection = new McpChannelConnection(channelId, mcpSecret: McpTestSecret.Value, callTokens: McpTestSecret.CallTokens);
        await connection.ConnectAsync(endpoint, CancellationToken.None);
        _owned.Add(connection);
        return connection;
    }

    // The fire loop's PeriodicTimer arms once and is reused, so the clock is driven an interval at a
    // time until the ring lands rather than advanced onto one wait.
    private async Task AdvanceUntilAsync(Func<bool> rung, string because)
    {
        await _clock.WaitForLiveAsync(_poll);
        await Eventually.Until(() =>
        {
            if (!rung())
            {
                _clock.Advance(_poll);
            }

            return rung();
        }, because);
    }

    private sealed record ChannelSettings : IMcpHostSettings
    {
        public McpGateSettings Mcp => McpTestSecret.Gate;
    }
}

// A channel that shows alerts, as far as the relay can tell: it records what it was offered, holds
// each offer until the test dismisses it or it is settled or called off, and records the settles.
public sealed class AlertProbe
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _showing = new();

    public ConcurrentQueue<OfferAlertParams> Offers { get; } = new();
    public ConcurrentQueue<(string AlertId, bool Dismissed)> Settles { get; } = new();
    public ConcurrentQueue<string> CalledOff { get; } = new();

    public async Task<string> OfferAsync(OfferAlertParams offer, CancellationToken ct)
    {
        var showing = _showing.GetOrAdd(offer.AlertId, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
        Offers.Enqueue(offer);
        try
        {
            return await showing.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            CalledOff.Enqueue(offer.AlertId);
            throw;
        }
    }

    public void Dismiss(string alertId) => _showing[alertId].TrySetResult(ChannelProtocol.AlertDismissed);

    public void Settle(string alertId, bool dismissed) => Settles.Enqueue((alertId, dismissed));
}

[McpServerToolType]
public sealed class AlertProbeTools
{
    [McpServerTool(Name = ChannelProtocol.OfferAlertTool)]
    [Description("Shows an alert to its person until they dismiss it.")]
    public static Task<string> OfferAlert(
        string alertId, Account owner, string text, IServiceProvider services,
        AnnounceKind kind = AnnounceKind.Timer, string? agentId = null, CancellationToken cancellationToken = default) =>
        services.GetRequiredService<AlertProbe>().OfferAsync(
            new OfferAlertParams { AlertId = alertId, Owner = owner, Text = text, Kind = kind, AgentId = agentId },
            cancellationToken);

    [McpServerTool(Name = ChannelProtocol.SettleAlertTool)]
    [Description("Stops showing an alert that ended elsewhere.")]
    public static string SettleAlert(string alertId, IServiceProvider services, bool dismissed = false)
    {
        services.GetRequiredService<AlertProbe>().Settle(alertId, dismissed);
        return "settled";
    }
}