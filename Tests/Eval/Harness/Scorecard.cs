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
        var path = Path.Combine(directory, $"scorecard-{Filed(tier, route)}.json");

        var claimRows = Covered(
            Tallied(claims.Select(c => (c.Claim, c.Passes, c.Runs, c.Kinds, c.Loaders))), coverage);
        var scenarioRows = Marked(
            Addressed(
                Priced(
                    Tallied((scenarios ?? []).Select(s => (s.Name, s.Passes, s.Runs, s.Kinds, s.Loaders))),
                    scenarios ?? []),
                scenarios ?? []),
            scenarios ?? []);

        var summary = new JsonObject
        {
            // The route that served the pass, never the configured model: an upgrade that changed
            // nothing in configuration still changes this, and that is the point of the file.
            ["model"] = route?.Model,
            ["provider"] = route?.Provider,
            // The judge that preloaded, read off its own answers; "off" only where nothing was
            // ever judged (no key), and "unanswered" where judgments went out and none came back
            // in time — two different findings, and a pass of deadlines must not read as the key
            // being absent.
            ["preload"] = preloadModel
                          ?? ((scenarios ?? []).Any(s => s.PreloadOutcomes.Count > 0) ? "unanswered" : "off"),
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
                // The same rate once per agent. A model is chosen per agent, and what a voice
                // agent is asked for is not what a text agent is: the pass's one number says
                // whether a model is good, these say which agent it is good for.
                ["byAgent"] = ByAgent(scenarios ?? []),
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
                ["preloadOutcomes"] = Outcomes(scenarios ?? []),
                // What the exec screen asked across the pass. Every prompt is approved here, so
                // this changes no verdict; asked sandbox execs are its false positives on real
                // traffic, and that count should sit near zero.
                ["execScreen"] = Screened(ExecScreenTally.Sum((scenarios ?? []).Select(s => s.Screens)))
            },
            // Each claim row says how it is covered — "cited", "judged", or its exemption kind —
            // so a null rate stops meaning three different things.
            ["claims"] = claimRows,
            // The scenarios themselves, guards included: a guard asserts without citing a claim,
            // and before this section its rate existed nowhere a model-bump diff could see.
            ["scenarios"] = scenarioRows
        };

        // How good the answers were, where anything was graded: the one number a pass on one
        // model is compared with a pass on another by, and each dimension's beside it. Absent
        // rather than null on a pass that graded nothing, which is every pass but the research
        // tier's — a null here would read as a score nobody earned.
        if (Graded(scenarios ?? []) is { } grades)
        {
            ((JsonObject)summary["summary"]!)["grades"] = grades;
        }

        File.WriteAllText(path, summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    // Every tier's diff is across time but the research tier's, which is across models: one file
    // per model served is what lets two passes sit side by side, and what keeps the second
    // model's pass from overwriting the first's.
    private static string Filed(EvalTier tier, ServedRoute? route)
    {
        var name = tier.ToString().ToLowerInvariant();
        return tier == EvalTier.Research && route?.Model is { Length: > 0 } model
            ? $"{name}-{string.Concat(model.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '-'))}"
            : name;
    }

    // A scenario's marks, one dimension at a time: the mean, how many runs it is over, the
    // judge's sentences where it gave any, and one score for the scenario — the mean of its
    // dimensions rather than of its marks, so a dimension the judge skipped once weighs the same
    // as one it answered every time.
    private static JsonObject Marked(JsonObject rows, IEnumerable<ScenarioOutcome> outcomes)
    {
        outcomes = [.. outcomes];

        foreach (var grouped in outcomes.Where(outcome => outcome.Group is not null))
        {
            if (rows[grouped.Name] is JsonObject row)
            {
                row["group"] = grouped.Group;
            }
        }

        // Which route the runs took, where there were two. A zero is written: it is what says
        // the scenario's score is the in-place route's alone.
        foreach (var route in outcomes
                     .Where(outcome => outcome.DelegatedRuns is not null)
                     .GroupBy(outcome => outcome.Name))
        {
            if (rows[route.Key] is JsonObject row)
            {
                row["delegatedRuns"] = route.Sum(outcome => outcome.DelegatedRuns ?? 0);
            }
        }

        foreach (var (name, dimensions) in ByDimension(outcomes))
        {
            if (rows[name] is not JsonObject row)
            {
                continue;
            }

            row["score"] = dimensions.Average(dimension => dimension.Average(mark => mark.Score));
            row["grades"] = dimensions.Aggregate(new JsonObject(), (node, dimension) =>
            {
                var marked = new JsonObject
                {
                    ["mean"] = dimension.Average(mark => mark.Score),
                    ["runs"] = dimension.Count(),
                    ["scores"] = new JsonArray([.. dimension.Select(mark => JsonValue.Create(mark.Score))])
                };

                var reasons = dimension.Where(mark => mark.Reason is not null).ToList();
                if (reasons.Count > 0)
                {
                    marked["reasons"] = new JsonArray(
                        [.. reasons.Select(mark => JsonValue.Create($"{mark.Score:0.#}: {mark.Reason}"))]);
                }

                node[dimension.Key] = marked;
                return node;
            });
        }

        return rows;
    }

    private static IEnumerable<(string Name, List<IGrouping<string, GradeMark>> Dimensions)> ByDimension(
        IEnumerable<ScenarioOutcome> outcomes) =>
        outcomes
            .GroupBy(outcome => outcome.Name)
            .Select(scenario => (
                scenario.Key,
                scenario.SelectMany(outcome => outcome.Grades).GroupBy(mark => mark.Dimension).ToList()))
            .Where(scenario => scenario.Item2.Count > 0);

    // The pass's score is the mean of its scenarios' scores, and a dimension's the mean of that
    // dimension across the scenarios that have it: a scenario that took more runs, or was marked
    // on more dimensions, must not outvote the rest.
    private static JsonObject? Graded(IEnumerable<ScenarioOutcome> outcomes)
    {
        var all = outcomes.ToList();
        var graded = Scored(all);
        if (graded is null)
        {
            return null;
        }

        // Once more per group, where the scenarios say which they are in: a mean over rungs
        // every strong model clears and rungs few do is held up by the first kind.
        var groups = all
            .Where(outcome => outcome.Group is not null)
            .GroupBy(outcome => outcome.Group!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (group.Key, Score: Scored(group)))
            .Where(group => group.Score is not null)
            .ToList();
        if (groups.Count > 0)
        {
            graded["byGroup"] = groups.Aggregate(new JsonObject(), (node, group) =>
            {
                node[group.Key] = group.Score;
                return node;
            });
        }

        return graded;
    }

    private static JsonObject? Scored(IEnumerable<ScenarioOutcome> outcomes)
    {
        var scenarios = ByDimension(outcomes).ToList();
        if (scenarios.Count == 0)
        {
            return null;
        }

        return new JsonObject
        {
            ["score"] = scenarios.Average(scenario =>
                scenario.Dimensions.Average(dimension => dimension.Average(mark => mark.Score))),
            ["scenarios"] = scenarios.Count,
            ["byDimension"] = scenarios
                .SelectMany(scenario => scenario.Dimensions
                    .Select(dimension => (dimension.Key, Mean: dimension.Average(mark => mark.Score))))
                .GroupBy(dimension => dimension.Key, StringComparer.Ordinal)
                .OrderBy(dimension => dimension.Key, StringComparer.Ordinal)
                .Aggregate(new JsonObject(), (node, dimension) =>
                {
                    node[dimension.Key] = dimension.Average(scenario => scenario.Mean);
                    return node;
                })
        };
    }

    private static JsonObject Screened(ExecScreenTally tally) => new()
    {
        ["screened"] = tally.Screened,
        ["asked"] = new JsonObject
        {
            ["contained"] = tally.AskedContained,
            ["host"] = tally.AskedHost
        }
    };

    private static JsonObject Addressed(JsonObject rows, IEnumerable<ScenarioOutcome> outcomes)
    {
        foreach (var outcome in outcomes.Where(o => o.Agent is not null))
        {
            if (rows[outcome.Name] is JsonObject row)
            {
                row["agent"] = outcome.Agent;
            }
        }

        return rows;
    }

    // Declared and exercised travel with each agent's rate for the reason they travel with the
    // pass's: a filtered run that touched two of an agent's scenarios must not read as its rate.
    private static JsonObject ByAgent(IReadOnlyList<ScenarioOutcome> outcomes) =>
        outcomes
            .Where(outcome => outcome.Agent is not null)
            .GroupBy(outcome => outcome.Agent!, StringComparer.Ordinal)
            .OrderBy(agent => agent.Key, StringComparer.Ordinal)
            .Aggregate(new JsonObject(), (node, agent) =>
            {
                var totals = Totalled(
                    Tallied(agent.Select(s => (s.Name, s.Passes, s.Runs, s.Kinds, s.Loaders))));
                totals["spend"] = Spent(agent);
                if (Graded(agent) is { } grades)
                {
                    totals["grades"] = grades;
                }

                node[agent.Key] = totals;
                return node;
            });

    // A scenario's price beside its rate. Only where something was paid for: a scenario that did
    // not run has no spend key, so "cost nothing" never stands in for "never ran".
    private static JsonObject Priced(JsonObject rows, IEnumerable<ScenarioOutcome> outcomes)
    {
        foreach (var group in outcomes.GroupBy(outcome => outcome.Name))
        {
            var spend = Spend.Sum(group.Select(outcome => outcome.Spend ?? Spend.Nothing));
            if (spend.Paid && rows[group.Key] is JsonObject row)
            {
                row["spend"] = Spelled(spend);
            }

            // Only where an exec was screened, so a scenario that ran none carries no zeros.
            var screens = ExecScreenTally.Sum(group.Select(outcome => outcome.Screens));
            if (screens.Screened > 0 && rows[group.Key] is JsonObject screenedRow)
            {
                screenedRow["execScreen"] = Screened(screens);
            }
        }

        return rows;
    }

    private static JsonObject Spent(IEnumerable<ScenarioOutcome> outcomes)
    {
        var priced = outcomes.Where(outcome => outcome.Spend is { Paid: true }).ToList();
        var spend = Spend.Sum(priced.Select(outcome => outcome.Spend!));
        var runs = priced.Sum(outcome => outcome.Runs);
        var summary = Spelled(spend);
        summary["costPerRun"] = runs == 0 ? null : JsonValue.Create(spend.Cost / runs);
        return summary;
    }

    private static JsonObject Spelled(Spend spend)
    {
        var spelled = Spelled(spend.Total);

        // Every model the scenario or the pass paid, by the id its usage named — the agent's, the
        // rubric judge's, Jev's — each one like any other.
        spelled["byModel"] = spend.ByModel.Aggregate(new JsonObject(), (node, entry) =>
        {
            node[entry.Key] = Spelled(entry.Value);
            return node;
        });

        return spelled;
    }

    private static JsonObject Spelled(ModelSpend spend) => new()
    {
        ["cost"] = spend.Cost,
        ["inputTokens"] = spend.InputTokens,
        ["cachedInputTokens"] = spend.CachedInputTokens,
        ["outputTokens"] = spend.OutputTokens,
        ["requests"] = spend.Requests,
        ["cacheShare"] = spend.CacheShare
    };

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
        IEnumerable<(string Key, int Passes, int Runs, IReadOnlyList<FailureKind> Kinds, IReadOnlyList<Loader> Loaders)> outcomes) =>
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

                // Only where something failed: the kind says which half of a skill to edit — a
                // missing load is the description's, an ignored rule the body's, a preload the
                // scenario did not permit neither. Every kind that occurred, so a kind added to
                // the enum appears here without a third property to remember to add.
                var kinds = group
                    .SelectMany(o => o.Kinds)
                    .Where(kind => kind != FailureKind.RunFailed)
                    .GroupBy(kind => kind)
                    .OrderBy(g => g.Key)
                    .ToList();
                if (kinds.Count > 0)
                {
                    row["failures"] = new JsonObject(
                        kinds.Select(g => KeyValuePair.Create<string, JsonNode?>(g.Key.Key(), g.Count())));
                }

                node[group.Key] = row;
                return node;
            });
}

