using WebChat.Client.Contracts;
using WebChat.Client.State;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Space;
using WebChat.Client.State.UserIdentity;

namespace WebChat.Client.Services;

public sealed class SessionRecovery(
    IChatSessionService sessionService,
    ITopicService topicService,
    IPushSubscriptionService pushSubscriptionService,
    UserIdentityStore userIdentityStore,
    SpaceStore spaceStore,
    ConnectionStore connectionStore,
    Dispatcher dispatcher) : ISessionRecovery
{
    // A reconnected connection is a new one to the hub, which refuses every call from a connection
    // that has not registered, so the user is back before anything else is asked of it.
    //
    // Catch-up waits on ConnectionRegistered rather than on the connection, so it is said only
    // for a registration the hub answered, and for the epoch it was asked on: one answered after
    // the connection moved on says nothing about the connection there is now.
    public async Task RecoverAsync()
    {
        var epoch = connectionStore.State.Epoch;
        if (await RegisterUserAsync())
        {
            dispatcher.Dispatch(new ConnectionRegistered(epoch));
        }

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

    private async Task<bool> RegisterUserAsync()
    {
        var userId = userIdentityStore.State.SelectedUserId;
        return !string.IsNullOrEmpty(userId) && (await sessionService.RegisterUserAsync(userId)).IsLive;
    }
}