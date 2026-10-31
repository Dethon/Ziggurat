using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.State;
using WebChat.Client.State.Alert;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Toast;

namespace Tests.Unit.WebChat.Client.State;

// A timer ringing for this person rings on this client until it ends: dismissed here or anywhere
// else, or rung out. A client that was not open when it began catches up on what still rings.
public sealed class AlertStoreTests : IDisposable
{
    private readonly Dispatcher _dispatcher = new();
    private readonly AlertStore _store;
    private readonly ToastStore _toasts;
    private readonly ConnectionStore _connection;
    private readonly FakeAlertService _alerts = new();

    public AlertStoreTests()
    {
        _store = new AlertStore(_dispatcher);
        _toasts = new ToastStore(_dispatcher);
        _connection = new ConnectionStore(_dispatcher);
    }

    public void Dispose()
    {
        _store.Dispose();
        _toasts.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public void AlertsRing_OldestOnScreen_AndEachEndsByItsOwnId()
    {
        _dispatcher.Dispatch(new AlertRang(new AlertOfferedNotification("pasta", "pasta is ready")));
        _dispatcher.Dispatch(new AlertRang(new AlertOfferedNotification("tea", "tea is ready")));
        _dispatcher.Dispatch(new AlertRang(new AlertOfferedNotification("pasta", "pasta is ready")));

        _store.State.Current.ShouldNotBeNull().Text.ShouldBe("pasta is ready");
        _store.State.Ringing.Count.ShouldBe(2);

        _dispatcher.Dispatch(new AlertEnded("pasta"));

        _store.State.Current.ShouldNotBeNull().AlertId.ShouldBe("tea");
    }

    [Fact]
    public void CatchingUp_KeepsWhatStillRings_AndDropsWhatEndedWhileAway()
    {
        _dispatcher.Dispatch(new AlertRang(new AlertOfferedNotification("pasta", "pasta is ready")));

        _dispatcher.Dispatch(new RingingAlertsReconciled([new AlertOfferedNotification("tea", "tea is ready")]));

        _store.State.Ringing.ShouldHaveSingleItem().AlertId.ShouldBe("tea");
    }

    // Opened from the push that told the person, the client asks what still rings as it becomes
    // live, and every time after an interruption.
    [Fact]
    public async Task BecomingLive_RingsWhatTheHubStillRings()
    {
        using var effect = new RingingAlertsEffect(_connection, _alerts, _dispatcher, NullLogger<RingingAlertsEffect>.Instance);
        _alerts.Ringing.Add(new AlertOfferedNotification("pasta", "pasta is ready"));

        _dispatcher.Dispatch(new ConnectionConnected());

        await Eventually.Until(() => _store.State.Current?.AlertId == "pasta", "the alert to ring");
    }

    [Fact]
    public async Task Dismissing_TakesTheAlertOffThisScreen()
    {
        _alerts.Ringing.Add(new AlertOfferedNotification("pasta", "pasta is ready"));
        _dispatcher.Dispatch(new AlertRang(_alerts.Ringing[0]));

        await new AlertDismisser(_alerts, _dispatcher).DismissAsync("pasta");

        _alerts.Dismissed.ShouldBe(["pasta"]);
        _store.State.Current.ShouldBeNull();
    }

    // A dismiss that could not reach the hub stopped nothing anywhere, so the alert stays up and the
    // person is told.
    [Fact]
    public async Task DismissingWhileNotLive_KeepsItRinging_AndSaysSo()
    {
        _alerts.NotLive = true;
        _dispatcher.Dispatch(new AlertRang(new AlertOfferedNotification("pasta", "pasta is ready")));

        await new AlertDismisser(_alerts, _dispatcher).DismissAsync("pasta");

        _store.State.Current.ShouldNotBeNull();
        _toasts.State.Toasts.ShouldNotBeEmpty();
    }
}