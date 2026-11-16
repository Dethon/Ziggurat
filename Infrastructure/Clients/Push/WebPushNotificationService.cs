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
    public async Task SendToSpaceAsync(string tenantId, string spaceSlug, PushNotice notice, CancellationToken ct = default)
    {
        await SendAsync(await store.GetBySpaceAsync(tenantId, spaceSlug, ct), spaceSlug, notice, ct);
    }

    public async Task SendToPersonAsync(Account person, string spaceSlug, PushNotice notice, CancellationToken ct = default)
    {
        var subscriptions = await store.GetBySpaceAsync(person.TenantId, spaceSlug, ct);
        await SendAsync([.. subscriptions.Where(s => s.UserId == person.UserId)], spaceSlug, notice, ct);
    }

    // The conversation rides beside the space's address rather than inside it: the worker compares the
    // address with the page on screen, and a worker from before reads the push exactly as it did.
    private async Task SendAsync(
        IReadOnlyList<(string UserId, PushSubscriptionDto Subscription)> subscriptions,
        string spaceSlug, PushNotice notice, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new
        {
            title = notice.Title,
            body = notice.Body,
            url = notice.Url,
            spaceSlug,
            agentId = notice.Topic?.AgentId,
            topicId = notice.Topic?.TopicId
        });

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