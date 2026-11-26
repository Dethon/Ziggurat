using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Tokens;

public record TokensState
{
    public IReadOnlyList<TokenUsageEvent> Events { get; init; } = [];
    public TokenDimension GroupBy { get; init; } = TokenDimension.User;
    public TokenMetric Metric { get; init; } = TokenMetric.Tokens;
    public Dictionary<string, decimal> Breakdown { get; init; } = [];
    // The last how many days the page reads: a rolling window the server resolves on every read.
    public int Days { get; init; } = 1;
    public long Truncations { get; init; }
}