using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

// The out-of-process "stop ringing" surface: the timers server's ./dismiss POSTs here so the hub
// cancels the live alert CancellationTokenSources (which only exist in this process).
public static class DismissEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/voice/dismiss", (HttpContext ctx, AnnounceSettings settings, ActiveAlertRegistry alerts) =>
            VoiceHubAuth.Reject(ctx, settings) ?? Results.Ok(alerts.DismissAll()));
    }
}