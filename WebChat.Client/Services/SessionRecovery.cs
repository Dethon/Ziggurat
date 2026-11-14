using WebChat.Client.Contracts;
using WebChat.Client.State.Space;

namespace WebChat.Client.Services;

public sealed class SessionRecovery(
    ITopicService topicService,
    IPushSubscriptionService pushSubscriptionService,
    SpaceStore spaceStore) : ISessionRecovery
{
    // A connection that comes back is a new one to the hub, which knows who it is from the proxy's
    // headers as it arrives; what it does not know is the space this page is in, or where to push.
    public Task RecoverAsync() =>
        Task.WhenAll(
            topicService.JoinSpaceAsync(spaceStore.State.CurrentSlug),
            ResubscribePushAsync());

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
}