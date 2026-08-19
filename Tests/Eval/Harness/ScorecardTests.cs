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
        // "cited", "judged", or the exemption kind — so the file itself answers how much of the
        // prompt surface is under test, instead of a null rate meaning three different things.
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"),
            [
                new ClaimOutcome("timers.duration-under-4h", 2, 3),
                new ClaimOutcome("mounts.exec-work-goes-where-exec-lives", 0, 0)
            ],
            coverage: new Dictionary<string, string>
            {
                ["timers.duration-under-4h"] = "cited",
                ["mounts.exec-work-goes-where-exec-lives"] = "needs-fixture"
            });

        var claims = Read(Path.Combine(_output, "scorecard-full.json")).GetProperty("claims");

        claims.GetProperty("timers.duration-under-4h").GetProperty("coverage").GetString()
            .ShouldBe("cited");
        claims.GetProperty("mounts.exec-work-goes-where-exec-lives").GetProperty("coverage").GetString()
            .ShouldBe("needs-fixture");
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
}