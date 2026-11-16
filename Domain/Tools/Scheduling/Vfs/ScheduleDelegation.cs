using Domain.DTOs;
using Domain.Security;

namespace Domain.Tools.Scheduling.Vfs;

// A schedule as a delegation describes it, from either end: the scheduling server reads it off the
// schedule it stores, the agent off the schedule.json a turn writes, and both through the one parse
// the mount writes with, so the two texts agree byte for byte.
public static class ScheduleDelegation
{
    public static DelegatedObject Of(Schedule schedule) =>
        Describe(schedule.Id, schedule.AgentId, schedule.Prompt, schedule.CronExpression, schedule.RunAt, schedule.DeliverTo);

    // What writing this text at this path makes the schedule do: a create's or an edit's
    // schedule.json (or the directory a create may name instead), a move's destination directory.
    // A runAt with no offset is read in the zone given — the agent's own, which compose sets to the
    // scheduling server's. Null when the path names no schedule or the text is not one, which the
    // mount refuses to write anyway.
    public static DelegatedObject? OfWrite(string path, string text, TimeZoneInfo zone)
    {
        var node = SchedulePath.Parse(path);
        if (node.Kind is not (ScheduleNodeKind.ScheduleFile or ScheduleNodeKind.ScheduleDir)
            || ScheduleFileSystem.ParseSpec(text, out _) is not { Prompt: { Length: > 0 } prompt } spec)
        {
            return null;
        }

        var runAt = spec.RunAt is { } at ? ScheduleFileSystem.ToUtc(at, zone) : (DateTime?)null;
        return Describe(node.ScheduleId!, node.AgentId!, prompt, spec.Cron, runAt, spec.DeliverTo);
    }

    private static DelegatedObject Describe(
        string id, string agentId, string prompt, string? cron, DateTime? runAt, IReadOnlyList<string>? deliverTo) =>
        DelegatedObject.Of(DelegatedObject.Schedule, id, new DelegatedSchedule(
            agentId,
            prompt,
            cron,
            runAt is { } utc ? new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds() : null,
            deliverTo is { Count: > 0 } ? deliverTo : null));
}