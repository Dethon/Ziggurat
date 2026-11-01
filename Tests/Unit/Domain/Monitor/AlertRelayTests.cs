using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Monitor;
using Infrastructure.Timers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

// A timer with no satellite rings on its person's channels: the agent offers what the timers server
// raised on every other channel, and the first dismissal — or the timers server stopping it —
// settles it everywhere, as an approval settles.
public class AlertRelayTests
{
    private static readonly Account _fran = new("home", "fran");

    private readonly ArmedClock _clock = new();

    private static ChannelAlertNotification Pasta() => new()
    {
        AlertId = Guid.NewGuid().ToString("N"),
        Owner = _fran,
        Text = "pasta is ready",
        AgentId = "jonas"
    };

    private (Task Running, CancellationTokenSource Stop) Run(params AlertChannel[] channels)
    {
        var stop = new CancellationTokenSource();
        var running = new AlertRelay(channels, new RingingAlerts(), NullLogger<AlertRelay>.Instance, _clock).RunAsync(stop.Token);
        return (running, stop);
    }

    [Fact]
    public async Task ARaisedAlert_IsOfferedOnEveryOtherChannel_AndTheFirstDismissalSettlesTheRest()
    {
        var timers = new AlertChannel("timers");
        var webchat = new AlertChannel("signalr");
        var telegram = new AlertChannel("telegram");
        var (_, stop) = Run(timers, webchat, telegram);
        var pasta = Pasta();

        timers.Raise(new AlertSignal.Ring(pasta));
        await Eventually.Until(() => webchat.Offered.Count == 1 && telegram.Offered.Count == 1, "the offers to go out");
        webchat.Dismiss();

        await Eventually.Until(() => telegram.Settled.Count == 1 && timers.Settled.Count == 1, "the others to be settled");
        telegram.Settled.ShouldHaveSingleItem().ShouldBe((pasta.AlertId, true));
        timers.Settled.ShouldHaveSingleItem().ShouldBe((pasta.AlertId, true));
        await Eventually.Until(() => telegram.CalledOff, "the Telegram offer to stop waiting");
        webchat.Settled.ShouldBeEmpty();
        timers.Offered.ShouldBeEmpty();
        webchat.Offered.ShouldHaveSingleItem().ShouldBe(pasta);
        await stop.CancelAsync();
    }

    // ./dismiss on the timers server stops what it raised, and every channel still showing it stops.
    [Fact]
    public async Task AnAlertStoppedWhereItWasRaised_IsSettledOnEveryChannelShowingIt()
    {
        var timers = new AlertChannel("timers");
        var webchat = new AlertChannel("signalr");
        var telegram = new AlertChannel("telegram");
        var (_, stop) = Run(timers, webchat, telegram);
        var pasta = Pasta();

        timers.Raise(new AlertSignal.Ring(pasta));
        await Eventually.Until(() => webchat.Offered.Count == 1 && telegram.Offered.Count == 1, "the offers to go out");
        timers.Raise(new AlertSignal.Stop(pasta.AlertId));

        await Eventually.Until(() => webchat.Settled.Count == 1 && telegram.Settled.Count == 1, "the channels to be settled");
        webchat.Settled.ShouldHaveSingleItem().ShouldBe((pasta.AlertId, true));
        await Eventually.Until(() => webchat.CalledOff && telegram.CalledOff, "both offers to stop waiting");
        await Eventually.Settle();
        timers.Settled.ShouldBeEmpty();
        await stop.CancelAsync();
    }

    // A timer rings as long as a satellite would ring it and no longer: an alert nobody dismissed
    // rings out, and everything holding it — the timers server included — is told it stopped.
    [Fact]
    public async Task AnAlertNobodyDismisses_RingsOutAfterTheWindow()
    {
        var timers = new AlertChannel("timers");
        var webchat = new AlertChannel("signalr");
        var (_, stop) = Run(timers, webchat);
        var pasta = Pasta();

        timers.Raise(new AlertSignal.Ring(pasta));
        await Eventually.Until(() => webchat.Offered.Count == 1, "the offer to go out");
        await _clock.AdvancePastAsync(AlertRelay.RingWindow);

        await Eventually.Until(() => webchat.Settled.Count == 1 && timers.Settled.Count == 1, "everything to be settled");
        webchat.Settled.ShouldHaveSingleItem().ShouldBe((pasta.AlertId, false));
        timers.Settled.ShouldHaveSingleItem().ShouldBe((pasta.AlertId, false));
        await Eventually.Until(() => webchat.CalledOff, "the offer to stop waiting");
        await stop.CancelAsync();
    }

