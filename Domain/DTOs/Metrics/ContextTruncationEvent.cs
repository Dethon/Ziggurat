namespace Domain.DTOs.Metrics;

public record ContextTruncationEvent : MetricEvent
{
    public required string Sender { get; init; }

    // The account the turn acted for, as TokenUsageEvent.UserId.
    public string? UserId { get; init; }
    public required string Model { get; init; }
    public required int DroppedMessages { get; init; }
    public required int EstimatedTokensBefore { get; init; }
    public required int EstimatedTokensAfter { get; init; }
    public required int MaxContextTokens { get; init; }
}