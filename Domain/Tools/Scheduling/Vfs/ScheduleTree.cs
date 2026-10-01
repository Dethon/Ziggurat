using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Scheduling.Vfs;

// Enumerates the schedule virtual filesystem as canonical, mount-relative paths (no leading slash),
// mirroring HaTree so glob matching is identical across the non-disk backends. The tree is:
//   <agentId>/                          (dir)
//   <agentId>/agent_info.json           (file)
//   <agentId>/<scheduleId>/             (dir)
//   <agentId>/<scheduleId>/schedule.json|status.json|run_now  (files; run_now is an action)
internal static class ScheduleTree
{
    public static IReadOnlyList<string> Directories(IAgentCatalog agents, IReadOnlyList<Schedule> schedules)
    {
        var dirs = agents.GetAll().Select(a => a.Id)
            .Concat(schedules.Where(s => agents.Exists(s.AgentId)).Select(s => $"{s.AgentId}/{s.Id}"));

        return dirs.Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<string> Files(IAgentCatalog agents, IReadOnlyList<Schedule> schedules)
    {
        var agentFiles = agents.GetAll().Select(a => $"{a.Id}/{SchedulePath.AgentInfoFileName}");

        var scheduleFiles = schedules
            .Where(s => agents.Exists(s.AgentId))
            .SelectMany(s => new[]
            {
                $"{s.AgentId}/{s.Id}/{SchedulePath.ScheduleFileName}",
                $"{s.AgentId}/{s.Id}/{SchedulePath.StatusFileName}",
                $"{s.AgentId}/{s.Id}/{SchedulePath.RunNowFileName}"
            });

        return agentFiles.Concat(scheduleFiles).OrderBy(f => f, StringComparer.Ordinal).ToList();
    }
}