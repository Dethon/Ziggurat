namespace Domain.DTOs.WebChat;

public record PushSubscriptionDto(string Endpoint, string P256dh, string Auth)
{
    // The signed-in session that subscribed the phone, stamped by the hub from its verified assertion
    // (never what a browser sent): a phone hears nothing once that session ends. Null for one saved
    // before sessions were kept, which is told as before until it is saved again.
    public string? SessionId { get; init; }
}