using Domain.DTOs.Household;

namespace WebChat.Client.State.Admin;

public record LoadHouseholdSettings : IAction;

public record HouseholdSettingsLoaded(HouseholdSettings Settings) : IAction;

// Loaded: the settings the page was showing, so another owner's change since then is kept (AdminEdit).
public record SaveHouseholdSettings(HouseholdSettings Settings, HouseholdSettings? Loaded = null) : IAction;

public record HouseholdSettingsSaved(HouseholdSettings Settings, string? Warning) : IAction;

// A load or a save the admin API did not answer with the settings: refused, not allowed, unreachable.
public record HouseholdSettingsNotSaved(AdminNotice Notice) : IAction;

public sealed record HouseholdSettingsState
{
    // Null until the admin API has answered with them.
    public HouseholdSettings? Settings { get; init; }

    public bool Saving { get; init; }

    public AdminNotice? Notice { get; init; }

    public static HouseholdSettingsState Initial => new();
}

// The household's settings as the admin area's settings page shows them. What is shown is only ever
// what the admin API answered: a save shows as saved once the API says so, never before.
public sealed class HouseholdSettingsStore : IDisposable
{
    private readonly Store<HouseholdSettingsState> _store;

    public HouseholdSettingsStore(Dispatcher dispatcher)
    {
        _store = new Store<HouseholdSettingsState>(HouseholdSettingsState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public HouseholdSettingsState State => _store.State;

    public IObservable<HouseholdSettingsState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();

    private static HouseholdSettingsState Reduce(HouseholdSettingsState state, IAction action) => action switch
    {
        LoadHouseholdSettings => state with { Notice = null },
        HouseholdSettingsLoaded a => state with { Settings = a.Settings },
        SaveHouseholdSettings => state with { Saving = true, Notice = null },
        HouseholdSettingsSaved a => state with
        {
            Settings = a.Settings,
            Saving = false,
            Notice = a.Warning is { } warning
                ? new AdminNotice(AdminNoticeKind.Warning, warning)
                : new AdminNotice(AdminNoticeKind.Saved, "Saved.")
        },
        HouseholdSettingsNotSaved a => state with { Saving = false, Notice = a.Notice },
        _ => state
    };
}