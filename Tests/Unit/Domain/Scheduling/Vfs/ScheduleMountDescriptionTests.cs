using Domain.Prompts;
using Domain.Tools.Scheduling.Vfs;
using Shouldly;

namespace Tests.Unit.Domain.Scheduling.Vfs;

// The mount's description is read on every turn of every agent that schedules, and the `scheduling`
// skill is loaded on the turn that needs it: the description says what the mount is and where the
// rules are, as the timers mount does, and leaves the file's fields, the directory to write to and the
// timing to the skill — they were written out in both, three hundred tokens a turn.
public class ScheduleMountDescriptionTests
{
    [Fact]
    public void TheMount_SaysWhatItIs_AndLeavesTheRulesToTheSchedulingSkill()
    {
        var description = ScheduleFileSystem.MountDescription;

        description.ShouldContain("`scheduling` skill");
        description.ShouldNotContain("cron|runAt");
        PromptTokens.Estimate(description).ShouldBeLessThan(120);
    }
}