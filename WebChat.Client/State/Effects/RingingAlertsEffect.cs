using System.Reactive.Linq;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.State.Alert;
using WebChat.Client.State.Connection;

namespace WebChat.Client.State.Effects;

// A timer ringing for this person rings on the chat clients open when it began; one opened
// afterwards — from the push that told them, above all — or one that was between connections asks
// the hub what still rings, every time it becomes live, the first time too.
public sealed class RingingAlertsEffect : IDisposable
{
    private readonly IDisposable _subscription;

    public RingingAlertsEffect(
        ConnectionStore connectionStore,
        IAlertService alerts,
        Dispatcher dispatcher,
        ILogger<RingingAlertsEffect> logger)
    {
        _subscription = connectionStore.StateObservable
            .Where(state => state.Status == ConnectionStatus.Connected)
            .Select(state => state.Epoch)
            .DistinctUntilChanged()
            .Subscribe(_ => CatchUpAsync(alerts, dispatcher).LogFaults(logger, "ringing alerts"));
    }

    private static async Task CatchUpAsync(IAlertService alerts, Dispatcher dispatcher)
    {
        var ringing = await alerts.GetOfferedAlertsAsync();
        if (ringing.IsLive)
        {
            dispatcher.Dispatch(new RingingAlertsReconciled(ringing.Value ?? []));
        }
    }

    public void Dispose() => _subscription.Dispose();
}