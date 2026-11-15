using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;

namespace Infrastructure.Clients.Push;

public sealed class NullPushNotificationService : IPushNotificationService
{
    public Task SendToSpaceAsync(string tenantId, string spaceSlug, PushNotice notice, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SendToPersonAsync(Account person, string spaceSlug, PushNotice notice, CancellationToken ct = default)
        => Task.CompletedTask;
}