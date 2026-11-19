using Domain.DTOs.Channel;
using Domain.Security;

namespace Domain.Tools.HomeAssistant.Vfs;

// A watch as a delegation describes it, from either end: the Home Assistant server reads it off the
// automation's metadata, the agent off the watch.json a turn writes, resolving the delivery the way
// the mount does when the file names none.
public static class WatchDelegation
{
    public static DelegatedObject Of(HaWatch watch) =>
        Describe(watch.Id, watch.Meta.AgentId, PromptsOf(watch.Meta.Effects), watch.Meta.DeliverTo);

    // What writing this text at this path makes the watch do. `agentId` is the writing turn's agent,
    // which runs a new watch's prompts — a rewrite by another agent's turn leaves the watch on the
    // agent that created it, so what that turn describes is not what the home will run, and its fires
    // act for nobody. `existing` is the watch.json the agent read before an edit or a replace, whose
    // delivery a file naming none keeps; `origin` is the writing turn's, which a new watch naming none
    // delivers to. Null when the path names no watch or the text is not one, which the mount refuses
    // to write anyway.
    public static DelegatedObject? OfWrite(string path, string text, string? existing, string agentId, ReplyTarget? origin)
    {
        var node = HaWatchPath.Parse(path);
        if (node.Kind != HaWatchKind.WatchFile || !HaWatchAutomation.IsValidWatchId(node.WatchId)
            || TryParse(text) is not { } spec)
        {
            return null;
        }

        var deliverTo = spec.DeliverTo
                        ?? (existing is null ? null : TryParse(existing)?.DeliverTo)
                        ?? OriginDelivery(origin);
        return Describe(node.WatchId!, agentId, PromptsOf(spec.Effects), deliverTo);
    }

    // What any watch does as far as the agent is concerned, whoever keeps it: a home watch and a glucose
    // watch are described by this one shape, so the agent's check of a watch's fire is one check.
    public static DelegatedObject Describe(
        string id, string agentId, IReadOnlyList<string> prompts, IReadOnlyList<string>? deliverTo) =>
        DelegatedObject.Of(DelegatedObject.Watch, id, new DelegatedWatch(
            agentId, prompts, deliverTo is { Count: > 0 } ? deliverTo : null));

    // Where a watch naming no delivery answers: the writing turn's own channel and address.
    public static IReadOnlyList<string>? OriginDelivery(ReplyTarget? origin) =>
        origin is null ? null : [origin.Address is null ? origin.ChannelId : $"{origin.ChannelId}:{origin.Address}"];

    private static IReadOnlyList<string> PromptsOf(IReadOnlyList<HaWatchEffect> effects) =>
        [.. effects.OfType<HaPromptEffect>().Select(p => p.Prompt)];

    private static HaWatchSpec? TryParse(string text)
    {
        try
        {
            return HaWatchSpec.Parse(text);
        }
        catch (HaWatchSpecException)
        {
            return null;
        }
    }
}