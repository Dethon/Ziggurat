namespace Infrastructure.Agents.ChatClients;

// Where the Lemonade chat host is, and nothing else: an address that is configuration, and a key
// only a box that checks one needs. An empty address is the feature switched off — no discovery,
// no probe, no warning — which is what a deployment without a local box gets.
public sealed record LemonadeChatHostOptions
{
    public required string ApiUrl { get; init; }
    public string? ApiKey { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiUrl);

    // A base address that does not end in a slash loses its last segment when a relative path is
    // resolved against it, so "http://host/api/v1" would post to "/responses".
    public Uri BaseAddress => new(ApiUrl.TrimEnd('/') + "/");

    // The address as an operator wrote it, for a log line or an error naming the host.
    public string Address => ApiUrl.TrimEnd('/');
}