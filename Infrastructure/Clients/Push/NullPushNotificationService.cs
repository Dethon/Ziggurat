using Domain.Contracts;

namespace Infrastructure.Clients.Push;

public sealed class NullPushNotificationService : IPushNotificationService
{
    public Task SendToSpaceAsync(string spaceSlug, string title, string body, string url, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task SendToPersonAsync(
        string userId, string spaceSlug, string title, string body, string url, CancellationToken ct = default)
        => Task.CompletedTask;
}