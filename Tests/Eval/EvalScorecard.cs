using Infrastructure.Agents.ChatClients;
using Tests.Eval.Fixtures;
using Tests.Eval.Harness;

namespace Tests.Eval;

// The class fixture each scenario class takes. Every instance fronts the same ledger, because the
// scenario classes run as parallel collections and a scorecard per class would leave whichever
// class disposed last as the whole run. Each dispose rewrites the file from everything recorded
// so far; the last one to finish writes the complete pass.
public sealed class EvalScorecard : IDisposable
{
    private static readonly ScorecardLedger _ledger = new();

    // The tier comes from the run rather than from the scenario: a scenario declares which tier it
    // is a canary for, and a full pass over the canaries would otherwise file itself as a smoke
    // run and overwrite the file a maintainer is about to diff.
    public void Record(EvalTier tier, Scenario scenario, ScenarioResult result) =>
        _ledger.Record(tier, scenario, result);

    public void Observe(ServedRoute? route) => _ledger.Observe(route);

    // The one place the provider's name is worth a request, and the last place before the file is
    // written. Blocking in a fixture's teardown is the cost of it.
    public void Dispose() => _ledger.WriteAll(
        FailureDump.DefaultDirectory,
        route => ProviderLookup.ResolveAsync(route, EvalGate.ApiKey ?? ""));
}