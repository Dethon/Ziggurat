using System.Net;
using System.Security.Cryptography;
using System.Text;
using Mcp.Hosting.Tunnel;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

// Shared gate for the voice hub's out-of-band HTTP endpoints (announce, dismiss, satellites):
// 503 when disabled, 404 when a non-loopback caller hits a loopback-only hub, 401 on token mismatch;
// the relay, presenting this hub's tunnel credential, is let through.
public static class VoiceHubAuth
{
    public static IResult? Reject(HttpContext ctx, AnnounceSettings settings)
    {
        if (!settings.Enabled)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // A tunnelled hub is reached by the deployment's timers server through the relay, which
        // presents the credential this hub's tunnel was opened with: the deployment's announce token
        // is never a household's to hold. Only while tunnelled — dialled, there is no such credential.
        if (ctx.RequestServices.GetService<RelayCredential>()?.PresentedBy(ctx.Request) == true)
        {
            return null;
        }

        // Loopback-only is enforced per-request (by remote IP), never by binding Kestrel to 127.0.0.1,
        // which would also take /mcp off the container network. The timers server is a different
        // container, so BindToLoopbackOnly must stay false or its calls would 404.
        if (settings.BindToLoopbackOnly && !IsLoopback(ctx.Connection.RemoteIpAddress))
        {
            return Results.NotFound();
        }

        var token = ctx.Request.Headers["X-Announce-Token"].FirstOrDefault();
        return TokenMatches(settings.Token, token) ? null : Results.Unauthorized();
    }

    // Constant-time comparison so a wrong token cannot be recovered byte-by-byte via response timing.
    private static bool TokenMatches(string configured, string? provided)
    {
        if (string.IsNullOrEmpty(configured) || provided is null)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(configured));
    }

    private static bool IsLoopback(IPAddress? ip) => ip is not null && IPAddress.IsLoopback(ip);
}