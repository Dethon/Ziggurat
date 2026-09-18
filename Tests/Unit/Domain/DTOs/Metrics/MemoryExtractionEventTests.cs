using System.Text.Json;
using Domain.DTOs.Metrics;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.Metrics;

public class MemoryExtractionEventTests
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Events stored before the outcome existed still read: the field is not required.
    [Fact]
    public void AnEventStoredBeforeTheOutcomeExisted_StillReads_WithNoOutcome()
    {
        const string json = """{"type":"memory_extraction","durationMs":1200,"candidateCount":2,"storedCount":1,"userId":"alice","timestamp":"2026-09-01T10:00:00+00:00"}""";

        var evt = JsonSerializer.Deserialize<MetricEvent>(json, _options).ShouldBeOfType<MemoryExtractionEvent>();

        evt.Outcome.ShouldBeNull();
        evt.CandidateCount.ShouldBe(2);
    }

    [Fact]
    public void TheOutcome_RoundTripsThroughTheBaseType()
    {
        MetricEvent evt = new MemoryExtractionEvent
        {
            DurationMs = 10, CandidateCount = 0, StoredCount = 0, UserId = "alice", Outcome = MemoryExtractionOutcomes.Failed
        };

        var back = JsonSerializer.Deserialize<MetricEvent>(JsonSerializer.Serialize(evt, _options), _options)
            .ShouldBeOfType<MemoryExtractionEvent>();

        back.Outcome.ShouldBe("failed");
    }
}