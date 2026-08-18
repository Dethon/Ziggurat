using Infrastructure.Agents.ChatClients;
using Shouldly;
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
    public static IReadOnlyList<Scenario> All =>
    [
        .. TimerScenarios.All,
        .. MechanismScenarios.All,
        .. VoiceScenarios.All,
        .. HomeAssistantScenarios.All,
        .. VaultScenarios.All,
        .. MountScenarios.All,
        .. DelegationScenarios.All,
        .. MemoryScenarios.All,
        .. MusicScenarios.All
    ];

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

    // What both tiers do, so that the only thing separating them is the threshold they ask for and
    // the tier they file their scorecard under.
    public static async Task AssertAsync(
        string name, EvalTier tier, Func<Scenario, RunPolicy> policy, RedisFixture redis,
        EvalScorecard scorecard)
    {
        Skip.IfNot(EvalGate.IsArmed, EvalGate.Reason);
        Skip.If(EvalGate.ApiKey is null, "openRouter:apiKey is not set in user secrets");

        var scenario = ByName(name);
        var outcome = await RunAsync(scenario, policy(scenario), redis);

        scorecard.Record(tier, scenario, outcome.Result);
        scorecard.Observe(outcome.Route);

        outcome.Result.Passed.ShouldBeTrue(
            outcome.Failure ?? $"'{name}' failed with no dump written");
    }

    // One run of one scenario, at whichever threshold the tier asks for, with the dump written by
    // the run that failed rather than by the last one taken.
    public static async Task<EvalOutcome> RunAsync(
        Scenario scenario, RunPolicy policy, RedisFixture redis)
    {
        Recording? failed = null;
        ServedRoute? failedRoute = null;
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
                // The route of the run being explained, not of the last run taken: under k of N
                // they are different runs and can be different endpoints.
                failedRoute = recording.Route;
                failures = observed;
            }

            return observed;
        });

        if (result.Passed || failed is null)
        {
            // The raw route travels on: a passing scenario writes nothing, so nothing here needs
            // the provider's name yet and nothing pays a request for it.
            return new EvalOutcome(result, null, route);
        }

        var message = FailureDump.Describe(FailureDump.DefaultDirectory, new FailedRun(
            scenario,
            failed,
            EvalRun.Decorated(scenario),
            await ProviderLookup.ResolveAsync(failedRoute, EvalGate.ApiKey ?? ""),
            [$"passed {result.Passes} of {result.Attempts} runs, needed {policy.K}", .. failures]));

        return new EvalOutcome(result, message, route);
    }
}

public sealed record EvalOutcome(ScenarioResult Result, string? Failure, ServedRoute? Route);