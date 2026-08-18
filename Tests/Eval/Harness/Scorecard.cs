using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs;

namespace Tests.Eval.Harness;

// One JSON summary per pass, beside the dumps in the same ignored directory. Successes are not
// archived one by one — what a maintainer needs before and after a model bump is the per-claim
// rate, and one file that gets overwritten is what makes two of them a diff.
public static class Scorecard
{
    public static string Write(
        string directory, EvalTier tier, ServedRoute? route, IReadOnlyList<ClaimOutcome> claims)
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
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["claims"] = claims
                .GroupBy(c => c.Claim)
                .Aggregate(new JsonObject(), (claimsNode, group) =>
                {
                    var passes = group.Sum(c => c.Passes);
                    var runs = group.Sum(c => c.Runs);
                    claimsNode[group.Key] = new JsonObject
                    {
                        ["passes"] = passes,
                        ["runs"] = runs,
                        // Null rather than zero for a claim nothing exercised: a claim that ran and
                        // failed and a claim nobody tested are different findings, and a scorecard
                        // that spelled both `0.0` would hide the second one behind the first.
                        ["rate"] = runs == 0 ? null : JsonValue.Create((double)passes / runs)
                    };
                    return claimsNode;
                })
        };

        File.WriteAllText(path, summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}

public sealed record ClaimOutcome(string Claim, int Passes, int Runs);