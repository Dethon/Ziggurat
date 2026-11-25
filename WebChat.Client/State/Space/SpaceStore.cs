using Domain.DTOs.WebChat;

namespace WebChat.Client.State.Space;

public record SelectSpace(string Slug) : IAction;

public record SpaceValidated(string Slug, string Name, string AccentColor, string? DefaultAgent = null) : IAction;

public record InvalidSpace : IAction;

// Ask the hub again what the space switcher offers (it is opened, or a space was joined).
public record LoadSwitcher : IAction;

public record SwitcherLoaded(IReadOnlyList<SwitcherEntry> Entries) : IAction;

public sealed class SpaceStore : IDisposable
{
    private readonly Store<SpaceState> _store;

    // The catch-all registered here runs synchronously before SpaceEffect's async handler,
    // so effects can read up-to-date state (e.g. CurrentSlug) immediately after dispatch.
    public SpaceStore(Dispatcher dispatcher)
    {
        _store = new Store<SpaceState>(SpaceState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public SpaceState State => _store.State;
    public IObservable<SpaceState> StateObservable => _store.StateObservable;
    public void Dispose() => _store.Dispose();

    private static SpaceState Reduce(SpaceState state, IAction action) => action switch
    {
        SelectSpace a => state with { CurrentSlug = a.Slug, Named = state.Named && state.CurrentSlug == a.Slug },
        SpaceValidated a => new SpaceState
        {
            CurrentSlug = a.Slug, SpaceName = a.Name, AccentColor = a.AccentColor, DefaultAgent = a.DefaultAgent,
            Named = true, Switcher = state.Switcher
        },
        InvalidSpace => SpaceState.Initial with { Switcher = state.Switcher },
        SwitcherLoaded a => state with { Switcher = a.Entries },
        _ => state
    };
}