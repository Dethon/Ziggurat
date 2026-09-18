using Domain.DTOs.Metrics.Enums;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.Metrics.Enums;

// Persisted as integers in Redis metric events, so the numbers are the wire format: only ever
// append. The same guard VoiceEnumsTests gives the voice family.
public class ModalDismissalEnumsTests
{
    [Theory]
    [InlineData(ModalDismissalDimension.Kind, 0)]
    [InlineData(ModalDismissalDimension.Outcome, 1)]
    public void ModalDismissalDimension_HasPinnedWireValues(ModalDismissalDimension dimension, int expected) =>
        ((int)dimension).ShouldBe(expected);

    [Theory]
    [InlineData(ModalDismissalMetric.Count, 0)]
    [InlineData(ModalDismissalMetric.LatencyMs, 1)]
    public void ModalDismissalMetric_HasPinnedWireValues(ModalDismissalMetric metric, int expected) =>
        ((int)metric).ShouldBe(expected);
}