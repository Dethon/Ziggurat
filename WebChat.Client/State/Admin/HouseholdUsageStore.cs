using Domain.DTOs.Admin;

namespace WebChat.Client.State.Admin;

// A month's usage asked for, yyyy-MM, or the current month when none is named.
public record LoadHouseholdUsage(string? Month) : IAction;

public record HouseholdUsageLoaded(AdminUsage Usage) : IAction;

// A read the admin API did not answer with figures: the figures could not be read, or not allowed.
public record HouseholdUsageNotLoaded(AdminNotice Notice) : IAction;

public sealed record HouseholdUsageState
{
    // Null until the admin API has answered, and again when a month could not be read: figures that
    // are not the month asked for are never left on show.
    public AdminUsage? Usage { get; init; }

    public bool Loading { get; init; }

    public AdminNotice? Notice { get; init; }

    public static HouseholdUsageState Initial => new();
}

// The household's usage as the admin area's usage page shows it: only ever what the admin API answered.
public sealed class HouseholdUsageStore : IDisposable
{
    private readonly Store<HouseholdUsageState> _store;

    public HouseholdUsageStore(Dispatcher dispatcher)
    {
        _store = new Store<HouseholdUsageState>(HouseholdUsageState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public HouseholdUsageState State => _store.State;

    public IObservable<HouseholdUsageState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();

    private static HouseholdUsageState Reduce(HouseholdUsageState state, IAction action) => action switch
    {
        LoadHouseholdUsage => state with { Loading = true, Notice = null },
        HouseholdUsageLoaded a => state with { Usage = a.Usage, Loading = false },
        HouseholdUsageNotLoaded a => state with { Usage = null, Loading = false, Notice = a.Notice },
        _ => state
    };
}