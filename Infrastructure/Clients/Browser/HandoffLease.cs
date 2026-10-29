using System.Text.Json.Nodes;

namespace Infrastructure.Clients.Browser;

// One handoff's hold on a person's logins, from before its tab opens until it is settled. While any
// of a person's leases is unsettled their handoff is open: nothing of theirs is saved and nothing
// forgotten. The lease is the only thing that ends that, and it ends it once — the first settle
// wins, a later Complete or Abandon is a no-op — so no exit path can leave a person's saving off
// until a restart, and none can release a handoff twice. Disposing an unsettled lease abandons it.
public sealed class HandoffLease : IAsyncDisposable
{
    private readonly PersonLogins _owner;
    private int _settled;

    internal HandoffLease(PersonLogins owner, string person, string site, IReadOnlySet<CookieIdentity> before)
    {
        _owner = owner;
        Person = person;
        Site = site;
        Before = before;
    }

    public string Person { get; }

    // The registrable domain of the url the handoff was opened at: what the login is a login to.
    public string Site { get; }

    // The person's cookies as they stood when the lease was taken; the handoff's own are the ones
    // that differ at its end.
    internal IReadOnlySet<CookieIdentity> Before { get; }

    public bool IsSettled => Volatile.Read(ref _settled) == 1;

    // Done: the login is remembered, or kept only until the browser restarts.
    public Task CompleteAsync(IReadOnlyCollection<string> visitedHosts, bool remember, CancellationToken ct = default) =>
        Settle() ? _owner.CompleteAsync(this, visitedHosts, remember, ct) : Task.CompletedTask;

    // Nobody finished it — out of time, its tab lost or never opened.
    public Task AbandonAsync(IReadOnlyCollection<string> visitedHosts, CancellationToken ct = default) =>
        Settle() ? _owner.AbandonAsync(this, visitedHosts, ct) : Task.CompletedTask;

    public async ValueTask DisposeAsync() => await AbandonAsync([]);

    private bool Settle() => Interlocked.Exchange(ref _settled, 1) == 0;
}

// A cookie as a handoff's before-and-after compare it: a cookie the site set again with the same
// value is not the handoff's.
internal sealed record CookieIdentity(string Name, string Domain, string Path, string Value)
{
    public static IReadOnlySet<CookieIdentity> In(string? storageState) =>
        storageState is null || JsonNode.Parse(storageState)?["cookies"] is not JsonArray cookies
            ? new HashSet<CookieIdentity>()
            : cookies.OfType<JsonObject>().Select(c => new CookieIdentity(
                Text(c, "name"), Text(c, "domain"), Text(c, "path"), Text(c, "value"))).ToHashSet();

    private static string Text(JsonObject cookie, string property) => cookie[property]?.GetValue<string>() ?? "";
}