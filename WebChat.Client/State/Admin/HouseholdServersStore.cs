using Domain.DTOs.Admin;

namespace WebChat.Client.State.Admin;

public record LoadHouseholdServers : IAction;

public record HouseholdServersLoaded(AdminServers Servers) : IAction;

public record AddHouseholdServer(ServerAddition Addition) : IAction;

public record RevokeServerCredential(string CredentialId) : IAction;

// A revocation the admin API answered with the household's servers as they are now.
public record HouseholdServersSaved(AdminServers Servers, string? Warning) : IAction;

// A credential the admin API minted: shown until the next change or the page is left, never again.
public record ServerCredentialMinted(MintedServerCredential Credential) : IAction;

// A load or a change the admin API did not answer: refused, not allowed, unreachable.
public record HouseholdServersNotSaved(AdminNotice Notice) : IAction;

public sealed record HouseholdServersState
{
    // Null until the admin API has answered with them.
    public AdminServers? Servers { get; init; }

    public MintedServerCredential? Minted { get; init; }

    public bool Saving { get; init; }

    public AdminNotice? Notice { get; init; }

    public static HouseholdServersState Initial => new();
}

// The household's own servers as the admin area's servers page shows them: only ever what the admin
// API answered. A minted credential is kept here alone — in memory, for this page — since the API
// never shows it again; a later change clears it, and the reload that follows a mint keeps it.
public sealed class HouseholdServersStore : IDisposable
{
    private readonly Store<HouseholdServersState> _store;

    public HouseholdServersStore(Dispatcher dispatcher)
    {
        _store = new Store<HouseholdServersState>(HouseholdServersState.Initial);

        dispatcher.RegisterCatchAll(action => _store.Dispatch(action, Reduce));
    }

    public HouseholdServersState State => _store.State;

    public IObservable<HouseholdServersState> StateObservable => _store.StateObservable;

    public void Dispose() => _store.Dispose();

    private static HouseholdServersState Reduce(HouseholdServersState state, IAction action) => action switch
    {
        LoadHouseholdServers => state with { Notice = null },
        HouseholdServersLoaded a => state with { Servers = a.Servers },
        AddHouseholdServer or RevokeServerCredential => state with { Saving = true, Notice = null, Minted = null },
        HouseholdServersSaved a => state with
        {
            Servers = a.Servers,
            Saving = false,
            Notice = a.Warning is { } warning
                ? new AdminNotice(AdminNoticeKind.Warning, warning)
                : new AdminNotice(AdminNoticeKind.Saved, "Revoked.")
        },
        ServerCredentialMinted a => state with { Saving = false, Minted = a.Credential },
        HouseholdServersNotSaved a => state with { Saving = false, Notice = a.Notice },
        _ => state
    };
}