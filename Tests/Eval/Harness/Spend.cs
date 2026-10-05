using Domain.DTOs.Metrics;

namespace Tests.Eval.Harness;

// What one model was paid over a run, a scenario or a pass. Cached tokens are nullable for the
// reason the metric event's are: null is a provider that said nothing, and spelling that as zero
// would report "nothing was cached" about a prompt nobody measured.
public sealed record ModelSpend(
    decimal Cost, long InputTokens, long? CachedInputTokens, long OutputTokens, int Requests)
{
    public static ModelSpend Nothing { get; } = new(0m, 0, null, 0, 0);

    public static ModelSpend Of(TokenUsageEvent usage) =>
        new(usage.Cost, usage.InputTokens, usage.CachedInputTokens, usage.OutputTokens, 1);

    // How much of the prompt the provider served from cache, over the requests that said. Null
    // while nothing has reported: a share of nothing is not a share.
    public double? CacheShare =>
        CachedInputTokens is { } cached && InputTokens > 0 ? (double)cached / InputTokens : null;

    public static ModelSpend operator +(ModelSpend left, ModelSpend right) => new(
        left.Cost + right.Cost,
        left.InputTokens + right.InputTokens,
        left.CachedInputTokens is null && right.CachedInputTokens is null
            ? null
            : (left.CachedInputTokens ?? 0) + (right.CachedInputTokens ?? 0),
        left.OutputTokens + right.OutputTokens,
        left.Requests + right.Requests);
}

// What a run, a scenario or a pass cost, read off the usage events the deployment's own metrics
// read, so a scorecard says what it spent in the same numbers the dashboard would. Every model it
// paid — the agent's, the rubric judge's, Jev's — is one like any other: summed into the total and
// kept apart by the id its usage named.
public sealed class Spend : IEquatable<Spend>
{
    private readonly SortedDictionary<string, ModelSpend> _byModel;

    private Spend(SortedDictionary<string, ModelSpend> byModel)
    {
        _byModel = byModel;
        Total = byModel.Values.Aggregate(ModelSpend.Nothing, (sum, model) => sum + model);
    }

    public static Spend Nothing { get; } = new(new SortedDictionary<string, ModelSpend>(StringComparer.Ordinal));

    public static Spend Of(TokenUsageEvent usage) => Of(usage.Model, ModelSpend.Of(usage));

    public static Spend Of(string model, ModelSpend spend) =>
        new(new SortedDictionary<string, ModelSpend>(StringComparer.Ordinal) { [model] = spend });

    // By model id, in ordinal order, so two scorecards list their models the same way.
    public IReadOnlyDictionary<string, ModelSpend> ByModel => _byModel;

    public ModelSpend Total { get; }

    public decimal Cost => Total.Cost;

    public long InputTokens => Total.InputTokens;

    public long? CachedInputTokens => Total.CachedInputTokens;

    public long OutputTokens => Total.OutputTokens;

    public int Requests => Total.Requests;

    public double? CacheShare => Total.CacheShare;

    // Whether anything was paid for at all. A scenario whose provider failed before its first
    // response may still have paid Jev for the judgments it asked first.
    public bool Paid => Requests > 0;

    public static Spend operator +(Spend left, Spend right) => new(new SortedDictionary<string, ModelSpend>(
        left._byModel.Concat(right._byModel)
            .GroupBy(entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(
                model => model.Key,
                model => model.Select(entry => entry.Value).Aggregate((sum, spend) => sum + spend),
                StringComparer.Ordinal),
        StringComparer.Ordinal));

    public static Spend Sum(IEnumerable<Spend> spends) => spends.Aggregate(Nothing, (a, b) => a + b);

    public bool Equals(Spend? other) =>
        other is not null && _byModel.SequenceEqual(other._byModel);

    public override bool Equals(object? obj) => Equals(obj as Spend);

    public override int GetHashCode() =>
        _byModel.Aggregate(0, (hash, entry) => HashCode.Combine(hash, entry.Key, entry.Value));

    public override string ToString() =>
        string.Join(", ", _byModel.Select(entry => $"{entry.Key}: {entry.Value}"));
}