    // Nothing could show it — no chat client open and no push, no Telegram linked — so nothing is
    // ringing, and the timers server is told at once rather than holding it for the window.
    [Fact]
    public async Task AnAlertNoChannelCouldShow_IsSettledAtItsOriginAtOnce()
    {
        var timers = new AlertChannel("timers");
        var webchat = new AlertChannel("signalr");
        var telegram = new AlertChannel("telegram");
        var (_, stop) = Run(timers, webchat, telegram);
        var pasta = Pasta();

        timers.Raise(new AlertSignal.Ring(pasta));
        await Eventually.Until(() => webchat.Offered.Count == 1 && telegram.Offered.Count == 1, "the offers to go out");
        webchat.NotShown();
        telegram.NotShown();

        await Eventually.Until(() => timers.Settled.Count == 1, "the timers server to be told");
        timers.Settled.ShouldHaveSingleItem().ShouldBe((pasta.AlertId, false));
        await stop.CancelAsync();
    }

    // A channel whose offer failed has not dismissed anything; the alert is still the others' to end.
    [Fact]
    public async Task AChannelWhoseOfferFails_LeavesTheAlertToTheOthers()
    {
        var timers = new AlertChannel("timers");
        var webchat = new AlertChannel("signalr") { OfferFailure = new InvalidOperationException("hub down") };
        var telegram = new AlertChannel("telegram");
        var (_, stop) = Run(timers, webchat, telegram);
        var pasta = Pasta();

        timers.Raise(new AlertSignal.Ring(pasta));
        await Eventually.Until(() => telegram.Offered.Count == 1, "the Telegram offer to go out");
        await Eventually.Settle();
        timers.Settled.ShouldBeEmpty();
        telegram.Dismiss();

        await Eventually.Until(() => timers.Settled.Count == 1, "the timers server to be told");
        timers.Settled.ShouldHaveSingleItem().ShouldBe((pasta.AlertId, true));
        await stop.CancelAsync();
    }
}

// A channel whose part the test plays: what its server raises, and what its person does with an
// offer. Records what it was offered and told.
internal sealed class AlertChannel(string channelId) : IAlertChannel
{
    private readonly System.Threading.Channels.Channel<AlertSignal> _raised =
        System.Threading.Channels.Channel.CreateUnbounded<AlertSignal>();
    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private readonly List<ChannelAlertNotification> _offered = [];
    private readonly List<(string, bool)> _settled = [];
    private volatile bool _calledOff;

    public Exception? OfferFailure { get; init; }

    public string ChannelId => channelId;

    public IAsyncEnumerable<AlertSignal> Alerts => _raised.Reader.ReadAllAsync();

    public IReadOnlyList<ChannelAlertNotification> Offered => Snapshot(_offered);

    public IReadOnlyList<(string AlertId, bool Dismissed)> Settled => Snapshot(_settled);

    public bool CalledOff => _calledOff;

    public void Raise(AlertSignal signal) => _raised.Writer.TryWrite(signal);

    public void Dismiss() => _answer.TrySetResult(true);

    public void NotShown() => _answer.TrySetResult(false);

    public async Task<bool> OfferAlertAsync(ChannelAlertNotification alert, CancellationToken ct)
    {
        lock (_gate)
        {
            _offered.Add(alert);
        }

        if (OfferFailure is not null)
        {
            throw OfferFailure;
        }

        try
        {
            return await _answer.Task.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _calledOff = true;
            throw;
        }
    }

    public Task SettleAlertAsync(string alertId, bool dismissed, CancellationToken ct)
    {
        lock (_gate)
        {
            _settled.Add((alertId, dismissed));
        }

        return Task.CompletedTask;
    }

    private IReadOnlyList<T> Snapshot<T>(List<T> list)
    {
        lock (_gate)
        {
            return [.. list];
        }
    }
}