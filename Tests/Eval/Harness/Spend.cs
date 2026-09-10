using Domain.DTOs.Metrics;

namespace Tests.Eval.Harness;

// What a run, a scenario or a pass cost, read off the usage events the deployment's own metrics
// read, so a scorecard says what it spent in the same numbers the dashboard would. Cached tokens
// are nullable for the reason the metric event's are: null is a provider that said nothing, and
// spelling that as zero would report "nothing was cached" about a prompt nobody measured.
public sealed record Spend(
    decimal Cost, long InputTokens, long? CachedInputTokens, long OutputTokens, int Requests)
{
    public static Spend Nothing { get; } = new(0m, 0, null, 0, 0);

    public static Spend Of(TokenUsageEvent usage) =>
        new(usage.Cost, usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens, 1);

    // How much of the prompt the provider served from cache, over the requests that said. Null
    // while nothing has reported: a share of nothing is not a share.
    public double? CacheShare =>
        CachedInputTokens is { } cached && InputTokens > 0 ? (double)cached / InputTokens : null;

    public static Spend operator +(Spend left, Spend right) => new(
        left.Cost + right.Cost,
        left.InputTokens + right.InputTokens,
        left.CachedInputTokens is null && right.CachedInputTokens is null
            ? null
            : (left.CachedInputTokens ?? 0) + (right.CachedInputTokens ?? 0),
        left.OutputTokens + right.OutputTokens,
        left.Requests + right.Requests);

    public static Spend Sum(IEnumerable<Spend> spends) => spends.Aggregate(Nothing, (a, b) => a + b);
}