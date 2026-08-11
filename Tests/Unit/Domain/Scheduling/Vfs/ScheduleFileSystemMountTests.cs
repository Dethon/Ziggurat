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
    // The blurb is a second copy of the timing contract, and it contradicted the engine on both
    // halves: ComputeNextRunAt evaluates cron against TimeProvider.LocalTimeZone, not UTC, and ToUtc
    // reads a zoneless runAt as wall clock in that same zone rather than rejecting it. Reading the
    // zone off the injected provider is what makes the two agree by construction.
    [Fact]
    public void DescribeMount_NamesTheZoneTheEngineComputesIn()
    {
        var madrid = TimeZoneInfo.FindSystemTimeZoneById("Europe/Madrid");

        var description = Backend(madrid).DescribeMount;

        description.ShouldContain(madrid.Id);
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