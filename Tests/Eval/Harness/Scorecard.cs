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
        TimeProvider? clock = null)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"scorecard-{tier.ToString().ToLowerInvariant()}.json");

        var summary = new JsonObject
        {
            // The route that served the pass, never the configured model: an upgrade that changed
            // nothing in configuration still changes this, and that is the point of the file.
            ["model"] = route?.Model,
            ["provider"] = route?.Provider,
            ["tier"] = tier.ToString().ToLowerInvariant(),
            // When the pass ran, which is the axis two scorecards are compared along. It is the
            // one time in this suite that is not the scenario's pinned instant: a scorecard is
            // about a run, not about the turn inside it.
            ["timestamp"] = (clock ?? TimeProvider.System).GetUtcNow().ToString("O"),
            ["claims"] = Tallied(claims.Select(c => (c.Claim, c.Passes, c.Runs))),
            // The scenarios themselves, guards included: a guard asserts without citing a claim,
            // and before this section its rate existed nowhere a model-bump diff could see.
            ["scenarios"] = Tallied((scenarios ?? []).Select(s => (s.Name, s.Passes, s.Runs)))
        };

        File.WriteAllText(path, summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }

    private static JsonObject Tallied(IEnumerable<(string Key, int Passes, int Runs)> outcomes) =>
        outcomes
            .GroupBy(o => o.Key)
            .Aggregate(new JsonObject(), (node, group) =>
            {
                var passes = group.Sum(o => o.Passes);
                var runs = group.Sum(o => o.Runs);
                node[group.Key] = new JsonObject
                {
                    ["passes"] = passes,
                    ["runs"] = runs,
                    // Null rather than zero for one nothing exercised: ran-and-failed and
                    // never-tested are different findings, and a scorecard that spelled both
                    // `0.0` would hide the second one behind the first.
                    ["rate"] = runs == 0 ? null : JsonValue.Create((double)passes / runs)
                };
                return node;
            });
}

public sealed record ClaimOutcome(string Claim, int Passes, int Runs);

public sealed record ScenarioOutcome(string Name, int Passes, int Runs);