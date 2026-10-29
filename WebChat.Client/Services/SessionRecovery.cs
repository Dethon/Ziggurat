using WebChat.Client.Contracts;
using WebChat.Client.State.Space;
using WebChat.Client.State.UserIdentity;

namespace WebChat.Client.Services;

public sealed class SessionRecovery(
    IChatSessionService sessionService,
    ITopicService topicService,
    IPushSubscriptionService pushSubscriptionService,
    UserIdentityStore userIdentityStore,
    SpaceStore spaceStore) : ISessionRecovery
{
    // A reconnected connection is a new one to the hub, which refuses every call from a connection
    // that has not registered, so the user is back before anything else is asked of it.
    public async Task RecoverAsync()
    {
        await RegisterUserAsync();

        await Task.WhenAll(
            topicService.JoinSpaceAsync(spaceStore.State.CurrentSlug),
            ResubscribePushAsync());
    }

    // Re-sends the existing push subscription without force-refreshing the push channel.
    // A full resubscribe would generate a new endpoint in Chrome and lose the space
    // memberships attached to the old one.
    private async Task ResubscribePushAsync()
    {
        try
        {
            await pushSubscriptionService.ResubscribeAsync();
        }
        catch
        {
            // Best-effort — a browser that has dropped its subscription must not fail recovery
        }
    }

    private Task RegisterUserAsync()
    {
        var userId = userIdentityStore.State.SelectedUserId;
        return string.IsNullOrEmpty(userId) ? Task.CompletedTask : sessionService.RegisterUserAsync(userId);
    }
}