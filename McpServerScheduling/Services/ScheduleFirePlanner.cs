using Domain.Channels;
using Domain.DTOs;
using Domain.DTOs.Channel;

namespace McpServerScheduling.Services;

public sealed record FirePlan(ChannelMessageNotification Payload, DateTime? NextRunAt, bool DeleteAfterFire);

public static class ScheduleFirePlanner
{
    public const string ConversationPrefix = "sched";
    public const string Sender = "scheduler";

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
            now), defaultDeliverTo);

        return new FirePlan(payload, nextRun, DeleteAfterFire: schedule.CronExpression is null);
    }
}