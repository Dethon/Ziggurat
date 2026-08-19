using Domain.DTOs;
using Domain.Prompts;
using Infrastructure.Agents.ChatClients;
using Tests.Eval.Fixtures;
using Tests.Eval.Harness;

namespace Tests.Eval;

// Collects what each scenario observed and writes one summary when the tier's run ends. It is a
// class fixture because that is what outlives the theory: a scorecard written per scenario would
// be overwritten by the next one, and a rate per claim only exists once every scenario citing
// that claim has run.
public sealed class EvalScorecard : IDisposable
{
    private readonly List<ClaimOutcome> _outcomes = [];
    private readonly List<ScenarioOutcome> _scenarios = [];
    private readonly Lock _gate = new();
    private EvalTier? _tier;
    private ServedRoute? _route;

    // The tier comes from the run rather than from the scenario: a scenario declares which tier it
    // is a canary for, and a full pass over the canaries would otherwise file itself as a smoke
    // run and overwrite the file a maintainer is about to diff.
    public void Record(EvalTier tier, Scenario scenario, ScenarioResult result)
    {
        lock (_gate)
        {
            _tier = tier;
            _outcomes.AddRange(
                scenario.Claims.Select(claim => new ClaimOutcome(claim, result.Passes, result.Attempts)));
            // The scenario's own rate, cited or not: a guard's drift is only a diff if the guard
            // has a number.
            _scenarios.Add(new ScenarioOutcome(scenario.Name, result.Passes, result.Attempts));
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

    public void Dispose()
    {
        lock (_gate)
        {
            if (_tier is null)
            {
                // Nothing ran — the gate was closed, or every scenario skipped. A scorecard of
                // nothing would look like a pass with no claims, which is worse than no file.
                return;
            }

            // Every declared claim, seeded at nothing, so a claim no scenario exercised appears
            // with a null rate rather than being absent — absent reads as "there is no such
            // claim", which is the one thing the file must not say.
            var seeded = PromptManifest.Claims
                .Select(claim => new ClaimOutcome(claim.Id, 0, 0))
                .Concat(_outcomes)
                .ToList();

            // Scenarios the same way as claims: one that did not run appears at a null rate
            // rather than being absent, so a filtered pass is legible as a filtered pass.
            var scenarios = EvalSuite.All
                .Select(scenario => new ScenarioOutcome(scenario.Name, 0, 0))
                .Concat(_scenarios)
                .ToList();

            // The one place the provider's name is worth a request, and the last place before the
            // file is written. Blocking in a fixture's teardown is the cost of it.
            var route = ProviderLookup
                .ResolveAsync(_route, EvalGate.ApiKey ?? "").GetAwaiter().GetResult();

            Scorecard.Write(FailureDump.DefaultDirectory, _tier.Value, route, seeded, scenarios);
        }
    }
}