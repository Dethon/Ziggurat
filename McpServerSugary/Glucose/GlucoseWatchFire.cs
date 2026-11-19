using Domain.Channels;
using Domain.DTOs.Channel;
using Domain.Tools.Sugary.Watches;
using McpServerSugary.Channel;

namespace McpServerSugary.Glucose;

// A glucose watch's prompt as the agent receives it, composed through the shared fire planning as a
// schedule's is, so how a delivery becomes reply targets cannot drift between the servers that fire.
// What it asks is the watch's own prompt, the words the delegation covers; what fired, on what reading
// and when is its report beside them. The delegation the watch keeps rides with what the watch does now,
// and the agent believes it only if its own signature holds for exactly that; the principal is the
// watch's userId, a label the agent does not believe. The household is the subject's, which the agent
// refuses the fire by while it is suspended.
public static class GlucoseWatchFire
{
    public const string ConversationPrefix = "glucose-watch";
    public const string Sender = ServiceSenders.GlucoseWatch;

    public static ChannelMessageNotification Compose(
        FiredGlucoseWatch fired, GlucosePromptEffect prompt, int effectIndex, GlucoseTriggerFire report,
        IReadOnlyList<string> defaultDeliverTo, DateTimeOffset now)
    {
        var watch = fired.Watch;
        var household = fired.Subject.TenantId;
        return FirePlanning.Compose(new Fire(
            // Two prompts of one fire, or two triggers firing together, read one clock: each is its own
            // conversation, so the trigger and the effect are in the id beside the instant.
            FirePlanning.ConversationId(ConversationPrefix, $"{watch.Id}-{report.TriggerIndex}-{effectIndex}", now),
            Sender,
            prompt.Prompt,
            watch.AgentId,
            watch.DeliverTo,
            watch.UserId,
            new MessageOrigin(MessageOriginKind.Watch, null, WatchId: watch.Id, Title: fired.Spec.Name),
            now,
            watch.UserId is { Length: > 0 } person
                ? new Principal(SugaryChannel.ChannelId, person, new Account(household, person))
                : null,
            watch.Delegation is { } token
                ? new FireDelegation(token, GlucoseWatchDelegation.Of(watch).Description)
                : null,
            GlucoseFireReport.Of(watch.Id, fired.Spec, report, fired.Zone, fired.Unit),
            household), defaultDeliverTo);
    }
}