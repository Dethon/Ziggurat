using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;

namespace Domain.Contracts;

// A person's phones, kept under their user id, and each household's spaces' phones under the
// household's id: two households may both have a "default" space, and a push to one never reaches the
// other's phones.
public interface IPushSubscriptionStore
{
    Task SaveAsync(Account person, PushSubscriptionDto subscription, string spaceSlug = "default", string? replacingEndpoint = null, CancellationToken ct = default);
    Task RemoveAsync(string userId, string endpoint, CancellationToken ct = default);
    Task RemoveByEndpointAsync(string endpoint, CancellationToken ct = default);
    Task<IReadOnlyList<(string UserId, PushSubscriptionDto Subscription)>> GetAllAsync(CancellationToken ct = default);
    Task<IReadOnlyList<(string UserId, PushSubscriptionDto Subscription)>> GetBySpaceAsync(string tenantId, string spaceSlug, CancellationToken ct = default);
}