using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.DTOs.Channel;
using Domain.Tools.HomeAssistant.Vfs;
using Mcp.Hosting;
using McpServerHomeAssistant.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace McpServerHomeAssistant.Services;

// Where a prompt effect comes back into the stack: Home Assistant's rest_command posts here when a
// watch fires, and the fire becomes a prompt in front of the agent that created the watch. Answered
// the way the automation's trace can read: 202 taken, 401 the token, 400 the payload, 503 nobody is
// connected — a lost fire is visible in the home rather than silent here. Nothing is retried or
// buffered on this side.
public static class WatchFiredEndpoint
{
    public const string Path = "/api/homeassistant/watch-fired";
    public const string TokenHeader = "X-Announce-Token";

    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app)
    {
        app.MapPost(Path, async (
            HttpContext ctx,
            McpSettings settings,
            ChannelNotificationEmitter emitter,
            HaWatches watches,
            TimeProvider time,
            ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            if (!TokenMatches(settings.Announce.Token, ctx.Request.Headers[TokenHeader].FirstOrDefault()))
            {
                return Results.Unauthorized();
            }

            WatchFiredRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync<WatchFiredRequest>(ctx.Request.Body, _json, ct);
            }
            catch (JsonException ex)
            {
                return Results.BadRequest(new { error = $"The payload is not the watch-fired JSON: {ex.Message}" });
            }

            if (request is null)
            {
                return Results.BadRequest(new { error = "The payload is empty." });
            }
            if (WatchFire.Malformed(request) is { } malformed)
            {
                return Results.BadRequest(new { error = malformed });
            }

            var logger = loggers.CreateLogger(typeof(WatchFiredEndpoint));
            var watch = await StoredAsync(watches, request.WatchId!, logger, ct);
            var written = watch?.Meta.Effects.OfType<HaPromptEffect>().ElementAtOrDefault(request.PromptIndex ?? -1)?.Prompt;
            var payload = WatchFire.Compose(request, settings.Delivery.DefaultDeliverTo, time.GetUtcNow(), written,
                watch is { Meta.Delegation: { } token } ? new FireDelegation(token, WatchDelegation.Of(watch).Description) : null);
            var receipt = await emitter.EmitWithReceiptAsync(payload, ct);
            if (!receipt.Accepted)
            {
                logger.LogWarning("Watch {WatchId} fired for agent {AgentId} and no agent is connected; the fire is lost",
                    request.WatchId, request.AgentId);
                return Results.Json(
                    new { error = $"No agent is connected to the Home Assistant channel; the fire of watch '{request.WatchId}' was not delivered." },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            logger.LogInformation("Watch {WatchId} fired for agent {AgentId} ({Delivery})",
                request.WatchId, request.AgentId, receipt.Live ? "delivered" : "buffered for a reconnecting agent");
            return Results.Accepted(value: new { conversationId = payload.ConversationId, delivered = receipt.Live });
        });
    }

    // The watch as the home keeps it at fire time — its own prompts, its delegation and what it does
    // now — rather than anything the home posted: the payload is whatever the automation renders, and
    // anything holding the announce token can post one. A watch the home cannot be asked about fires
    // for nobody rather than not at all.
    private static async Task<HaWatch?> StoredAsync(HaWatches watches, string watchId, ILogger logger, CancellationToken ct)
    {
        try
        {
            return await watches.GetAsync(watchId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Watch {WatchId} fired and the home could not be asked for its delegation; it fires for nobody", watchId);
            return null;
        }
    }

    // Constant-time, and an unset token refuses everyone rather than admitting anyone.
    private static bool TokenMatches(string configured, string? provided) =>
        !string.IsNullOrEmpty(configured) && provided is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(configured));
}