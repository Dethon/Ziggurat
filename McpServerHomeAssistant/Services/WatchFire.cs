using System.Text.Json.Serialization;
using Domain.Channels;
using Domain.DTOs.Channel;
using JetBrains.Annotations;

namespace McpServerHomeAssistant.Services;

// What the home posts when a prompt effect fires: the watch's identity and delivery, rendered into
// the automation when it was written, and the firing facts Home Assistant knows at fire time.
[PublicAPI]
public sealed record WatchFiredRequest
{
    public string? WatchId { get; init; }
    public string? Name { get; init; }
    public string? AgentId { get; init; }
    public string? Prompt { get; init; }
    public IReadOnlyList<string>? DeliverTo { get; init; }
    public string? UserId { get; init; }
    public string? EntityId { get; init; }
    public string? FriendlyName { get; init; }
    public string? FromState { get; init; }
    public string? ToState { get; init; }
    public string? Description { get; init; }

    // Home Assistant's `now().isoformat()`, kept as its string: the home's zone is in it.
    [JsonPropertyName("firedAt")]
    public string? FiredAt { get; init; }
}

public static class WatchFire
{
    public const string ConversationPrefix = "watch";
    public const string Sender = "watch";

    // What the payload must carry for a fire to be a prompt in front of an agent; the rest is the
    // firing facts, and a template trigger legitimately carries none.
    public static string? Malformed(WatchFiredRequest request) =>
        string.IsNullOrWhiteSpace(request.WatchId) ? "watchId is required"
        : string.IsNullOrWhiteSpace(request.AgentId) ? "agentId is required"
        : string.IsNullOrWhiteSpace(request.Prompt) ? "prompt is required"
        : null;

    // One notification through the shared fire planning, so a watch's fire and a schedule's cannot
    // drift in how deliverTo becomes reply targets. The first line says what fired, so "look into
    // it" is enough of a prompt; the rendered prompt follows.
    public static ChannelMessageNotification Compose(WatchFiredRequest request, IReadOnlyList<string> defaultDeliverTo, DateTimeOffset now)
    {
        var name = request.Name ?? request.WatchId!;
        return FirePlanning.Compose(new Fire(
            FirePlanning.ConversationId(ConversationPrefix, request.WatchId!, now),
            Sender,
            $"{FirstLine(request, name)}\n\n{request.Prompt}",
            request.AgentId!,
            request.DeliverTo,
            request.UserId,
            new MessageOrigin(MessageOriginKind.Watch, null, WatchId: request.WatchId, Title: name),
            now), defaultDeliverTo);
    }

    private static string FirstLine(WatchFiredRequest request, string name)
    {
        var subject = request.FriendlyName is { Length: > 0 } friendly && request.EntityId is { Length: > 0 } entity
            ? $"{friendly} ({entity})"
            : request.FriendlyName ?? request.EntityId ?? request.Description ?? "the home";
        var transition = (request.FromState, request.ToState) switch
        {
            ({ Length: > 0 } from, { Length: > 0 } to) => $" went from {from} to {to}",
            (_, { Length: > 0 } to) => $" is now {to}",
            _ => " changed"
        };
        var when = request.FiredAt is { Length: > 0 } at ? $" at {at}" : "";
        return $"[watch \"{name}\" fired] {subject}{transition}{when}.";
    }
}