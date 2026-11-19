using Domain.DTOs.Channel;
using Domain.DTOs.Sugary;
using Domain.Security;
using Domain.Security.Signing;
using Domain.Tools.HomeAssistant.Vfs;

namespace Domain.Tools.Sugary.Watches;

// A glucose watch as a delegation describes it, from either end: Sugary reads it off the watch it keeps,
// the agent off the watch.json a turn writes, resolving the delivery the way the mount does when the file
// names none.
//
// It is a watch to the agent, kind `watch` and the home watch's description: what the agent checks of a
// watch's fire — its id, the agent that runs it, that the words asked are one of its prompts as written,
// and where the answer goes — is all a glucose watch's prompt does too, and its fire's origin is a watch.
// The triggers are Sugary's and act for nobody, as a home watch's are the home's. A delegation lives with
// the server that stored it and names its object by id, so one kept by the home is never Sugary's to
// present, and a token lifted between two watches of one id would ask the same words of the same agent
// delivered to the same place — the fire the person signed for.
public static class GlucoseWatchDelegation
{
    public static DelegatedObject Of(StoredGlucoseWatch watch) =>
        WatchDelegation.Describe(watch.Id, watch.AgentId, PromptsOf(TryParse(watch.Spec)), watch.DeliverTo);

    // What writing this text at this path makes the watch do. `agentId` is the writing turn's agent, which
    // runs a new watch's prompts — a rewrite by another agent's turn leaves the watch on the agent that
    // created it, so what that turn describes is not what Sugary will run, and its fires act for nobody.
    // `existing` is the watch.json the agent read before an edit or a replace, whose delivery a file naming
    // none keeps; `origin` is the writing turn's, which a new watch naming none delivers to. Null when the
    // path names no watch or the text is not one, which the mount refuses to write anyway.
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
                        ?? WatchDelegation.OriginDelivery(origin);
        return WatchDelegation.Describe(node.WatchId!, agentId, PromptsOf(spec), deliverTo);
    }

    // The delegation a write carried, if it is the writer's own for this very watch; anything else would
    // fire for nobody, and kept it would only hide that until the fire. Never verified here: that is the
    // agent's, at fire time.
    public static string? KeptFor(string? token, Account writer, string id) =>
        Delegation.Unverified(token) is { Kind: DelegatedObject.Watch } named
        && named.ObjectId == id && named.Subject == writer.UserId && named.TenantId == writer.TenantId
            ? token
            : null;

    private static IReadOnlyList<string> PromptsOf(GlucoseWatchSpec? spec) =>
        [.. (spec?.Effects ?? []).OfType<GlucosePromptEffect>().Select(p => p.Prompt)];

    private static GlucoseWatchSpec? TryParse(string text)
    {
        try
        {
            return GlucoseWatchSpec.Parse(text);
        }
        catch (GlucoseWatchSpecException)
        {
            return null;
        }
    }
}