using Domain.DTOs;
using Tests.Eval.Fixtures;
using Tests.Eval.Harness;
using Tests.Eval.Scenarios;
using Tests.Integration.Fixtures;

namespace Tests.Eval;

// Every scenario in the suite, in one list. A scenario that is not here is not run, and the
// coverage test reads the same list — so a claim cannot be reported as covered by a scenario
// nothing executes.
public static class EvalSuite
{
    public static IReadOnlyList<Scenario> All => [.. TimerScenarios.All];

    public static Scenario ByName(string name) =>
        All.FirstOrDefault(s => s.Name == name)
        ?? throw new InvalidOperationException($"No scenario named '{name}'.");

    public static TheoryData<string> Named(EvalTier tier)
    {
        var data = new TheoryData<string>();
        All.Where(s => tier == EvalTier.Full || s.Tier == tier)
            .Select(s => s.Name)
            .ToList()
            .ForEach(name => data.Add(name));

        return data;
    }

    // One run of one scenario, at whichever threshold the tier asks for, with the dump written by
    // the run that failed rather than by the last one taken.
    public static async Task<EvalOutcome> RunAsync(
        Scenario scenario, RunPolicy policy, RedisFixture redis)
    {
        Recording? failed = null;
        ServedRoute? route = null;
        IReadOnlyList<string> failures = [];

        var result = await ScenarioRunner.RunAsync(policy, async () =>
        {
            var recording = await EvalRun.ExecuteAsync(scenario, redis.ConnectionString);
            route = recording.Route ?? route;
            var observed = ScenarioChecks.Failures(scenario, recording);
            if (observed.Count > 0 && failed is null)
            {
                failed = recording;
                failures = observed;
            }

            return observed;
        });

        // Asked for once, and only where something is about to be written that names it.
        route = await ProviderLookup.ResolveAsync(route, EvalGate.ApiKey ?? "");

        if (result.Passed || failed is null)
        {
            return new EvalOutcome(result, null, route);
        }

        failed.OnTurn(new TurnObservation(failed.SystemPrompt, route));

        var message = FailureDump.Describe(
            FailureDump.DefaultDirectory, scenario, failed, EvalRun.Decorated(scenario),
            [$"passed {result.Passes} of {result.Attempts} runs, needed {policy.K}", .. failures]);

        return new EvalOutcome(result, message, route);
    }
}

public sealed record EvalOutcome(ScenarioResult Result, string? Failure, ServedRoute? Route);