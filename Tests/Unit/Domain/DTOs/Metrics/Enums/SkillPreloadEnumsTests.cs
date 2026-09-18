using Domain.DTOs.Metrics.Enums;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.Metrics.Enums;

// Persisted as integers in Redis metric events, so the numbers are the wire format: only ever
// append. The same guard VoiceEnumsTests gives the voice family.
public class SkillPreloadEnumsTests
{
    [Theory]
    [InlineData(SkillPreloadDimension.Outcome, 0)]
    [InlineData(SkillPreloadDimension.Agent, 1)]
    [InlineData(SkillPreloadDimension.Channel, 2)]
    [InlineData(SkillPreloadDimension.Skill, 3)]
    public void SkillPreloadDimension_HasPinnedWireValues(SkillPreloadDimension dimension, int expected) =>
        ((int)dimension).ShouldBe(expected);

    [Theory]
    [InlineData(SkillPreloadMetric.Count, 0)]
    [InlineData(SkillPreloadMetric.LatencyMs, 1)]
    [InlineData(SkillPreloadMetric.InputTokens, 2)]
    public void SkillPreloadMetric_HasPinnedWireValues(SkillPreloadMetric metric, int expected) =>
        ((int)metric).ShouldBe(expected);
}