using System.Net;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Push;

public sealed class WebPushNotificationService(
    IPushSubscriptionStore store,
    IPushMessageSender pushClient,
    ILogger<WebPushNotificationService> logger) : IPushNotificationService
{
    public async Task SendToSpaceAsync(string spaceSlug, string title, string body, string url,
        CancellationToken ct = default)
    {
        await SendAsync(await store.GetBySpaceAsync(spaceSlug, ct), spaceSlug, title, body, url, ct);
    }

    public async Task SendToPersonAsync(string userId, string spaceSlug, string title, string body, string url,
        CancellationToken ct = default)
    {
        var subscriptions = await store.GetBySpaceAsync(spaceSlug, ct);
        await SendAsync([.. subscriptions.Where(s => s.UserId == userId)], spaceSlug, title, body, url, ct);
    }

    private async Task SendAsync(
        IReadOnlyList<(string UserId, PushSubscriptionDto Subscription)> subscriptions,
        string spaceSlug, string title, string body, string url, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { title, body, url, spaceSlug });

        foreach (var (_, subscription) in subscriptions)
        {
            try
            {
                await pushClient.SendAsync(subscription.Endpoint, subscription.P256dh, subscription.Auth, payload, ct);
            }
            catch (WebPushSendException ex) when (ex.StatusCode == HttpStatusCode.Gone)
            {
                await store.RemoveByEndpointAsync(subscription.Endpoint, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to send push notification to {Endpoint}", subscription.Endpoint);
            }
        }
    }
}