using Domain.Channels;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Tools.Scheduling.Vfs;

namespace McpServerScheduling.Services;

public sealed record FirePlan(ChannelMessageNotification Payload, DateTime? NextRunAt, bool DeleteAfterFire);

public static class ScheduleFirePlanner
{
    public const string ConversationPrefix = "sched";
    public const string Sender = ServiceSenders.Scheduler;

    public static FirePlan Plan(
        Schedule schedule, IReadOnlyList<string> defaultDeliverTo, DateTime? nextRun, DateTimeOffset now)
    {
        // The targets, the origin and the clock reading are the shared fire planning's; what is the
        // scheduler's own is what to do with the record afterwards.
        var payload = FirePlanning.Compose(new Fire(
            FirePlanning.ConversationId(ConversationPrefix, schedule.Id, now),
            Sender,
            schedule.Prompt,
            schedule.AgentId,
            schedule.DeliverTo,
            schedule.UserId,
            new MessageOrigin(MessageOriginKind.Schedule, schedule.Id),
            now,
            schedule.CreatedBy,
            // The delegation as the last write left it, beside what the schedule does now: the agent
            // acts for its person only if the two still agree with what it signed.
            schedule.Delegation is { } token
                ? new FireDelegation(token, ScheduleDelegation.Of(schedule).Description)
                : null,
            Report: Report(schedule),
            // Whose schedule it is, so a suspended household's fire is refused even when no delegation
            // stands behind it.
            HouseholdId: schedule.TenantId is { Length: > 0 } household ? household : null), defaultDeliverTo);

        return new FirePlan(payload, nextRun, DeleteAfterFire: schedule.CronExpression is null);
    }

    // What the turn is, said beside its prompt rather than in it (the delegation signs the prompt).
    // A prompt written for later ("write Dana a poem and send it to her") reads on its own as
    // somebody's request, and the model went looking for a way to send while its reply was the
    // sending: four fires in ten answered that nothing reached her.
    private static string Report(Schedule schedule) =>
        $"This turn is the scheduled task \"{schedule.Id}\", set up by "
        + (schedule.UserId is { Length: > 0 } person ? person : "the household")
        + ", come due now. Your reply is what is delivered to them, so anything the task asks you to "
        + "send or tell them is your reply itself.";
}