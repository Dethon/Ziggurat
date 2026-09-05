using Domain.DTOs.Channel;

namespace Domain.Channels;

// One unprompted fire, before it is a notification: what raised it, who runs it, what it says, and
// where the answer goes. Both the scheduler and the Home Assistant server's watch callback compose
// their fires through this, so how a `deliverTo` list becomes reply targets cannot drift between
// them.
public sealed record Fire(
    string ConversationId,
    string Sender,
    string Content,
    string AgentId,
    IReadOnlyList<string>? DeliverTo,
    string? UserId,
    MessageOrigin Origin,
    DateTimeOffset At);

public static class FirePlanning
{
    // One reading of the clock for both the conversation id's suffix and the Timestamp: the two
    // must agree, and the injected TimeProvider — not the wall clock — is what a scheduled agent's
    // one-clock world and a test's fake time both expect to drive them.
    public static string ConversationId(string prefix, string id, DateTimeOffset at) =>
        $"{prefix}-{id}-{at.ToUnixTimeSeconds()}";

    public static ChannelMessageNotification Compose(Fire fire, IReadOnlyList<string> defaultDeliverTo)
    {
        ArgumentNullException.ThrowIfNull(fire);
        ArgumentNullException.ThrowIfNull(defaultDeliverTo);

        return new ChannelMessageNotification
        {
            ConversationId = fire.ConversationId,
            Sender = fire.Sender,
            Content = fire.Content,
            AgentId = fire.AgentId,
            UserId = fire.UserId,
            ReplyTo = ReplyTargets(fire.DeliverTo, defaultDeliverTo),
            Origin = fire.Origin,
            Timestamp = fire.At
        };
    }

    // `channelId[:address]` entries into one reply target per channel. Several entries for one
    // channel (a few `voice:<id>` satellites) are one logical delivery: their sub-addresses merge
    // into a single target, so the fan-out produces one conversation spoken on every satellite
    // rather than a duplicate per satellite.
    public static IReadOnlyList<ReplyTarget> ReplyTargets(
        IReadOnlyList<string>? deliverTo, IReadOnlyList<string> defaultDeliverTo)
    {
        var channels = deliverTo is { Count: > 0 } ? deliverTo : defaultDeliverTo;
        return channels
            .Select(ParseTarget)
            .GroupBy(t => t.ChannelId)
            .Select(Coalesce)
            .ToList();
    }

    private static ReplyTarget Coalesce(IGrouping<string, ReplyTarget> group)
    {
        var targets = group.ToList();
        if (targets.Count == 1)
        {
            return targets[0];
        }

        // A bare entry (no address) means "all" for that channel and subsumes the specific ones.
        if (targets.Any(t => string.IsNullOrWhiteSpace(t.Address)))
        {
            return new ReplyTarget(group.Key, null, null);
        }

        var addresses = targets
            .Select(t => t.Address)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct()
            .ToList();
        return new ReplyTarget(group.Key, null, addresses.Count == 0 ? null : string.Join(",", addresses));
    }

    private static ReplyTarget ParseTarget(string entry)
    {
        var separator = entry.IndexOf(':');
        if (separator < 0)
        {
            return new ReplyTarget(entry, null);
        }

        var address = entry[(separator + 1)..];
        return new ReplyTarget(entry[..separator], null, string.IsNullOrWhiteSpace(address) ? null : address);
    }
}