using McpServerApps.Vfs;
using Shouldly;

namespace Tests.Unit.McpServerApps;

// The mount line sits in every turn's prompt of an agent offered /apps, beside every other mount's, under
// the mounts section's budget; how to build, check and publish an app is the `apps` skill's, loaded when
// a turn needs it. So the line says what the mount is and where an app lives, and points at the skill.
public class AppsMountDescriptionTests
{
    [Fact]
    public void TheMountLine_PointsAtTheAppsSkill_AndStaysShort()
    {
        AppsFileSystem.MountDescription.ShouldContain("`apps` skill");
        AppsFileSystem.MountDescription.ShouldContain("/apps/<slug>/");
        AppsFileSystem.MountDescription.Length.ShouldBeLessThanOrEqualTo(400);
    }
}