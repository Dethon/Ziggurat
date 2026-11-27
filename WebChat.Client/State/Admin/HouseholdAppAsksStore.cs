using Domain.DTOs.Admin;
using Domain.DTOs.Household;

namespace WebChat.Client.State.Admin;

public record LoadHouseholdAppAsks : IAction;

public record HouseholdAppAsksLoaded(AdminAppAsks AppAsks) : IAction;

// Loaded: the caps the page was showing, so another owner's change since then is kept (AdminEdit).
public record SaveHouseholdAppAsks(AppAsksSection Caps, AppAsksSection? Loaded = null) : IAction;

public record HouseholdAppAsksSaved(AdminAppAsks AppAsks, string? Warning) : IAction;

// A load or a save the admin API did not answer with the caps: refused, not allowed, unreachable.
public record HouseholdAppAsksNotSaved(AdminNotice Notice) : IAction;

public sealed record HouseholdAppAsksState
{
    // Null until the admin API has answered with them.
    public AdminAppAsks? AppAsks { get; init; }

    public bool Saving { get; init; }

    public AdminNotice? Notice { get; init; }

    public static HouseholdAppAsksState Initial => new();
}

// The household's daily caps on its apps' asks as the admin area's apps page shows them: only ever what the
// admin API answered.
public sealed class HouseholdAppAsksStore : IDisposable
{
    private readonly Store<HouseholdAppAsksState> _store;

    public HouseholdAppAsksStore(Dispatcher dispatcher)
    {
        _store = new Store<HouseholdAppAsksState>(HouseholdAppAsksState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public HouseholdAppAsksState State => _store.State;

    public IObservable<HouseholdAppAsksState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();

    private static HouseholdAppAsksState Reduce(HouseholdAppAsksState state, IAction action) => action switch
    {
        LoadHouseholdAppAsks => state with { Notice = null },
        HouseholdAppAsksLoaded a => state with { AppAsks = a.AppAsks },
        SaveHouseholdAppAsks => state with { Saving = true, Notice = null },
        HouseholdAppAsksSaved a => state with
        {
            AppAsks = a.AppAsks,
            Saving = false,
            Notice = a.Warning is { } warning
                ? new AdminNotice(AdminNoticeKind.Warning, warning)
                : new AdminNotice(AdminNoticeKind.Saved, "Saved.")
        },
        HouseholdAppAsksNotSaved a => state with { Saving = false, Notice = a.Notice },
        _ => state
    };
}