using System.Net;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Push;

public sealed class WebPushNotificationService(
    IPushSubscriptionStore store,
    IPushMessageSender pushClient,
    ILogger<WebPushNotificationService> logger,
    ISessionAccounts? sessions = null) : IPushNotificationService
{
    public async Task SendToSpaceAsync(string tenantId, string spaceSlug, string title, string body, string url,
        CancellationToken ct = default)
    {
        await SendAsync(await store.GetBySpaceAsync(tenantId, spaceSlug, ct), spaceSlug, title, body, url, ct);
    }

    public async Task SendToPersonAsync(Account person, string spaceSlug, string title, string body, string url,
        CancellationToken ct = default)
    {
        var subscriptions = await store.GetBySpaceAsync(person.TenantId, spaceSlug, ct);
        await SendAsync([.. subscriptions.Where(s => s.UserId == person.UserId)], spaceSlug, title, body, url, ct);
    }

    private async Task SendAsync(
        IReadOnlyList<(string UserId, PushSubscriptionDto Subscription)> subscriptions,
        string spaceSlug, string title, string body, string url, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { title, body, url, spaceSlug });

        foreach (var (userId, subscription) in subscriptions)
        {
            if (!await SessionLivesAsync(userId, subscription, ct))
            {
                continue;
            }

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

    // A phone hears only while the session that subscribed it lives: one signed out from the account
    // page, or whose person was disabled, is dropped; one that could not be asked about just now is
    // told nothing this time and kept.
    private async Task<bool> SessionLivesAsync(string userId, PushSubscriptionDto subscription, CancellationToken ct)
    {
        if (sessions is null || subscription.SessionId is not { } session)
        {
            return true;
        }

        try
        {
            if (await sessions.SessionAsync(userId, session, ct) is not null)
            {
                return true;
            }

            await store.RemoveByEndpointAsync(subscription.Endpoint, ct);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not ask whether the session behind {Endpoint} lives; not told", subscription.Endpoint);
            return false;
        }
    }
}