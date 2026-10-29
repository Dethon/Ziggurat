using Domain.Security;
using Mcp.Hosting;

namespace McpServerTimers.Settings;

public record TimerSettings : IMcpHostSettings
{
    // This server's own credential, which its /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    public VoiceHubSettings VoiceHub { get; init; } = new();

    // Reuses the voice hub's announce secret (env Announce__Token) — the timers server calls the
    // hub's token-gated announce/dismiss/satellites endpoints.
    public AnnounceTokenSettings Announce { get; init; } = new();
}

public record VoiceHubSettings
{
    public string BaseUrl { get; init; } = "http://mcp-channel-voice:8080";
}

public record AnnounceTokenSettings
{
    public string Token { get; init; } = "";
}