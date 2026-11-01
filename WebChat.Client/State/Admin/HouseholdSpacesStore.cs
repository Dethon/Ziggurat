using Domain.DTOs.Admin;
using Domain.DTOs.Household;

namespace WebChat.Client.State.Admin;

public record LoadHouseholdSpaces : IAction;

public record HouseholdSpacesLoaded(AdminSpaces Spaces) : IAction;

public record SaveHouseholdSpaces(SpacesSection Spaces) : IAction;

public record HouseholdSpacesSaved(AdminSpaces Spaces, string? Warning) : IAction;

// A load or a save the admin API did not answer with the spaces: refused, not allowed, unreachable.
public record HouseholdSpacesNotSaved(AdminNotice Notice) : IAction;

public sealed record HouseholdSpacesState
{
    // Null until the admin API has answered with them.
    public AdminSpaces? Spaces { get; init; }

    public bool Saving { get; init; }

    public AdminNotice? Notice { get; init; }

    public static HouseholdSpacesState Initial => new();
}

// The household's spaces as the admin area's spaces page shows them: only ever what the admin API
// answered, so a save shows as saved once the API says so.
public sealed class HouseholdSpacesStore : IDisposable
{
    private readonly Store<HouseholdSpacesState> _store;

    public HouseholdSpacesStore(Dispatcher dispatcher)
    {
        _store = new Store<HouseholdSpacesState>(HouseholdSpacesState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public HouseholdSpacesState State => _store.State;

    public IObservable<HouseholdSpacesState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();

    private static HouseholdSpacesState Reduce(HouseholdSpacesState state, IAction action) => action switch
    {
        LoadHouseholdSpaces => state with { Notice = null },
        HouseholdSpacesLoaded a => state with { Spaces = a.Spaces },
        SaveHouseholdSpaces => state with { Saving = true, Notice = null },
        HouseholdSpacesSaved a => state with
        {
            Spaces = a.Spaces,
            Saving = false,
            Notice = a.Warning is { } warning
                ? new AdminNotice(AdminNoticeKind.Warning, warning)
                : new AdminNotice(AdminNoticeKind.Saved, "Saved.")
        },
        HouseholdSpacesNotSaved a => state with { Saving = false, Notice = a.Notice },
        _ => state
    };
}