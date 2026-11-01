using Domain.DTOs.Channel;

namespace Domain.Contracts;

// A space is a household's, so a push names the household whose space it goes to.
public interface IPushNotificationService
{
    Task SendToSpaceAsync(string tenantId, string spaceSlug, string title, string body, string url, CancellationToken ct = default);

    // One person's subscriptions in the space, for news only they may know: a private topic's.
    Task SendToPersonAsync(Account person, string spaceSlug, string title, string body, string url, CancellationToken ct = default);
}