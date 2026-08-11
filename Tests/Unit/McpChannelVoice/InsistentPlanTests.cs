using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class InsistentPlanTests
{
    private static readonly InsistentDefaults _defaults = new();

    [Fact]
    public void Resolve_NullOptions_UsesDefaults()
    {
        var plan = InsistentPlan.Resolve(null, _defaults);

        plan.Gap.ShouldBe(TimeSpan.FromSeconds(15));
        plan.MaxRepeats.ShouldBe(12);
        plan.MaxDuration.ShouldBeNull();
    }

    [Fact]
    public void Resolve_RequestOverridesGapAndRepeats()
    {
        var plan = InsistentPlan.Resolve(new InsistentOptions { GapSeconds = 10, MaxRepeats = 3 }, _defaults);

        plan.Gap.ShouldBe(TimeSpan.FromSeconds(10));
        plan.MaxRepeats.ShouldBe(3);
    }

    [Fact]
    public void Resolve_DurationOnly_IsDurationBoundedNotClippedToDefaultRepeats()
    {
        // A request that sets only MaxDurationSeconds must be bounded by duration, not also
        // silently capped at the default repeat count.
        var plan = InsistentPlan.Resolve(new InsistentOptions { MaxDurationSeconds = 120 }, _defaults);

        plan.MaxRepeats.ShouldBe(int.MaxValue);
        plan.MaxDuration.ShouldBe(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void Resolve_NonPositiveDuration_IsTreatedAsNoDurationCap()
    {
        var plan = InsistentPlan.Resolve(new InsistentOptions { MaxDurationSeconds = 0 }, _defaults);

        plan.MaxDuration.ShouldBeNull();
        plan.MaxRepeats.ShouldBe(12);
    }

    [Fact]
    public void GainFor_DefaultRamp_RisesFromHalfToFullByRampRounds()
    {
        var plan = InsistentPlan.Resolve(null, _defaults); // RampStartPercent 50, RampRounds 4

        plan.GainFor(0).ShouldBe(0.5, 0.001);
        plan.GainFor(1).ShouldBe(0.5 + 0.5 / 3, 0.001);
        plan.GainFor(3).ShouldBe(1.0, 0.001);
        plan.GainFor(10).ShouldBe(1.0, 0.001);
    }

    [Fact]
    public void Resolve_RequestRampStartOverride_WinsOverDefaults()
    {
        var plan = InsistentPlan.Resolve(new InsistentOptions { RampStartPercent = 100 }, _defaults);

        plan.GainFor(0).ShouldBe(1.0, 0.001);
    }
}