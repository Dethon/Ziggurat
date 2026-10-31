using Domain.DTOs.Channel;
using Domain.Security;

namespace Domain.Tools.HomeAssistant.Vfs;

// A watch as a delegation describes it, from either end: the Home Assistant server reads it off the
// automation's metadata, the agent off the watch.json a turn writes, resolving the delivery the way
// the mount does when the file names none.
public static class WatchDelegation
{
    public static DelegatedObject Of(HaWatch watch) =>
        Describe(watch.Id, watch.Meta.AgentId, watch.Meta.Effects, watch.Meta.DeliverTo);

    // What writing this text at this path makes the watch do. `agentId` is the writing turn's agent,
    // which runs a new watch's prompts — a rewrite by another agent's turn leaves the watch on the
    // agent that created it, so what that turn describes is not what the home will run, and its fires
    // act for nobody. `existing` is the watch.json the agent read before an edit or a replace, whose
    // delivery a file naming none keeps; `origin` is the writing turn's, which a new watch naming none
    // delivers to. Null when the path names no watch or the text is not one, which the mount refuses
    // to write anyway.
    public static DelegatedObject? OfWrite(string path, string text, string? existing, string agentId, ReplyTarget? origin)
    {
        var node = HaVfsPath.Parse(path);
        if (node.Kind != HaVfsKind.WatchFile || !HaWatchAutomation.IsValidWatchId(node.WatchId)
            || TryParse(text) is not { } spec)
        {
            return null;
        }

        var deliverTo = spec.DeliverTo
                        ?? (existing is null ? null : TryParse(existing)?.DeliverTo)
                        ?? (origin is null ? null : [origin.Address is null ? origin.ChannelId : $"{origin.ChannelId}:{origin.Address}"]);
        return Describe(node.WatchId!, agentId, spec.Effects, deliverTo);
    }

    private static DelegatedObject Describe(
        string id, string agentId, IReadOnlyList<HaWatchEffect> effects, IReadOnlyList<string>? deliverTo) =>
        DelegatedObject.Of(DelegatedObject.Watch, id, new DelegatedWatch(
            agentId,
            [.. effects.OfType<HaPromptEffect>().Select(p => p.Prompt)],
            deliverTo is { Count: > 0 } ? deliverTo : null));

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