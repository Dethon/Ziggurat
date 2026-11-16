using Domain.DTOs.Household;

namespace WebChat.Client.State.Admin;

public record LoadHouseholdFeatures : IAction;

public record HouseholdFeaturesLoaded(FeaturesSection Features) : IAction;

// Loaded: the features the page was showing, so another owner's change since then is kept (AdminEdit).
public record SaveHouseholdFeatures(FeaturesSection Features, FeaturesSection? Loaded = null) : IAction;

public record HouseholdFeaturesSaved(FeaturesSection Features, string? Warning) : IAction;

// A load or a save the admin API did not answer with the features: refused, not allowed, unreachable.
public record HouseholdFeaturesNotSaved(AdminNotice Notice) : IAction;

public sealed record HouseholdFeaturesState
{
    // Null until the admin API has answered with them.
    public FeaturesSection? Features { get; init; }

    public bool Saving { get; init; }

    public AdminNotice? Notice { get; init; }

    public static HouseholdFeaturesState Initial => new();
}

// The household's features as the admin area's features page shows them: only ever what the admin
// API answered.
public sealed class HouseholdFeaturesStore : IDisposable
{
    private readonly Store<HouseholdFeaturesState> _store;

    public HouseholdFeaturesStore(Dispatcher dispatcher)
    {
        _store = new Store<HouseholdFeaturesState>(HouseholdFeaturesState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public HouseholdFeaturesState State => _store.State;

    public IObservable<HouseholdFeaturesState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();

    private static HouseholdFeaturesState Reduce(HouseholdFeaturesState state, IAction action) => action switch
    {
        LoadHouseholdFeatures => state with { Notice = null },
        HouseholdFeaturesLoaded a => state with { Features = a.Features },
        SaveHouseholdFeatures => state with { Saving = true, Notice = null },
        HouseholdFeaturesSaved a => state with
        {
            Features = a.Features,
            Saving = false,
            Notice = a.Warning is { } warning
                ? new AdminNotice(AdminNoticeKind.Warning, warning)
                : new AdminNotice(AdminNoticeKind.Saved, "Saved.")
        },
        HouseholdFeaturesNotSaved a => state with { Saving = false, Notice = a.Notice },
        _ => state
    };
}