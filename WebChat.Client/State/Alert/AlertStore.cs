using System.Collections.Immutable;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;

namespace WebChat.Client.State.Alert;

// A timer ringing for this person, which this client shows insistently until it ends.
public sealed record RingingAlert(string AlertId, string Text, AnnounceKind Kind);

public sealed record AlertState
{
    // Every alert ringing for this person, oldest first: dismissing the one on screen surfaces the next.
    public ImmutableList<RingingAlert> Ringing { get; init; } = [];

    public RingingAlert? Current => Ringing.FirstOrDefault();

    public static AlertState Initial => new();
}

public record AlertRang(AlertOfferedNotification Alert) : IAction;

// It ended — dismissed here or anywhere else it rang, or rung out.
public record AlertEnded(string AlertId) : IAction;

// What the hub says still rings for this person, which is the whole truth about it: anything else
// this client holds ended while it was away.
public record RingingAlertsReconciled(IReadOnlyList<AlertOfferedNotification> StillRinging) : IAction;

public sealed class AlertStore : IDisposable
{
    private readonly Store<AlertState> _store;

    public AlertStore(Dispatcher dispatcher)
    {
        _store = new Store<AlertState>(AlertState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public AlertState State => _store.State;

    public IObservable<AlertState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();

    private static AlertState Reduce(AlertState state, IAction action) => action switch
    {
        AlertRang rang => state with { Ringing = Ring(state.Ringing, rang.Alert) },
        AlertEnded ended => state with { Ringing = state.Ringing.RemoveAll(a => a.AlertId == ended.AlertId) },
        RingingAlertsReconciled reconciled => state with
        {
            Ringing = reconciled.StillRinging.Aggregate(
                state.Ringing.RemoveAll(a => reconciled.StillRinging.All(r => r.AlertId != a.AlertId)), Ring)
        },
        _ => state
    };

    private static ImmutableList<RingingAlert> Ring(ImmutableList<RingingAlert> ringing, AlertOfferedNotification alert) =>
        ringing.Any(a => a.AlertId == alert.AlertId)
            ? ringing
            : ringing.Add(new RingingAlert(alert.AlertId, alert.Text, alert.Kind));
}