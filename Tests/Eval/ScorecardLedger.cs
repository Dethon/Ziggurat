using Domain.DTOs;
using Domain.Prompts;
using Infrastructure.Agents.ChatClients;
using Tests.Eval.Harness;

namespace Tests.Eval;

// The run-wide accumulator behind every EvalScorecard instance. Scenario classes run as parallel
// xUnit collections, each disposing its own fixture whenever its last theory finishes — so every
// write is a rewrite of everything recorded so far, and the last fixture to finish leaves the
// whole run behind, never its own slice.
public sealed class ScorecardLedger
{
    private readonly Dictionary<EvalTier, TierState> _tiers = [];
    private readonly Lock _gate = new();
    private ServedRoute? _route;

    public void Record(EvalTier tier, Scenario scenario, ScenarioResult result)
    {
        lock (_gate)
        {
            if (!_tiers.TryGetValue(tier, out var state))
            {
                state = new TierState([], []);
                _tiers[tier] = state;
            }

            // The scenario's own claims ride its rate whole; the conditional ones arrive already
            // tallied over the runs that gave them material, which is a different denominator.
            state.Outcomes.AddRange(scenario.Claims
                .Concat(scenario.Judged.Select(check => check.Claim))
                .Select(claim => new ClaimOutcome(claim, result.Passes, result.Attempts))
                .Concat(result.Conditionals));
            // The scenario's own rate, cited or not: a guard's drift is only a diff if the guard
            // has a number.
            state.Scenarios.Add(new ScenarioOutcome(scenario.Name, result.Passes, result.Attempts));
        }
    }

    // The route that served the pass. It comes from a recording rather than from configuration:
    // an upgrade that changed nothing in appsettings still changes this, which is the whole point.
    public void Observe(ServedRoute? route)
    {
        lock (_gate)
        {
            _route = route ?? _route;
        }
    }

    public void WriteAll(string directory, Func<ServedRoute?, Task<ServedRoute?>> resolve)
    {
        lock (_gate)
        {
            if (_tiers.Count == 0)
            {
                // Nothing ran — the gate was closed, or every scenario skipped. A scorecard of
                // nothing would look like a pass with no claims, which is worse than no file.
                return;
            }

            // Resolved once and kept: the first write pays the provider lookup, and every later
            // fixture's dispose presents a route already carrying its name.
            _route = resolve(_route).GetAwaiter().GetResult();

            foreach (var (tier, state) in _tiers)
            {
                // Every declared claim, seeded at nothing, so a claim no scenario exercised
                // appears with a null rate rather than being absent — absent reads as "there is
                // no such claim", which is the one thing the file must not say.
                var seeded = PromptManifest.Claims
                    .Select(claim => new ClaimOutcome(claim.Id, 0, 0))
                    .Concat(state.Outcomes)
                    .ToList();

                // Scenarios the same way as claims: one that did not run appears at a null rate
                // rather than being absent, so a filtered pass is legible as a filtered pass.
                var scenarios = EvalSuite.All
                    .Select(scenario => new ScenarioOutcome(scenario.Name, 0, 0))
                    .Concat(state.Scenarios)
                    .ToList();

                Scorecard.Write(directory, tier, _route, seeded, scenarios, Coverage());
            }
        }
    }

    // How each declared claim is covered, from the same two sources the coverage tests read: a
    // scenario citing it, or its typed exemption. The scorecard row then says what a null rate
    // means — never tested, waiting on a fixture, a judgement, or a rule the deployment does not
    // follow — instead of leaving the reader to guess.
    private static IReadOnlyDictionary<string, string> Coverage()
    {
        var cited = EvalSuite.All.SelectMany(s => s.Claims).ToHashSet();
        var judged = EvalSuite.All.SelectMany(s => s.Judged.Select(check => check.Claim)).ToHashSet();
        // "conditional" is a citation with a smaller denominator: the claim is tested on every
        // run that produces its material, so its null rate means "no run delegated this pass"
        // rather than "nothing tests this".
        var conditional = EvalSuite.All
            .SelectMany(s => s.IfDelegated.SelectMany(c =>
                c.Claims.Concat(c.Judged.Select(check => check.Claim))))
            .ToHashSet();

        return PromptManifest.Claims.ToDictionary(
            claim => claim.Id,
            claim => cited.Contains(claim.Id)
                ? "cited"
                : judged.Contains(claim.Id)
                    ? "judged"
                    : conditional.Contains(claim.Id)
                        ? "conditional"
                        : ClaimExemptions.Reasons.TryGetValue(claim.Id, out var exemption)
                            ? Spelled(exemption.Kind)
                            : "uncovered");
    }

    private static string Spelled(ExemptionKind kind) => kind switch
    {
        ExemptionKind.NeedsFixture => "needs-fixture",
        _ => kind.ToString().ToLowerInvariant()
    };

    private sealed record TierState(List<ClaimOutcome> Outcomes, List<ScenarioOutcome> Scenarios);
}