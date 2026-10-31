using Domain.DTOs.Metrics.Enums;

namespace Domain.DTOs.Metrics;

// One use of the shared Lemonade, counted against the household it was for (TenantId), so the
// operator can see who uses what: every household's speech and recall run on the operator's box.
// The speech typist posts at Lemonade directly (docs/adr/0026) and is never counted.
public record LemonadeUsageEvent : MetricEvent
{
    public required LemonadeUse Use { get; init; }

    // In the use's own unit: seconds of audio for a transcription, characters of text for speech,
    // texts for an embedding.
    public required double Amount { get; init; }

    // Who asked, as a dashboard string: a VoiceChannels value for speech, "memory" for an embedding.
    public string? Channel { get; init; }
}