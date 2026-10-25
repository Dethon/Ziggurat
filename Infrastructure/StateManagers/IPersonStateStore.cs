namespace Infrastructure.StateManagers;

// Where a person's remembered logins rest between restarts: their browser context's storage state
// and the sites they handed off, encrypted at rest. No model tool reads or writes it.
public interface IPersonStateStore
{
    // Whether a key is configured, so a record can be written. Without one remembering is off,
    // and every login lives only until the browser restarts.
    bool CanWrite { get; }

    // Null when the person has nothing remembered. A record this store cannot open — no key, or
    // the wrong one — throws PersonStateUnreadableException rather than answering null: a person
    // silently logged out of everything is the failure this exists to refuse.
    Task<PersonState?> LoadAsync(string person, CancellationToken ct = default);

    Task SaveAsync(string person, PersonState state, CancellationToken ct = default);
}

// StorageState is Playwright's own JSON, cookies, localStorage and IndexedDB, already without the
// person's live-only sites. Sites are the remembered ones only: a live-only login dies with the
// browser, so it is listed in memory beside the context that holds it and never written here.
public record PersonState(string StorageState, IReadOnlyList<RememberedSite> Sites);

public record RememberedSite(string Domain, DateTimeOffset HandedOffAt, DateTimeOffset? LastUsedAt);
