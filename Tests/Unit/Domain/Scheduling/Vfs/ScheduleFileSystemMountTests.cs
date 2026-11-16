using Domain.Contracts;
using Domain.Tools.Scheduling.Vfs;
using Infrastructure.Validation;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Scheduling.Vfs;

// What the model reads about the schedules mount as a whole. It sits on the backend beside the
// per-operation descriptions, so everything the model is told about this mount comes from one file.
public class ScheduleFileSystemMountTests
{
    // The blurb is a second copy of the timing contract, and it once contradicted the engine on both
    // halves: cron is evaluated in a zone, not UTC, and a zoneless runAt is read as wall clock in that
    // same zone rather than rejected. The zone is the calling household's, and the description is
    // served before any call names one, so it says local time and names no zone.
    [Fact]
    public void DescribeMount_SaysLocalTimeAndNamesNoZone()
    {
        var madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

        var description = Backend(madrid).DescribeMount;

        description.ShouldNotContain(madrid.Id);
        description.ShouldContain("local time");
        description.ShouldNotContain("UTC cron");
        description.ShouldNotContain("MUST include a time zone");
    }

    private static ScheduleFileSystem Backend(TimeZoneInfo zone)
    {
        var clock = new FakeTimeProvider();
        clock.SetLocalTimeZone(zone);
        return new ScheduleFileSystem(
            new FakeScheduleStore(), Mock.Of<IAgentCatalog>(), new CronValidator(), clock);
    }
}