using Domain.DTOs;

namespace McpServerHomeAssistant.Settings;

public record McpSettings
{
    public required HomeAssistantConfiguration HomeAssistant { get; init; }

    // Optional: Music Assistant's own API, used only for the podcast-episode listing Home Assistant
    // cannot provide. Absent or tokenless, the server simply does not expose that action.
    public MusicAssistantConfiguration? MusicAssistant { get; init; }

    // The voice hub's announce secret (env Announce__Token, in the stack's .env), reused to guard
    // the watch callback: Home Assistant carries a prompt effect back through a rest_command that
    // presents it, so provisioning the home is one secret.
    public AnnounceTokenSettings Announce { get; init; } = new();

    // Where a watch with no deliverTo lands: the shared policy file's answer, the same the
    // scheduler gives a schedule (Domain/delivery.json).
    public DeliverySettings Delivery { get; init; } = new();
}

public record AnnounceTokenSettings
{
    public string Token { get; init; } = "";
}

public record HomeAssistantConfiguration
{
    public required string BaseUrl { get; init; }
    public required string Token { get; init; }
}

public record MusicAssistantConfiguration
{
    public required string BaseUrl { get; init; }
    public string Token { get; init; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Token);
}