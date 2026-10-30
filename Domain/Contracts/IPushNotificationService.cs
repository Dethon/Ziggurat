namespace Domain.Contracts;

public interface IPushNotificationService
{
    Task SendToSpaceAsync(string spaceSlug, string title, string body, string url, CancellationToken ct = default);

    // One person's subscriptions in the space, for news only they may know: a private topic's.
    Task SendToPersonAsync(string userId, string spaceSlug, string title, string body, string url, CancellationToken ct = default);
}