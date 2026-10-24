namespace Domain.DTOs;

// The deployment's VAPID keys (WEBPUSH__*): WebChat pushes a reply with them and Sugary a pen
// reminder, each to subscriptions of its own origin.
public record WebPushConfig
{
    public string? PublicKey { get; init; }
    public string? PrivateKey { get; init; }
    public string? Subject { get; init; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(PublicKey)
        && !string.IsNullOrWhiteSpace(PrivateKey)
        && !string.IsNullOrWhiteSpace(Subject);
}