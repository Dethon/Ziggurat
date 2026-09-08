using System.Collections.Concurrent;
using Infrastructure.Agents.ChatClients;
using Shouldly;
using Tests.Eval.Fixtures;
using Tests.Eval.Harness;
using Tests.Eval.Scenarios;

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
        .. WatchScenarios.All,
        .. VaultScenarios.All,
        .. MountScenarios.All,
        .. DelegationScenarios.All,
        .. MemoryScenarios.All,
        .. MusicScenarios.All,
        .. WebScenarios.All
    ];

    public static Scenario ByName(string name) =>
        All.FirstOrDefault(s => s.Name == name)
        ?? throw new InvalidOperationException($"No scenario named '{name}'.");

    public static TheoryData<string> Named(EvalTier tier) =>
        Named(OfTier(tier));

    public static TheoryData<string> Named(EvalTier tier, int shard, int of) =>
        Named(OfTier(tier), shard, of);

    private static IEnumerable<Scenario> OfTier(EvalTier tier) =>
        All.Where(s => tier == EvalTier.Full || s.Tier == tier);

    // Part of a family, for the families too long to be one class. A class's scenarios run one
    // after another, so the longest class is the tail every other class's slots wait on — an
    // eleven-scenario family took longer by itself than the rest of the pass needs. Round-robin,
    // so a scenario added to a split family lands wherever keeps the shards even.
    public static TheoryData<string> Named(IEnumerable<Scenario> family, int shard, int of) =>
        Named(family.Where((_, index) => index % of == shard));

    // One family's worth, for the full-tier classes: xUnit parallelizes across collections and a
    // class is one collection, so a class per family is what puts scenarios in flight at once.
    public static TheoryData<string> Named(IEnumerable<Scenario> family)
    {
        var data = new TheoryData<string>();
        family.Select(s => s.Name).ToList().ForEach(name => data.Add(name));
        return data;
    }

    // What both tiers do, so that the only thing separating them is the threshold they ask for and
    // the tier they file their scorecard under.
    public static async Task AssertAsync(
        string name, EvalTier tier, Func<Scenario, RunPolicy> policy, EvalScorecard scorecard)
    {
        Skip.IfNot(EvalGate.IsArmed, EvalGate.Reason);
        Skip.If(EvalGate.ApiKey is null, "openRouter:apiKey is not set in user secrets");

        var scenario = ByName(name);
        var outcome = await RunAsync(scenario, policy(scenario));

        scorecard.Record(tier, scenario, outcome.Result);
        scorecard.Observe(outcome.Route);

        outcome.Result.Passed.ShouldBeTrue(
            outcome.Failure ?? $"'{name}' failed with no dump written");
    }

    // One run of one scenario, at whichever threshold the tier asks for, with the dump written by
    // the run that failed rather than by the last one taken.
    public static async Task<EvalOutcome> RunAsync(Scenario scenario, RunPolicy policy)
    {
        // The runs are in flight together, so what each one saw is filed under its own number and
        // read back in that order afterwards: "the first failure" has to mean the same run every
        // time, not whichever one happened to come back first.
        var recordings = new ConcurrentDictionary<int, Recording>();
        var observations = new ConcurrentDictionary<int, IReadOnlyList<string>>();

        var result = await ScenarioRunner.RunAsync(policy, async index =>
        {
            var recording = await EvalRun.ExecuteAsync(scenario);
            recordings[index] = recording;
            var observed = ScenarioChecks.Failures(scenario, recording);
            // The judged checks, on runs the deterministic ones pass: a verdict fails the run
            // like any other check, and a judge is never paid to grade a run already red.
            if (observed.Count == 0 && ScenarioChecks.JudgedNow(scenario, recording).Count > 0)
            {
                observed = await Judge.FailuresAsync(scenario, recording, EvalGate.ApiKey ?? "");
            }

            if (observed.Count > 0)
            {
                observations[index] = observed;
            }

            return new RunReading(observed, ScenarioChecks.Exercised(scenario, recording));
        });

        var route = recordings.OrderBy(run => run.Key)
            .Select(run => run.Value.Route)
            .LastOrDefault(served => served is not null);

        if (observations.IsEmpty)
        {
            // The raw route travels on: a clean pass writes nothing, so nothing here needs the
            // provider's name yet and nothing pays a request for it.
            return new EvalOutcome(result, null, route);
        }

        var (first, failures) = observations.OrderBy(run => run.Key).First();
        var failed = recordings[first];
        // The route of the run being explained, not of the last run taken: under k of N they are
        // different runs and can be different endpoints.
        var failedRoute = failed.Route;

        // A pass with a failed run inside it dumps that run under flakes/ and reports nothing;
        // a failed scenario dumps beside the scorecard and the message is the test's failure.
        var message = FailureDump.Describe(FailureDump.DefaultDirectory, new FailedRun(
            scenario,
            failed,
            EvalRun.Decorated(scenario),
            await ProviderLookup.ResolveAsync(failedRoute, EvalGate.ApiKey ?? ""),
            [$"passed {result.Passes} of {result.Attempts} runs, needed {policy.K}", .. failures]),
            result.Passed);

        return new EvalOutcome(result, message, route);
    }
}

public sealed record EvalOutcome(ScenarioResult Result, string? Failure, ServedRoute? Route);