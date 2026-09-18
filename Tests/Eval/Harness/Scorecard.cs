using System.Text.Json;
using System.Text.Json.Nodes;
using Infrastructure.Agents.ChatClients;

namespace Tests.Eval.Harness;

// One JSON summary per pass, beside the dumps in the same ignored directory. Successes are not
// archived one by one — what a maintainer needs before and after a model bump is the per-claim
// rate, and one file that gets overwritten is what makes two of them a diff.
public static class Scorecard
{
    public static string Write(
        string directory, EvalTier tier, ServedRoute? route, IReadOnlyList<ClaimOutcome> claims,
        IReadOnlyList<ScenarioOutcome>? scenarios = null,
        IReadOnlyDictionary<string, string>? coverage = null,
        TimeProvider? clock = null,
        string? preloadModel = null)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"scorecard-{tier.ToString().ToLowerInvariant()}.json");

        var claimRows = Covered(
            Tallied(claims.Select(c => (c.Claim, c.Passes, c.Runs, c.SkillNotLoaded, c.RuleIgnored, c.Loaders))), coverage);
        var scenarioRows = Priced(
            Tallied((scenarios ?? []).Select(s => (s.Name, s.Passes, s.Runs, s.SkillNotLoaded, s.RuleIgnored, s.Loaders))),
            scenarios ?? []);

        var summary = new JsonObject
        {
            // The route that served the pass, never the configured model: an upgrade that changed
            // nothing in configuration still changes this, and that is the point of the file.
            ["model"] = route?.Model,
            ["provider"] = route?.Provider,
            // The judge that preloaded, read off its own answers, or "off": a pass with no
            // TypeSafe key is labelled rather than mistaken for one where Jev abstained every time.
            ["preload"] = preloadModel ?? "off",
            ["tier"] = tier.ToString().ToLowerInvariant(),
            // When the pass ran, which is the axis two scorecards are compared along. It is the
            // one time in this suite that is not the scenario's pinned instant: a scorecard is
            // about a run, not about the turn inside it.
            ["timestamp"] = (clock ?? TimeProvider.System).GetUtcNow().ToString("O"),
            // The whole run in one place: the per-row sections say which claim moved, this says
            // whether the run got better or worse without the reader summing rows across a diff.
            ["summary"] = new JsonObject
            {
                ["claims"] = Totalled(claimRows),
                ["scenarios"] = Totalled(scenarioRows),
                // What the pass cost, in the two numbers a maintainer acts on: the price of an
                // average run, and how much of the prompt the provider served from cache. The
                // per-row spend says which scenario is expensive; this says whether the pass is.
                ["spend"] = Spent(scenarios ?? []),
                // The whole pass's loader split, so the reader sees how often Jev preloaded, how
                // often the model still loaded for itself and how often nobody did, without
                // summing rows.
                ["loader"] = Loaders(scenarios ?? []),
                // What the judge answered across the pass: a run of deadlines or errors reads
                // as a TypeSafe problem here, where "preload: off" would have hidden it.
                ["preloadOutcomes"] = Outcomes(scenarios ?? [])
            },
            // Each claim row says how it is covered — "cited", "judged", or its exemption kind —
            // so a null rate stops meaning three different things.
            ["claims"] = claimRows,
            // The scenarios themselves, guards included: a guard asserts without citing a claim,
            // and before this section its rate existed nowhere a model-bump diff could see.
            ["scenarios"] = scenarioRows
        };

        File.WriteAllText(path, summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    // A scenario's price beside its rate. Only where something was paid for: a scenario that did
    // not run has no spend key, so "cost nothing" never stands in for "never ran".
    private static JsonObject Priced(JsonObject rows, IEnumerable<ScenarioOutcome> outcomes)
    {
        foreach (var group in outcomes.GroupBy(outcome => outcome.Name))
        {
            var spend = Spend.Sum(group.Select(outcome => outcome.Spend ?? Spend.Nothing));
            if (spend.Requests > 0 && rows[group.Key] is JsonObject row)
            {
                row["spend"] = Spelled(spend);
            }
        }

        return rows;
    }

    private static JsonObject Spent(IEnumerable<ScenarioOutcome> outcomes)
    {
        var priced = outcomes.Where(outcome => outcome.Spend is { Requests: > 0 }).ToList();
        var spend = Spend.Sum(priced.Select(outcome => outcome.Spend!));
        var runs = priced.Sum(outcome => outcome.Runs);
        var summary = Spelled(spend);
        summary["costPerRun"] = runs == 0 ? null : JsonValue.Create(spend.Cost / runs);
        return summary;
    }

    private static JsonObject Spelled(Spend spend)
    {
        var spelled = new JsonObject
        {
            ["cost"] = spend.Cost,
            ["inputTokens"] = spend.InputTokens,
            ["cachedInputTokens"] = spend.CachedInputTokens,
            ["outputTokens"] = spend.OutputTokens,
            ["requests"] = spend.Requests,
            ["cacheShare"] = spend.CacheShare
        };

        // Only where a judgment was paid for: a pass with the preload off has no preload key,
        // so "cost nothing" never stands in for "never asked".
        if (spend.PreloadRequests > 0)
        {
            spelled["preload"] = new JsonObject
            {
                ["cost"] = spend.PreloadCost,
                ["inputTokens"] = spend.PreloadInputTokens,
                ["requests"] = spend.PreloadRequests
            };
        }

        return spelled;
    }

    private static JsonObject Outcomes(IEnumerable<ScenarioOutcome> outcomes) =>
        ScenarioRunner.Summed(outcomes.Select(outcome => outcome.PreloadOutcomes))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Aggregate(new JsonObject(), (node, entry) =>
            {
                node[entry.Key] = entry.Value;
                return node;
            });

    private static JsonObject Loaders(IEnumerable<ScenarioOutcome> outcomes) =>
        Loaders(outcomes.SelectMany(outcome => outcome.Loaders));

    private static JsonObject Loaders(IEnumerable<Loader> loaders)
    {
        var counted = loaders.Where(loader => loader != Loader.None).ToList();
        return new JsonObject
        {
            ["host"] = counted.Count(loader => loader == Loader.Host),
            ["model"] = counted.Count(loader => loader == Loader.Model),
            ["nobody"] = counted.Count(loader => loader == Loader.Nobody)
        };
    }

    private static JsonObject Covered(JsonObject claims, IReadOnlyDictionary<string, string>? coverage)
    {
        foreach (var (claim, how) in coverage ?? new Dictionary<string, string>())
        {
            if (claims[claim] is JsonObject row)
            {
                row["coverage"] = how;
            }
        }

        return claims;
    }

    // "exercised" beside "declared" keeps a filtered pass honest: a rate over the six scenarios
    // that ran must not read as a rate over the suite.
    private static JsonObject Totalled(JsonObject rows)
    {
        var tallies = rows.Select(row => (JsonObject)row.Value!).ToList();
        var passes = tallies.Sum(tally => (int)tally["passes"]!);
        var runs = tallies.Sum(tally => (int)tally["runs"]!);

        return new JsonObject
        {
            ["declared"] = tallies.Count,
            ["exercised"] = tallies.Count(tally => (int)tally["runs"]! > 0),
            ["passes"] = passes,
            ["runs"] = runs,
            ["rate"] = runs == 0 ? null : JsonValue.Create((double)passes / runs)
        };
    }

    private static JsonObject Tallied(
        IEnumerable<(string Key, int Passes, int Runs, int SkillNotLoaded, int RuleIgnored, IReadOnlyList<Loader> Loaders)> outcomes) =>
        outcomes
            .GroupBy(o => o.Key)
            .Aggregate(new JsonObject(), (node, group) =>
            {
                var passes = group.Sum(o => o.Passes);
                var runs = group.Sum(o => o.Runs);
                var row = new JsonObject
                {
                    ["passes"] = passes,
                    ["runs"] = runs,
                    // Null rather than zero for one nothing exercised: ran-and-failed and
                    // never-tested are different findings, and a scorecard that spelled both
                    // `0.0` would hide the second one behind the first.
                    ["rate"] = runs == 0 ? null : JsonValue.Create((double)passes / runs)
                };

                // Only where a load was required: who loaded it, run by run, so a claim met by
                // Jev every time and a claim the model still meets for itself read differently.
                var loaders = group.SelectMany(o => o.Loaders).Where(loader => loader != Loader.None).ToList();
                if (loaders.Count > 0)
                {
                    row["loader"] = Loaders(loaders);
                }

                // Only where something failed: the kind says which half of a skill to edit —
                // a missing load is the description's, an ignored rule the body's.
                var notLoaded = group.Sum(o => o.SkillNotLoaded);
                var ignored = group.Sum(o => o.RuleIgnored);
                if (notLoaded + ignored > 0)
                {
                    row["failures"] = new JsonObject
                    {
                        [FailureKind.SkillNotLoaded.Key()] = notLoaded,
                        [FailureKind.RuleIgnored.Key()] = ignored
                    };
                }

                node[group.Key] = row;
                return node;
            });
}

public sealed record ClaimOutcome(string Claim, int Passes, int Runs, int SkillNotLoaded = 0, int RuleIgnored = 0)
{
    public IReadOnlyList<Loader> Loaders { get; init; } = [];
}

public sealed record ScenarioOutcome(
    string Name, int Passes, int Runs, int SkillNotLoaded = 0, int RuleIgnored = 0, Spend? Spend = null)
{
    public IReadOnlyList<Loader> Loaders { get; init; } = [];

    public IReadOnlyDictionary<string, int> PreloadOutcomes { get; init; } = new Dictionary<string, int>();
}