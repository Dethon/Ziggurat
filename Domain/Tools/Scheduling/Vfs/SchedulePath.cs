namespace Domain.Tools.Scheduling.Vfs;

public enum ScheduleNodeKind
{
    Root, AgentDir, AgentInfoFile, ScheduleDir, ScheduleFile, StatusFile, RunNowFile, Unknown
}

public sealed record ScheduleNode(ScheduleNodeKind Kind, string? AgentId, string? ScheduleId);

public static class SchedulePath
{
    public const string ScheduleFileName = "schedule.json";
    public const string StatusFileName = "status.json";
    public const string AgentInfoFileName = "agent_info.json";
    // An action file, so it carries no extension: it runs, it is never a script to open, and a
    // real shell will run it as `./run_now` once exec reaches the sandbox.
    public const string RunNowFileName = "run_now";

    public static ScheduleNode Parse(string path)
    {
        var segments = (path ?? "").Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // '.'/'..' are never valid ids, and the reserved file names are only meaningful as the
        // trailing file segment — never as an agent or schedule directory id. Guarding the id
        // positions keeps a schedule from being named e.g. "status.json".
        if (Array.Exists(segments, s => s is "." or ".."))
        {
            return new ScheduleNode(ScheduleNodeKind.Unknown, null, null);
        }

        return segments switch
        {
            [] => new ScheduleNode(ScheduleNodeKind.Root, null, null),
            [var agent] when !IsReserved(agent) => new ScheduleNode(ScheduleNodeKind.AgentDir, agent, null),
            [var agent, AgentInfoFileName] when !IsReserved(agent) => new ScheduleNode(ScheduleNodeKind.AgentInfoFile, agent, null),
            [var agent, var sched] when !IsReserved(agent) && !IsReserved(sched) => new ScheduleNode(ScheduleNodeKind.ScheduleDir, agent, sched),
            [var agent, var sched, ScheduleFileName] when !IsReserved(agent) && !IsReserved(sched) => new ScheduleNode(ScheduleNodeKind.ScheduleFile, agent, sched),
            [var agent, var sched, StatusFileName] when !IsReserved(agent) && !IsReserved(sched) => new ScheduleNode(ScheduleNodeKind.StatusFile, agent, sched),
            [var agent, var sched, RunNowFileName] when !IsReserved(agent) && !IsReserved(sched) => new ScheduleNode(ScheduleNodeKind.RunNowFile, agent, sched),
            _ => new ScheduleNode(ScheduleNodeKind.Unknown, null, null)
        };
    }

    private static bool IsReserved(string segment) =>
        segment is ScheduleFileName or StatusFileName or AgentInfoFileName or RunNowFileName;
}