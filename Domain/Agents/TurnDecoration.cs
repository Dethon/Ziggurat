using Domain.Extensions;
using Domain.Memory;
using Microsoft.Extensions.AI;

namespace Domain.Agents;

// Everything prepended to a user turn on its way to the model that the user did not type: who
// sent it, from where, through which satellite, the local time, the alert they just dismissed,
// and the recall block. All of it lives here rather than in whichever client sends the turn —
// see docs/adr/0009-user-turn-decoration-lives-in-one-domain-function.md.
//
// A client decides when a turn is decorated and never what the decoration says. It has to be a
// copy: decoration must never reach the persisted message, which the memory extractor reads
// back as the user's own words.
public static class TurnDecoration
{
    // The time zone is an argument rather than an injected clock, which is what keeps this a
    // plain function with no ambient state.
    public static ChatMessage Apply(ChatMessage message, TimeZoneInfo localTimeZone)
    {
        var decorated = message.Clone();
        if (decorated.Role != ChatRole.User)
        {
            return decorated;
        }

        // Prepended in reverse of how the model reads them: the recall block comes out first,
        // then the sender and timestamp prefix, then what the user typed.
        var prefix = BuildPrefix(decorated, localTimeZone);
        if (prefix is not null)
        {
            decorated.Contents = decorated.Contents.Prepend(new TextContent(prefix)).ToList();
        }

        if (decorated.GetMemoryContext() is { } memoryContext)
        {
            decorated.Contents = decorated.Contents
                .Prepend(new TextContent(RecallBlock.Render(memoryContext)))
                .ToList();
        }

        return decorated;
    }

    private static string? BuildPrefix(ChatMessage message, TimeZoneInfo localTimeZone)
    {
        var sender = message.GetSenderId();
        var timestamp = message.GetTimestamp();
        var dismissedAlert = message.GetDismissedAlert();
        var offline = message.GetOfflineServers();
        if (sender is null && timestamp is null && dismissedAlert is null && offline is null)
        {
            return null;
        }

        var localTimestamp = timestamp is { } ts
            ? TimeZoneInfo.ConvertTime(ts, localTimeZone)
            : (DateTimeOffset?)null;

        var senderSegment = BuildSenderSegment(message, sender);
        var prefix = (senderSegment, timestamp) switch
        {
            (not null, not null) => $"[Current time: {localTimestamp:yyyy-MM-dd HH:mm:ss zzz}] {senderSegment}:\n",
            (not null, null) => $"{senderSegment}:\n",
            (null, not null) => $"[Current time: {localTimestamp:yyyy-MM-dd HH:mm:ss zzz}]:\n",
            _ => ""
        };

        var alerted = string.IsNullOrWhiteSpace(dismissedAlert)
            ? prefix
            : $"[The user just dismissed the {dismissedAlert}]\n{prefix}";
        return offline is { Count: > 0 } ? $"{OfflineLine(offline)}\n{alerted}" : alerted;
    }

    // One line, so the model can say which of the household's machines is off instead of looking for
    // tools it was never given or answering as if they had come back empty.
    public static string OfflineLine(IReadOnlyList<string> serverNames) =>
        $"[Offline household servers: {string.Join(", ", serverNames)}. Their tools are missing this turn; " +
        "tell the user if the request needs them]";

    // The room and the satellite qualify the sender, so neither is rendered without one.
    private static string? BuildSenderSegment(ChatMessage message, string? sender)
    {
        if (sender is null)
        {
            return null;
        }

        var location = message.GetLocation();
        var satelliteId = message.GetSatelliteId();
        var hasLocation = !string.IsNullOrWhiteSpace(location);
        var hasSatellite = !string.IsNullOrWhiteSpace(satelliteId);

        return (hasLocation, hasSatellite) switch
        {
            (true, true) => $"Message from {sender} (in {location} via {satelliteId})",
            (true, false) => $"Message from {sender} (in {location})",
            (false, true) => $"Message from {sender} (via {satelliteId})",
            (false, false) => $"Message from {sender}"
        };
    }
}