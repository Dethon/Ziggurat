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
                new ScenarioOutcome("a timer", 3, 3, Spend: new Spend(0.10m, 20_000, 15_000, 400, 4, 0.0003m, 6_600, 3))
                {
                    Loaders = [Loader.Host, Loader.Host, Loader.Model]
                },
                new ScenarioOutcome("an alarm", 1, 2, SkillNotLoaded: 1, Spend: new Spend(0.30m, 30_000, 15_000, 600, 6))
                {
                    Loaders = [Loader.Model, Loader.Nobody]
                },
                // Requires no load: contributes nothing to the split.
                new ScenarioOutcome("a joke", 2, 2, Spend: new Spend(0.01m, 1_000, null, 50, 2)) { Loaders = [Loader.None, Loader.None] }
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

        var timer = scenarios.GetProperty("a timer").GetProperty("spend").GetProperty("preload");
        timer.GetProperty("cost").GetDecimal().ShouldBe(0.0003m);
        timer.GetProperty("inputTokens").GetInt64().ShouldBe(6_600);
        timer.GetProperty("requests").GetInt32().ShouldBe(3);
        scenarios.GetProperty("an alarm").GetProperty("spend").TryGetProperty("preload", out _).ShouldBeFalse();

        var pass = summary.GetProperty("summary");
        pass.GetProperty("loader").GetProperty("host").GetInt32().ShouldBe(2);
        pass.GetProperty("loader").GetProperty("model").GetInt32().ShouldBe(2);
        pass.GetProperty("loader").GetProperty("nobody").GetInt32().ShouldBe(1);
        pass.GetProperty("spend").GetProperty("preload").GetProperty("inputTokens").GetInt64().ShouldBe(6_600);
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
                new ScenarioOutcome("a timer", 2, 2, Spend: new Spend(0.10m, 20_000, 15_000, 400, 4)),
                new ScenarioOutcome("an alarm", 2, 2, Spend: new Spend(0.30m, 30_000, 15_000, 600, 6)),
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
            [new ScenarioOutcome("a timer", 1, 1, Spend: new Spend(0.10m, 20_000, null, 400, 1))]);

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
}