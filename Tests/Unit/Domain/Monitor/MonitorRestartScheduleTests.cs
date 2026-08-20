using Domain.Monitor;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

// The schedule on its own, with the randomness held still. Every number here is a wait a real
// outage would sit through, and the one that matters most is the reset rule: a monitor that dies
// after two seconds, ten times running, is not ten first failures.
public class MonitorRestartScheduleTests
{
    private static readonly MonitorRestartPolicy _policy = new()
    {
        InitialDelay = TimeSpan.FromSeconds(1),
        MaxDelay = TimeSpan.FromSeconds(64),
        HealthyRun = TimeSpan.FromMinutes(2)
    };

    // Equal jitter: half the computed delay, plus a random share of the other half. Pinning the
    // random source to its ends is what makes the bounds assertable at all.
    private static MonitorRestartSchedule Schedule(double jitter = 0) => new(_policy, () => jitter);

    private static readonly TimeSpan _crashed = TimeSpan.FromMilliseconds(50);

    [Fact]
    public void NextDelay_ConsecutiveFailures_DoublesFromTheInitialDelay()
    {
        var schedule = Schedule(jitter: 1);

        var delays = Enumerable.Range(0, 5).Select(_ => schedule.NextDelay(_crashed)).ToList();

        delays.ShouldBe([
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16)
        ]);
    }

    [Fact]
    public void NextDelay_ADeadDependency_StopsDoublingAtTheCeiling()
    {
        var schedule = Schedule(jitter: 1);

        var delays = Enumerable.Range(0, 20).Select(_ => schedule.NextDelay(_crashed)).ToList();

        delays.ShouldAllBe(d => d <= _policy.MaxDelay);
        delays[^1].ShouldBe(_policy.MaxDelay);
    }

    [Theory]
    [InlineData(0, 0.5)]
    [InlineData(0.5, 0.75)]
    [InlineData(1, 1)]
    public void NextDelay_Jitter_SpreadsTheDelayOverTheTopHalfOfTheInterval(double random, double share)
    {
        Schedule(random).NextDelay(_crashed).ShouldBe(_policy.InitialDelay * share);
    }

    // Two agents restarting in the same second against the same dependency is the thing jitter
    // exists to prevent, so two schedules with different random draws must not agree.
    [Fact]
    public void NextDelay_TwoSchedulesFailingTogether_DoNotWaitTheSameLength()
    {
        Schedule(jitter: 0).NextDelay(_crashed).ShouldNotBe(Schedule(jitter: 1).NextDelay(_crashed));
    }

    [Fact]
    public void NextDelay_AShortRunBeforeFailing_KeepsBackingOff()
    {
        var schedule = Schedule(jitter: 1);
        schedule.NextDelay(_crashed);

        schedule.NextDelay(_policy.HealthyRun - TimeSpan.FromSeconds(1))
            .ShouldBe(TimeSpan.FromSeconds(2));
    }

    // The whole point of the reset rule: recovery is a run that lasted, never a run that started.
    [Fact]
    public void NextDelay_ASustainedHealthyRun_ResetsTheBackoff()
    {
        var schedule = Schedule(jitter: 1);
        schedule.NextDelay(_crashed);
        schedule.NextDelay(_crashed);
        schedule.NextDelay(_crashed);

        schedule.NextDelay(_policy.HealthyRun).ShouldBe(_policy.InitialDelay);
        schedule.NextDelay(_crashed).ShouldBe(_policy.InitialDelay * 2);
    }

    [Fact]
    public void Attempt_CountsTheConsecutiveFailuresBehindTheCurrentDelay()
    {
        var schedule = Schedule(jitter: 1);
        schedule.Attempt.ShouldBe(0);

        schedule.NextDelay(_crashed);
        schedule.NextDelay(_crashed);
        schedule.Attempt.ShouldBe(2);

        schedule.NextDelay(_policy.HealthyRun);
        schedule.Attempt.ShouldBe(1);
    }
}