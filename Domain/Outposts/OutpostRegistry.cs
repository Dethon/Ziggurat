using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Domain.Outposts;

// The hub's side of an outpost's life: it lands, it is kept alive, it is taken back, and if the
// machine cannot say anything at all it lapses. The lifetime is the store's — an entry is written
// with an expiry and refreshed by pushing that expiry out — so there is no reaper here, no sweep
// and no timer. A machine losing power looks like an entry nobody renewed.
//
// The one thing this owns beyond the store is the record of what happened, because an outpost
// leaves no other trace of having been there and "was that machine up at two o'clock" has to be
// answerable afterwards.
public sealed class OutpostRegistry(
    IOutpostStore store,
    IMetricsPublisher metrics,
    TimeProvider timeProvider) : IOutpostRegistry
{
    public async Task RegisterAsync(OutpostRegistration registration, CancellationToken ct = default)
    {
        await store.SetAsync(registration, OutpostLifetime.Expiry, ct);
        Publish(OutpostLifecycle.Registered, registration.Name, registration.Endpoint);
    }

    public async Task<bool> KeepAliveAsync(string name, CancellationToken ct = default)
    {
        var refreshed = await store.RefreshAsync(name, OutpostLifetime.Expiry, ct);
        if (refreshed)
        {
            Publish(OutpostLifecycle.Refreshed, name);
        }

        return refreshed;
    }

    public async Task<bool> DeregisterAsync(string name, CancellationToken ct = default)
    {
        var removed = await store.RemoveAsync(name, ct);
        if (removed)
        {
            Publish(OutpostLifecycle.Deregistered, name);
        }

        return removed;
    }

    public async Task<IReadOnlyList<OutpostRegistration>> ListAsync(CancellationToken ct = default)
    {
        // Reading is the only moment an expiry can be noticed: nothing else ever looks, which is
        // the point of letting the store do the expiring. The event is stamped with when the hub
        // learned it rather than with a guess at when the machine went, because the machine went
        // silently and there is nothing to guess from.
        var snapshot = await store.ReadAsync(ct);
        snapshot.Lapsed.ToList().ForEach(name => Publish(OutpostLifecycle.Expired, name));
        return snapshot.Live;
    }

    private void Publish(OutpostLifecycle lifecycle, string outpost, string? endpoint = null) =>
        metrics.Publish(new OutpostEvent
        {
            Timestamp = timeProvider.GetUtcNow(),
            Outpost = outpost,
            Lifecycle = lifecycle,
            Endpoint = endpoint
        });
}