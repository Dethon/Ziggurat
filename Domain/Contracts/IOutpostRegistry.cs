using Domain.DTOs;

namespace Domain.Contracts;

// The hub's side of an outpost's lifecycle: a machine announces itself, keeps announcing while it
// runs, takes the announcement back when it stops, and lapses on its own when it cannot.
//
// An interface rather than the concrete OutpostRegistry beside it because Infrastructure consumes
// it — the session build asks which machines are live — and Infrastructure cannot reference the
// host that composes them. Domain is the only place both can see.
public interface IOutpostRegistry
{
    Task RegisterAsync(OutpostRegistration registration, CancellationToken ct = default);

    // The hub's verdict on this outpost's mount, or null where the registration had already lapsed
    // — so the machine learns it must announce itself again rather than keep pinging an entry
    // nobody holds. The verdict rides the keepalive because it is the only channel back to a
    // machine, and the keepalive stays a liveness ping carrying one verdict rather than growing
    // into a reporting channel.
    Task<OutpostVerdict?> KeepAliveAsync(string name, CancellationToken ct = default);

    // What a session build learned about one outpost. Nothing else can learn it: a mount name
    // lives inside each server's filesystem resource and is only read when a session is built.
    Task RecordVerdictAsync(string name, OutpostVerdict verdict, CancellationToken ct = default);

    Task<bool> DeregisterAsync(string name, CancellationToken ct = default);

    // Every outpost still live. Reading is also when a lapse is noticed, because nothing else ever
    // looks: an entry that has gone is published as an expiry here and then forgotten.
    Task<IReadOnlyList<OutpostRegistration>> ListAsync(CancellationToken ct = default);
}