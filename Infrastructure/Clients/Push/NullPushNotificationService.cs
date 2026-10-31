using Domain.Contracts;
using Domain.DTOs.Channel;

namespace Infrastructure.Clients.Push;

public sealed class NullPushNotificationService : IPushNotificationService
{
    public Task SendToSpaceAsync(
        string tenantId, string spaceSlug, string title, string body, string url, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SendToPersonAsync(
        Account person, string spaceSlug, string title, string body, string url, CancellationToken ct = default)
        => Task.CompletedTask;
}