public sealed record ClaimOutcome(string Claim, int Passes, int Runs, int SkillNotLoaded = 0, int RuleIgnored = 0)
{
    public IReadOnlyList<Loader> Loaders { get; init; } = [];

    // One per failed run. The two counts above are the same thing for the two kinds that predate
    // the rest; this is what the scorecard reports, so a kind added to the enum needs no third
    // count here. Falls back to them where a caller set only those.
    public IReadOnlyList<FailureKind> Kinds
    {
        get => field.Count > 0 ? field : FailureKinds.From(SkillNotLoaded, RuleIgnored);
        init;
    } = [];
}

public sealed record ScenarioOutcome(
    string Name, int Passes, int Runs, int SkillNotLoaded = 0, int RuleIgnored = 0, Spend? Spend = null)
{
    public IReadOnlyList<Loader> Loaders { get; init; } = [];

    // The agent the scenario's turn is addressed to. Null for a row that is nobody's turn.
    public string? Agent { get; init; }

    public IReadOnlyList<FailureKind> Kinds
    {
        get => field.Count > 0 ? field : FailureKinds.From(SkillNotLoaded, RuleIgnored);
        init;
    } = [];

    public IReadOnlyDictionary<string, int> PreloadOutcomes { get; init; } = new Dictionary<string, int>();

    public ExecScreenTally Screens { get; init; } = ExecScreenTally.None;

    // Every mark of every run the scenario took, in run order.
    public IReadOnlyList<GradeMark> Grades { get; init; } = [];

    // The scenario's group within a graded family, where the family has any: its score is
    // reported once per group as well as once for the pass.
    public string? Group { get; init; }

    // How many of the runs handed work to a worker, for a scenario whose workers really run.
    // Null everywhere else: a canned delegation is a contract the checks already hold, and a
    // count of it here would read as a route the scenario offered.
    public int? DelegatedRuns { get; init; }
}