using System.Text.Json;
using Domain.DTOs.Metrics;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Agents.Skills;
using Shouldly;
using Tests.Eval.Scenarios;
using static Tests.Eval.Harness.EvalTools;

namespace Tests.Eval.Harness;

// The eval's half of the ADR 0039 refinement: a trigger claim is met by whichever reader of the
// description loaded the skill, the scorecard says which, and a preload the scenario did not
// permit is as red as a load it did not permit. Deterministic — the recording is built by hand
// from the same observations the deployment publishes.
public class ScenarioChecksPreloadTests
{
    private const string Timers = "countdown-timers";

    private static Scenario RequiringTheLoad() => new()
    {
        Name = "a timer",
        AgentId = "nabu",
        Turn = new EvalTurn { Text = "pon un temporizador de ocho minutos", Sender = "jack" },
        Instant = new DateTimeOffset(2026, 9, 18, 20, 0, 0, TimeSpan.FromHours(2)),
        CallCeiling = 4,
        Required =
        [
            CallExpectation.LoadsSkill(Timers),
            new CallExpectation { Label = "create", Tool = Create, Arguments = [Arg.Is("path", "/timers/pasta/timer.json")] }
        ]
    };

    private static Recording Recorded(IReadOnlyList<string> preloaded, params (string Tool, string Arguments)[] modelCalls)
    {
        var recording = new Recording();
        if (preloaded.Count > 0)
        {
            recording.Publish(new SkillPreloadEvent
            {
                Outcome = SkillPreloadOutcomes.Preloaded,
                Skills = preloaded,
                DurationMs = 380,
                InputTokens = 2200,
                Model = "jev-1.13.0"
            });
        }

        foreach (var (call, index) in modelCalls.Select((c, i) => (c, i)))
        {
            recording.OnInvoked(new ToolInvocation
            {
                Sequence = index + 1,
                ToolName = call.Tool,
                Arguments = call.Arguments,
                Result = "ok",
                Outcome = ToolInvocationOutcome.Completed
            });
        }

        return recording;
    }

