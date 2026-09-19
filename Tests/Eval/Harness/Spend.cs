using Domain.DTOs.Metrics;

namespace Tests.Eval.Harness;

// What a run, a scenario or a pass cost, read off the usage events the deployment's own metrics
// read, so a scorecard says what it spent in the same numbers the dashboard would. Cached tokens
// are nullable for the reason the metric event's are: null is a provider that said nothing, and
// spelling that as zero would report "nothing was cached" about a prompt nobody measured.
public sealed record Spend(
    decimal Cost, long InputTokens, long? CachedInputTokens, long OutputTokens, int Requests,
    // Jev's, kept apart from the model's: the judge answers no cost, so its price is configured
    // and its tokens are the number a description edit moves.
    decimal PreloadCost = 0m, long PreloadInputTokens = 0, int PreloadRequests = 0)
{
    public static Spend Nothing { get; } = new(0m, 0, null, 0, 0);

    // Whether anything was paid for at all. Model requests alone used to answer this, so a
    // scenario whose provider failed before its first response dropped the judgments it had
    // already paid Jev for: no spend key on the row, and the scenario left out of the pass total.
    public bool Paid => Requests > 0 || PreloadRequests > 0;

    public static Spend Of(TokenUsageEvent usage) =>
        new(usage.Cost, usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens, 1);

    // One judgment at what the provider charged for it, read off the same usage the deployment's
    // metrics read. A skipped Lemonade turn asked nothing and costs nothing.
    public static Spend OfPreload(SkillPreloadEvent preload) =>
        preload.InputTokens is { } tokens
            ? new Spend(0m, 0, null, 0, 0, preload.Cost ?? 0m, tokens, 1)
            : Nothing;

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
        left.Requests + right.Requests,
        left.PreloadCost + right.PreloadCost,
        left.PreloadInputTokens + right.PreloadInputTokens,
        left.PreloadRequests + right.PreloadRequests);

    public static Spend Sum(IEnumerable<Spend> spends) => spends.Aggregate(Nothing, (a, b) => a + b);
}