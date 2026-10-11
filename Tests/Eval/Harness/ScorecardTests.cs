using System.Text.Json;
using Domain.DTOs;
using Infrastructure.Agents.ChatClients;
using Shouldly;

namespace Tests.Eval.Harness;

// What makes "behaviour got worse after the model bump" a diff rather than an impression. It is
// never committed: a stochastic wobble must not dirty the working tree.
public class ScorecardTests : IDisposable
{
    private readonly string _output = Directory.CreateTempSubdirectory("eval-scorecard").FullName;

    public void Dispose() => Directory.Delete(_output, recursive: true);

    [Fact]
    public void AFullPass_WritesOneSummary_NamingTheRouteThatServedIt()
    {
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("openai/gpt-5.6-luna", "Fireworks"),
        [
            new ClaimOutcome("timers.duration-under-4h", 2, 3),
            new ClaimOutcome("timers.voice-defaults-to-speaking-room", 3, 3)
        ]);

        var summary = Read(Path.Combine(_output, "scorecard-full.json"));

        summary.GetProperty("model").GetString().ShouldBe("openai/gpt-5.6-luna");
        summary.GetProperty("provider").GetString().ShouldBe("Fireworks");
        summary.GetProperty("timestamp").GetString().ShouldNotBeNullOrWhiteSpace();