    private static string Load(string skill) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { [SkillsProvider.SkillNameParameter] = skill });

    private const string Created = """{"path":"/timers/pasta/timer.json","content":"{}"}""";

    [Fact]
    public void AHostPreloadAndNoModelLoad_MeetsTheRequiredLoad_AndTheLoaderIsTheHost()
    {
        var scenario = RequiringTheLoad();
        var recording = Recorded([Timers], (Create, Created));

        var failures = ScenarioChecks.Failures(scenario, recording);

        failures.ShouldBeEmpty();
        ScenarioChecks.KindOf(scenario, recording, failures).ShouldBeNull();
        ScenarioChecks.LoaderOf(scenario, recording).ShouldBe(Loader.Host);
    }

    [Fact]
    public void AModelLoadAndNoPreload_MeetsTheRequiredLoad_AndTheLoaderIsTheModel()
    {
        var scenario = RequiringTheLoad();
        var recording = Recorded([], (LoadSkill, Load(Timers)), (Create, Created));

        var failures = ScenarioChecks.Failures(scenario, recording);

        failures.ShouldBeEmpty();
        ScenarioChecks.LoaderOf(scenario, recording).ShouldBe(Loader.Model);
    }

    [Fact]
    public void NeitherLoader_IsStillASkillNotLoadedFailure_AndTheLoaderIsNobody()
    {
        var scenario = RequiringTheLoad();
        var recording = Recorded([], (Create, Created));

        var failures = ScenarioChecks.Failures(scenario, recording);

        ScenarioChecks.KindOf(scenario, recording, failures).ShouldBe(FailureKind.SkillNotLoaded);
        ScenarioChecks.LoaderOf(scenario, recording).ShouldBe(Loader.Nobody);
    }

    [Fact]
    public void APreloadOfASkillTheScenarioDoesNotPermit_IsRed_AndTheFailureNamesThePreload()
    {
        var scenario = RequiringTheLoad();
        var recording = Recorded([Timers, "home-assistant"], (Create, Created));

        var failures = ScenarioChecks.Failures(scenario, recording);

        var wrong = failures.ShouldHaveSingleItem();
        wrong.ShouldContain("home-assistant");
        wrong.ShouldContain("preloaded by the host");
        // The host's failure, not the body's: the model read what it was given, so what it did
        // next is no evidence about that skill's prose. Pointing this at ruleIgnored sent the
        // next edit to a body nobody had a complaint about.
        ScenarioChecks.KindOf(scenario, recording, failures).ShouldBe(FailureKind.HostPreloaded);
    }

    [Fact]
    public void AScenarioRequiringNoLoad_HasNoLoader()
    {
        var scenario = RequiringTheLoad() with { Required = [RequiringTheLoad().Required[1]] };

        ScenarioChecks.LoaderOf(scenario, Recorded([Timers], (Create, Created))).ShouldBe(Loader.None);
    }

    [Fact]
    public void APreload_SitsBeforeTheModelsFirstCall_AndSaysWhoPutItThere()
    {
        var recording = Recorded([Timers], (Create, Created));

        var calls = recording.Calls;
        calls.Select(c => c.ToolName).ShouldBe([LoadSkill, Create]);
        calls[0].Sequence.ShouldBeLessThan(0);
        calls[0].Result.ShouldBe(Recording.PreloadedResult);
        recording.IsPreload(calls[0]).ShouldBeTrue();
        recording.IsPreload(calls[1]).ShouldBeFalse();
    }

    // The index read the host made beside the skill is a call the model did not make, so it
    // arrives as the preload did: sequenced below zero, and matching the read every home
    // scenario requires.
    [Fact]
    public void AReadTheHostMade_IsAPreloadedReadCall()
    {
        var recording = new Recording();
        recording.Publish(new SkillPreloadEvent
        {
            Outcome = SkillPreloadOutcomes.Preloaded,
            Skills = ["home-assistant"],
            Reads = ["/ha/setup-index.md"],
            DurationMs = 380,
            InputTokens = 2200,
            Model = "jev-1.13.0"
        });

        var calls = recording.Calls;
        calls.Select(c => c.ToolName).ShouldBe([LoadSkill, Read]);
        calls[1].Sequence.ShouldBeLessThan(0);
        calls[1].Sequence.ShouldBeGreaterThan(calls[0].Sequence);
        calls[1].Arguments.ShouldBe("""{"filePath":"/ha/setup-index.md"}""");
        calls[1].Result.ShouldBe(Recording.PreloadedResult);
        recording.IsPreload(calls[1]).ShouldBeTrue();

        // The read every home scenario requires is met by the host's, as the load is.
        var scenario = RequiringTheLoad() with
        {
            Required = [CallExpectation.LoadsSkill("home-assistant"), HomeAssistantScenarios.ReadsTheSetupIndex]
        };
        ScenarioChecks.Failures(scenario, recording).ShouldBeEmpty();
    }

    [Fact]
    public void APreload_IsSpentAtTheConfiguredPrice_AndNamesTheJudge()
    {
        var recording = Recorded([Timers], (Create, Created));
        recording.Publish(new TokenUsageEvent { Sender = "u", Model = "m", InputTokens = 9000, OutputTokens = 100, Cost = 0.02m });

        var spend = recording.Spend;

        spend.Cost.ShouldBe(0.02m);
        spend.Requests.ShouldBe(1);
        spend.PreloadRequests.ShouldBe(1);
        spend.PreloadInputTokens.ShouldBe(2200);
        spend.PreloadCost.ShouldBe(JevPrice.Of(2200));
        recording.PreloadModel.ShouldBe("jev-1.13.0");
    }

    [Fact]
    public void AJudgmentThatAskedNothing_CostsNothingAndNamesNoJudge()
    {
        var recording = new Recording();
        recording.Publish(new SkillPreloadEvent { Outcome = SkillPreloadOutcomes.SkippedLemonade });

        recording.Spend.PreloadRequests.ShouldBe(0);
        recording.Calls.ShouldBeEmpty();
        recording.PreloadModel.ShouldBeNull();
    }
}