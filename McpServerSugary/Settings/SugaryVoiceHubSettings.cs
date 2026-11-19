namespace McpServerSugary.Settings;

// The relay's endpoint for the household's voice server: the deployment's own address, which compose
// spells again as VOICEHUB__BASEURL.
public record SugaryVoiceHubSettings
{
    public string BaseUrl { get; init; } = "http://agent:8080/internal/relay/voice/";
}