        var claims = summary.GetProperty("claims");
        claims.GetProperty("timers.duration-under-4h").GetProperty("rate").GetDouble()
            .ShouldBe(2d / 3, 0.001);
        claims.GetProperty("timers.duration-under-4h").GetProperty("passes").GetInt32().ShouldBe(2);
    }

    [Fact]
    public void ARow_SaysWhoLoaded_AndTheSummaryNamesTheJudgeAndItsSpend()
    {
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"),
            [new ClaimOutcome("countdown-timers.loads-for-a-timer-request", 3, 3) { Loaders = [Loader.Host, Loader.Host, Loader.Model] }],
            [
                new ScenarioOutcome("a timer", 3, 3, Spend: Spend.Of("m", new ModelSpend(0.10m, 20_000, 15_000, 400, 4)) + Spend.Of("jev-1.13.0", new ModelSpend(0.0003m, 6_600, null, 90, 3)))
                {
                    Loaders = [Loader.Host, Loader.Host, Loader.Model]
                },
                new ScenarioOutcome("an alarm", 1, 2, SkillNotLoaded: 1, Spend: Spend.Of("m", new ModelSpend(0.30m, 30_000, 15_000, 600, 6)))
                {
                    Loaders = [Loader.Model, Loader.Nobody]
                },
                // Requires no load: contributes nothing to the split.
                new ScenarioOutcome("a joke", 2, 2, Spend: Spend.Of("m", new ModelSpend(0.01m, 1_000, null, 50, 2))) { Loaders = [Loader.None, Loader.None] }
            ],
            preloadModel: "jev-1.13.0");

        var summary = Read(Path.Combine(_output, "scorecard-full.json"));

        summary.GetProperty("preload").GetString().ShouldBe("jev-1.13.0");

        var claim = summary.GetProperty("claims").GetProperty("countdown-timers.loads-for-a-timer-request").GetProperty("loader");
        claim.GetProperty("host").GetInt32().ShouldBe(2);
        claim.GetProperty("model").GetInt32().ShouldBe(1);
        claim.GetProperty("nobody").GetInt32().ShouldBe(0);

        var scenarios = summary.GetProperty("scenarios");
        scenarios.GetProperty("an alarm").GetProperty("loader").GetProperty("nobody").GetInt32().ShouldBe(1);
        scenarios.GetProperty("a joke").TryGetProperty("loader", out _).ShouldBeFalse();

        var timer = scenarios.GetProperty("a timer").GetProperty("spend").GetProperty("byModel").GetProperty("jev-1.13.0");
        timer.GetProperty("cost").GetDecimal().ShouldBe(0.0003m);
        timer.GetProperty("inputTokens").GetInt64().ShouldBe(6_600);
        timer.GetProperty("requests").GetInt32().ShouldBe(3);
        scenarios.GetProperty("an alarm").GetProperty("spend").GetProperty("byModel").TryGetProperty("jev-1.13.0", out _).ShouldBeFalse();

        var pass = summary.GetProperty("summary");
        pass.GetProperty("loader").GetProperty("host").GetInt32().ShouldBe(2);
        pass.GetProperty("loader").GetProperty("model").GetInt32().ShouldBe(2);
        pass.GetProperty("loader").GetProperty("nobody").GetInt32().ShouldBe(1);
        pass.GetProperty("spend").GetProperty("byModel").GetProperty("jev-1.13.0").GetProperty("inputTokens").GetInt64().ShouldBe(6_600);
        pass.GetProperty("spend").GetProperty("byModel").GetProperty("m").GetProperty("requests").GetInt32().ShouldBe(12);
        pass.GetProperty("spend").GetProperty("requests").GetInt32().ShouldBe(15);
    }

    [Fact]
    public void TheSummary_CountsWhatTheJudgeAnswered()
    {
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"), [new ClaimOutcome("c", 1, 1)],
            [
                new ScenarioOutcome("a", 3, 3) { PreloadOutcomes = new Dictionary<string, int> { ["preloaded"] = 2, ["deadline"] = 1 } },
                new ScenarioOutcome("b", 2, 2) { PreloadOutcomes = new Dictionary<string, int> { ["deadline"] = 2 } }
            ]);

        var outcomes = Read(Path.Combine(_output, "scorecard-full.json")).GetProperty("summary").GetProperty("preloadOutcomes");
        outcomes.GetProperty("preloaded").GetInt32().ShouldBe(2);
        outcomes.GetProperty("deadline").GetInt32().ShouldBe(3);
    }

    [Fact]
    public void APassWhoseJudgeNeverAnswered_SaysSo_NotOff()
    {
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"), [new ClaimOutcome("c", 1, 1)],
            [new ScenarioOutcome("a", 1, 1) { PreloadOutcomes = new Dictionary<string, int> { ["deadline"] = 3 } }]);

        Read(Path.Combine(_output, "scorecard-full.json")).GetProperty("preload").GetString().ShouldBe("unanswered");
    }

    [Fact]
    public void APassWithNoJudge_SaysThePreloadIsOff()
    {
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"), [new ClaimOutcome("c", 1, 1)]);

        Read(Path.Combine(_output, "scorecard-full.json")).GetProperty("preload").GetString().ShouldBe("off");
    }

    [Fact]
    public void AScenarioRow_SaysWhatItSpent_AndTheSummaryTotalsThePass()
    {
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"),
            [new ClaimOutcome("timers.duration-under-4h", 2, 2)],
            [
                new ScenarioOutcome("a timer", 2, 2, Spend: Spend.Of("m", new ModelSpend(0.10m, 20_000, 15_000, 400, 4))),
                new ScenarioOutcome("an alarm", 2, 2, Spend: Spend.Of("m", new ModelSpend(0.30m, 30_000, 15_000, 600, 6))),
                // Never ran: no spend key at all, rather than a row that says it cost nothing.
                new ScenarioOutcome("a watch", 0, 0)
            ]);

        var summary = Read(Path.Combine(_output, "scorecard-full.json"));
        var scenarios = summary.GetProperty("scenarios");

        var timer = scenarios.GetProperty("a timer").GetProperty("spend");
        timer.GetProperty("cost").GetDecimal().ShouldBe(0.10m);
        timer.GetProperty("inputTokens").GetInt64().ShouldBe(20_000);
        timer.GetProperty("cachedInputTokens").GetInt64().ShouldBe(15_000);
        timer.GetProperty("outputTokens").GetInt64().ShouldBe(400);
        timer.GetProperty("requests").GetInt32().ShouldBe(4);
        timer.GetProperty("cacheShare").GetDouble().ShouldBe(0.75, 0.001);
        scenarios.GetProperty("a watch").TryGetProperty("spend", out _).ShouldBeFalse();

        // The whole pass in one place, with the two numbers a maintainer acts on: what a run
        // costs on average, and how much of the prompt the provider served from cache.
        var spend = summary.GetProperty("summary").GetProperty("spend");
        spend.GetProperty("cost").GetDecimal().ShouldBe(0.40m);
        spend.GetProperty("costPerRun").GetDecimal().ShouldBe(0.10m);
        spend.GetProperty("cacheShare").GetDouble().ShouldBe(0.6, 0.001);
        spend.GetProperty("requests").GetInt32().ShouldBe(10);
    }

    [Fact]
    public void ASpendNoProviderDetailed_LeavesCacheShareNull()
    {
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"), [],
            [new ScenarioOutcome("a timer", 1, 1, Spend: Spend.Of("m", new ModelSpend(0.10m, 20_000, null, 400, 1)))]);

        var summary = Read(Path.Combine(_output, "scorecard-full.json"));

        summary.GetProperty("scenarios").GetProperty("a timer").GetProperty("spend")
            .GetProperty("cacheShare").ValueKind.ShouldBe(JsonValueKind.Null);
        summary.GetProperty("summary").GetProperty("spend")
            .GetProperty("cacheShare").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void AClaimNothingExercised_IsDistinguishableFromOneThatFailed()
    {
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"),
        [
            new ClaimOutcome("timers.failed", 0, 3),
            new ClaimOutcome("timers.never-run", 0, 0)
        ]);

        var claims = Read(Path.Combine(_output, "scorecard-full.json")).GetProperty("claims");

        claims.GetProperty("timers.failed").GetProperty("rate").GetDouble().ShouldBe(0);
        claims.GetProperty("timers.never-run").GetProperty("rate").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void AClaimRow_SaysHowItIsCovered()
    {
        // "cited", "judged", "guarded", or the exemption kind — so the file itself answers how
        // much of the prompt surface is under test, instead of a null rate meaning three
        // different things.
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"),
            [
                new ClaimOutcome("timers.duration-under-4h", 2, 3),
                new ClaimOutcome("mounts.exec-work-goes-where-exec-lives", 0, 0)
            ],
            coverage: new Dictionary<string, string>
            {
                ["timers.duration-under-4h"] = "cited",
                ["mounts.exec-work-goes-where-exec-lives"] = "guarded"
            });

        var claims = Read(Path.Combine(_output, "scorecard-full.json")).GetProperty("claims");

        claims.GetProperty("timers.duration-under-4h").GetProperty("coverage").GetString()
            .ShouldBe("cited");
        claims.GetProperty("mounts.exec-work-goes-where-exec-lives").GetProperty("coverage").GetString()
            .ShouldBe("guarded");
    }

    [Fact]
    public void AScenarioRow_CarriesItsOwnRate_AndAnUnrunOneIsNull()
    {
        // Guards run and assert but cite nothing, so before this section existed they appeared
        // only as pass/fail tests — drift that stayed above threshold was invisible as a number.
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"),
            [new ClaimOutcome("timers.duration-under-4h", 2, 3)],
            [
                new ScenarioOutcome("a ten-minute reminder is a countdown", 2, 3),
                new ScenarioOutcome("a scenario nothing ran", 0, 0)
            ]);

        var scenarios = Read(Path.Combine(_output, "scorecard-full.json")).GetProperty("scenarios");

        var ran = scenarios.GetProperty("a ten-minute reminder is a countdown");
        ran.GetProperty("passes").GetInt32().ShouldBe(2);
        ran.GetProperty("runs").GetInt32().ShouldBe(3);
        ran.GetProperty("rate").GetDouble().ShouldBe(2d / 3, 0.001);
        scenarios.GetProperty("a scenario nothing ran").GetProperty("rate").ValueKind
            .ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void ARun_SummarisesItselfAsAWhole()
    {
        // The per-row sections answer "which claim moved"; the summary answers "did the run get
        // better or worse" without the reader summing rows by hand across two files.
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"),
            [
                new ClaimOutcome("timers.duration-under-4h", 2, 3),
                new ClaimOutcome("timers.voice-defaults-to-speaking-room", 3, 3),
                new ClaimOutcome("mounts.never-run", 0, 0)
            ],
            [
                new ScenarioOutcome("a ten-minute reminder is a countdown", 2, 3),
                new ScenarioOutcome("a scenario nothing ran", 0, 0)
            ]);

        var summary = Read(Path.Combine(_output, "scorecard-full.json")).GetProperty("summary");

        var claims = summary.GetProperty("claims");
        claims.GetProperty("declared").GetInt32().ShouldBe(3);
        claims.GetProperty("exercised").GetInt32().ShouldBe(2);
        claims.GetProperty("passes").GetInt32().ShouldBe(5);
        claims.GetProperty("runs").GetInt32().ShouldBe(6);
        claims.GetProperty("rate").GetDouble().ShouldBe(5d / 6, 0.001);

        var scenarios = summary.GetProperty("scenarios");
        scenarios.GetProperty("declared").GetInt32().ShouldBe(2);
        scenarios.GetProperty("exercised").GetInt32().ShouldBe(1);
        scenarios.GetProperty("passes").GetInt32().ShouldBe(2);
        scenarios.GetProperty("runs").GetInt32().ShouldBe(3);
        scenarios.GetProperty("rate").GetDouble().ShouldBe(2d / 3, 0.001);
    }

    // A model is picked per agent, and a voice agent's scenarios ask for different things than a
    // text agent's: one rate over both hides which of them a model suits. Haiku 5.5 on 2026-10-10
    // read 94.9% overall, and only a hand-written split showed voice and text apart.
    [Fact]
    public void TheSummary_RatesEachAgentsScenariosOnTheirOwn()
    {
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"), [new ClaimOutcome("c", 1, 1)],
        [
            new ScenarioOutcome("a spoken timer", 2, 3, Spend: Spend.Of("m", new ModelSpend(0.3m, 1_000, null, 10, 3)))
            {
                Agent = "nabu"
            },
            new ScenarioOutcome("a spoken search", 4, 4) { Agent = "nabu" },
            new ScenarioOutcome("a written note", 0, 0) { Agent = "jonas" },
            new ScenarioOutcome("a written note", 1, 3) { Agent = "jonas" },
            new ScenarioOutcome("a note nothing ran", 0, 0) { Agent = "jonas" }
        ]);

        var card = Read(Path.Combine(_output, "scorecard-full.json"));
        var byAgent = card.GetProperty("summary").GetProperty("byAgent");

        var nabu = byAgent.GetProperty("nabu");
        nabu.GetProperty("declared").GetInt32().ShouldBe(2);
        nabu.GetProperty("exercised").GetInt32().ShouldBe(2);
        nabu.GetProperty("passes").GetInt32().ShouldBe(6);
        nabu.GetProperty("runs").GetInt32().ShouldBe(7);
        nabu.GetProperty("rate").GetDouble().ShouldBe(6d / 7, 0.001);
        nabu.GetProperty("spend").GetProperty("cost").GetDecimal().ShouldBe(0.3m);

        var jonas = byAgent.GetProperty("jonas");
        jonas.GetProperty("declared").GetInt32().ShouldBe(2);
        jonas.GetProperty("exercised").GetInt32().ShouldBe(1);
        jonas.GetProperty("passes").GetInt32().ShouldBe(1);
        jonas.GetProperty("runs").GetInt32().ShouldBe(3);
        jonas.GetProperty("rate").GetDouble().ShouldBe(1d / 3, 0.001);

        card.GetProperty("scenarios").GetProperty("a spoken timer").GetProperty("agent").GetString().ShouldBe("nabu");
    }

    [Fact]
    public void AGradedScenario_CarriesEachDimensionsMean_AndThePassCarriesTheScore()
    {
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"), [new ClaimOutcome("c", 1, 1)],
        [
            new ScenarioOutcome("two totals", 2, 2)
            {
                Agent = "jonas",
                Grades =
                [
                    new GradeMark("accuracy", 10), new GradeMark("attribution", 6, "one figure unsourced"),
                    new GradeMark("accuracy", 5), new GradeMark("attribution", 8, "sourced")
                ]
            },
            new ScenarioOutcome("an absent answer", 1, 1)
            {
                Agent = "jonas",
                Grades = [new GradeMark("accuracy", 0)]
            },
            new ScenarioOutcome("an ungraded scenario", 1, 1) { Agent = "nabu" }
        ]);

        var card = Read(Path.Combine(_output, "scorecard-full.json"));

        var row = card.GetProperty("scenarios").GetProperty("two totals").GetProperty("grades");
        row.GetProperty("accuracy").GetProperty("mean").GetDouble().ShouldBe(7.5, 0.001);
        row.GetProperty("accuracy").GetProperty("runs").GetInt32().ShouldBe(2);
        row.GetProperty("attribution").GetProperty("mean").GetDouble().ShouldBe(7, 0.001);
        row.GetProperty("attribution").GetProperty("reasons").EnumerateArray()
            .Select(r => r.GetString()).ShouldBe(["6: one figure unsourced", "8: sourced"]);
        // The scenario's score is the mean of its dimensions, not of its marks: a dimension the
        // judge skipped once must not weigh less than one it answered every time.
        card.GetProperty("scenarios").GetProperty("two totals").GetProperty("score").GetDouble()
            .ShouldBe(7.25, 0.001);
        card.GetProperty("scenarios").GetProperty("an ungraded scenario").TryGetProperty("grades", out _)
            .ShouldBeFalse();

        // The pass's score is the mean of its scenarios' scores: one long scenario with many
        // runs must not outvote the rest.
        var grades = card.GetProperty("summary").GetProperty("grades");
        grades.GetProperty("score").GetDouble().ShouldBe((7.25 + 0) / 2, 0.001);
        grades.GetProperty("scenarios").GetInt32().ShouldBe(2);
        grades.GetProperty("byDimension").GetProperty("accuracy").GetDouble().ShouldBe((7.5 + 0) / 2, 0.001);
        grades.GetProperty("byDimension").GetProperty("attribution").GetDouble().ShouldBe(7, 0.001);

        card.GetProperty("summary").GetProperty("byAgent").GetProperty("jonas")
            .GetProperty("grades").GetProperty("score").GetDouble().ShouldBe(3.625, 0.001);
        card.GetProperty("summary").GetProperty("byAgent").GetProperty("nabu")
            .TryGetProperty("grades", out _).ShouldBeFalse();
    }

    [Fact]
    public void AScenarioWithRealWorkers_SaysHowManyOfItsRunsDelegated()
    {
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"), [new ClaimOutcome("c", 1, 1)],
        [
            new ScenarioOutcome("a briefing", 3, 3) { DelegatedRuns = 0 },
            new ScenarioOutcome("a timer", 1, 1)
        ]);

        var scenarios = Read(Path.Combine(_output, "scorecard-full.json")).GetProperty("scenarios");

        // Zero is said out loud where a worker could have run: "nobody delegated" is what makes
        // the scenario's score a score of the in-place route alone.
        scenarios.GetProperty("a briefing").GetProperty("delegatedRuns").GetInt32().ShouldBe(0);
        scenarios.GetProperty("a timer").TryGetProperty("delegatedRuns", out _).ShouldBeFalse();
    }

    // A family that mixes rungs every strong model clears with rungs few do has one mean that
    // says little: the easy ones hold it up and the spread between two models is whatever the
    // hard ones leave of it. The score once per group is what a comparison reads.
    [Fact]
    public void GradedScenariosInGroups_AreScoredOncePerGroup()
    {
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"), [new ClaimOutcome("c", 1, 1)],
        [
            new ScenarioOutcome("an easy one", 3, 3) { Group = "base", Grades = [new GradeMark("accuracy", 10)] },
            new ScenarioOutcome("another easy one", 3, 3) { Group = "base", Grades = [new GradeMark("accuracy", 9)] },
            new ScenarioOutcome("a hard one", 1, 3) { Group = "hard", Grades = [new GradeMark("accuracy", 4)] }
        ]);

        var card = Read(Path.Combine(_output, "scorecard-full.json"));
        var groups = card.GetProperty("summary").GetProperty("grades").GetProperty("byGroup");

        groups.GetProperty("base").GetProperty("score").GetDouble().ShouldBe(9.5, 0.001);
        groups.GetProperty("base").GetProperty("scenarios").GetInt32().ShouldBe(2);
        groups.GetProperty("hard").GetProperty("score").GetDouble().ShouldBe(4, 0.001);
        card.GetProperty("scenarios").GetProperty("a hard one").GetProperty("group").GetString().ShouldBe("hard");
    }

    // What an answer cost in calls, beside what it scored: two models with the same marks are
    // not the same model if one of them read the whole site to get there.
    [Fact]
    public void AScenarioWithAReportedCeiling_CarriesItsCallsPerRun_AndThePassCarriesTheMean()
    {
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"), [new ClaimOutcome("c", 1, 1)],
        [
            new ScenarioOutcome("a thorough one", 3, 3) { Group = "scale", Calls = [12, 40, 44], CallCeiling = 30 },
            new ScenarioOutcome("a quick one", 3, 3) { Group = "base", Calls = [6, 6, 6], CallCeiling = 12 },
            new ScenarioOutcome("a timer", 1, 1)
        ]);

        var card = Read(Path.Combine(_output, "scorecard-full.json"));
        var row = card.GetProperty("scenarios").GetProperty("a thorough one").GetProperty("calls");
        row.GetProperty("perRun").GetDouble().ShouldBe(32, 0.001);
        row.GetProperty("ceiling").GetInt32().ShouldBe(30);
        row.GetProperty("over").GetInt32().ShouldBe(2);
        card.GetProperty("scenarios").GetProperty("a timer").TryGetProperty("calls", out _).ShouldBeFalse();

        var calls = card.GetProperty("summary").GetProperty("calls");
        calls.GetProperty("perRun").GetDouble().ShouldBe(19, 0.001);
        calls.GetProperty("overCeiling").GetInt32().ShouldBe(2);
        calls.GetProperty("byGroup").GetProperty("scale").GetDouble().ShouldBe(32, 0.001);
    }

    [Fact]
    public void APassWithNothingGraded_CarriesNoGrades()
    {
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"), [new ClaimOutcome("c", 1, 1)],
            [new ScenarioOutcome("a timer", 1, 1)]);

        Read(Path.Combine(_output, "scorecard-full.json")).GetProperty("summary")
            .TryGetProperty("grades", out _).ShouldBeFalse();
    }

    // The research tier's diff is across models, where every other tier's is across time: one
    // file per model is what lets two of them sit side by side, and what stops the second
    // model's pass from overwriting the first's.
    [Fact]
    public void AResearchPass_IsFiledUnderTheModelThatServedIt()
    {
        var path = Scorecard.Write(
            _output, EvalTier.Research, new ServedRoute("anthropic/claude-haiku-5.5", "Anthropic"),
            [new ClaimOutcome("c", 1, 1)]);

        Path.GetFileName(path).ShouldBe("scorecard-research-anthropic-claude-haiku-5.5.json");
        Path.GetFileName(Scorecard.Write(_output, EvalTier.Research, null, [new ClaimOutcome("c", 1, 1)]))
            .ShouldBe("scorecard-research.json");
    }

    [Fact]
    public void AScenarioThatNamesNoAgent_IsInNoAgentsRate()
    {
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"), [new ClaimOutcome("c", 1, 1)],
            [new ScenarioOutcome("a harness row", 1, 1)]);

        var card = Read(Path.Combine(_output, "scorecard-full.json"));

        card.GetProperty("summary").GetProperty("byAgent").EnumerateObject().ShouldBeEmpty();
        card.GetProperty("scenarios").GetProperty("a harness row").TryGetProperty("agent", out _).ShouldBeFalse();
    }

    [Fact]
    public void ARunWhereNothingRan_SummarisesAtANullRate()
    {
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"),
            [new ClaimOutcome("timers.never-run", 0, 0)]);

        var summary = Read(Path.Combine(_output, "scorecard-full.json")).GetProperty("summary");

        summary.GetProperty("claims").GetProperty("rate").ValueKind.ShouldBe(JsonValueKind.Null);
        summary.GetProperty("scenarios").GetProperty("rate").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public void ASmokeRun_DoesNotOverwriteAFullPass()
    {
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("full-model", null),
            [new ClaimOutcome("timers.duration-under-4h", 3, 3)]);
        Scorecard.Write(_output, EvalTier.Smoke, new ServedRoute("smoke-model", null),
            [new ClaimOutcome("timers.duration-under-4h", 1, 1)]);

        Read(Path.Combine(_output, "scorecard-full.json")).GetProperty("model").GetString()
            .ShouldBe("full-model");
        Read(Path.Combine(_output, "scorecard-smoke.json")).GetProperty("model").GetString()
            .ShouldBe("smoke-model");
    }

    private static JsonElement Read(string path) =>
        JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();

    // Jev is paid before the model answers, so a scenario whose provider failed before its first
    // response still spent something. Gating the spend on model requests alone dropped those
    // judgments off the row and out of the pass total, under-reporting what the pass paid.
    [Fact]
    public void AScenarioThatOnlyPaidTheJudge_StillCarriesItsSpend()
    {
        var judgedOnly = Spend.Of("jev", new ModelSpend(0.0004m, 2200, null, 120, 2));

        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"),
            [new ClaimOutcome("timers.sets-a-timer", 0, 2)],
            [new ScenarioOutcome("a timer to set", 0, 2, Spend: judgedOnly)]);

        var summary = Read(Path.Combine(_output, "scorecard-full.json"));

        var row = summary.GetProperty("scenarios").GetProperty("a timer to set")
            .GetProperty("spend").GetProperty("byModel").GetProperty("jev");
        row.GetProperty("requests").GetInt32().ShouldBe(2);
        row.GetProperty("inputTokens").GetInt64().ShouldBe(2200);

        summary.GetProperty("summary").GetProperty("spend").GetProperty("byModel").GetProperty("jev")
            .GetProperty("requests").GetInt32().ShouldBe(2);
    }

    // A red row says which kind of red it was, per scenario and per claim, so the reader knows
    // whether to edit a skill's description or its body without opening the dump.
    [Fact]
    public void ARedRow_CarriesTheFailureKinds_AndAGreenRowCarriesNone()
    {
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"),
            [
                new ClaimOutcome("home-watches.loads-for-a-watch-request", 1, 3, SkillNotLoaded: 2),
                new ClaimOutcome("home-watches.range-is-two-triggers", 2, 3, RuleIgnored: 1),
                new ClaimOutcome("timers.duration-under-4h", 3, 3)
            ],
            [new ScenarioOutcome("a range to leave", 2, 3, RuleIgnored: 1)]);

        var summary = Read(Path.Combine(_output, "scorecard-full.json"));
        var claims = summary.GetProperty("claims");

        claims.GetProperty("home-watches.loads-for-a-watch-request").GetProperty("failures")
            .GetProperty("skillNotLoaded").GetInt32().ShouldBe(2);
        claims.GetProperty("home-watches.range-is-two-triggers").GetProperty("failures")
            .GetProperty("ruleIgnored").GetInt32().ShouldBe(1);
        claims.GetProperty("timers.duration-under-4h").TryGetProperty("failures", out _).ShouldBeFalse();
        summary.GetProperty("scenarios").GetProperty("a range to leave").GetProperty("failures")
            .GetProperty("ruleIgnored").GetInt32().ShouldBe(1);
    }

    // A skill the host preloaded that the scenario does not permit is neither half of a skill's
    // prose. It reaches the row as its own kind, so the reader is not sent to a body nobody has a
    // complaint about — and every kind is reported off the run's own list, so one added to the
    // enum appears here without another count to remember.
    [Fact]
    public void ARowWhoseHostPreloadedAWrongSkill_NamesThePreload_NotTheBody()
    {
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"),
            [
                new ClaimOutcome("timers.sets-a-timer", 1, 3)
                {
                    Kinds = [FailureKind.HostPreloaded, FailureKind.RuleIgnored]
                }
            ],
            [
                new ScenarioOutcome("a timer to set", 1, 3)
                {
                    Kinds = [FailureKind.HostPreloaded, FailureKind.HostPreloaded]
                }
            ]);

        var summary = Read(Path.Combine(_output, "scorecard-full.json"));

        var claim = summary.GetProperty("claims").GetProperty("timers.sets-a-timer").GetProperty("failures");
        claim.GetProperty("hostPreloaded").GetInt32().ShouldBe(1);
        claim.GetProperty("ruleIgnored").GetInt32().ShouldBe(1);

        var scenario = summary.GetProperty("scenarios").GetProperty("a timer to set").GetProperty("failures");
        scenario.GetProperty("hostPreloaded").GetInt32().ShouldBe(2);
        scenario.TryGetProperty("ruleIgnored", out _).ShouldBeFalse();
    }
}