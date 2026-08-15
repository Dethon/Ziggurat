using Domain.DTOs;

namespace Domain.Contracts;

// The hub's side of an outpost's lifecycle: a machine announces itself, keeps announcing while it
// runs, takes the announcement back when it stops, and lapses on its own when it cannot.
public interface IOutpostRegistry
{
    Task RegisterAsync(OutpostRegistration registration, CancellationToken ct = default);

    // False where the registration had already lapsed, so the machine learns it must announce
    // itself again rather than keep pinging an entry nobody holds.
    Task<bool> KeepAliveAsync(string name, CancellationToken ct = default);

    Task<bool> DeregisterAsync(string name, CancellationToken ct = default);

    // Every outpost still live. Reading is also when a lapse is noticed, because nothing else ever
    // looks: an entry that has gone is published as an expiry here and then forgotten.
    Task<IReadOnlyList<OutpostRegistration>> ListAsync(CancellationToken ct = default);
}