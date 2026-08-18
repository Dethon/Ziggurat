using System.Text.Json;
using Domain.DTOs;
using Shouldly;

namespace Tests.Eval.Harness;

// What makes "behaviour got worse after the model bump" a diff rather than an impression. It is
// never committed: a stochastic wobble must not dirty the working tree.
public class ScorecardTests : IDisposable
{
    private readonly string _output = Directory.CreateTempSubdirectory("eval-scorecard").FullName;

    public void Dispose() => Directory.Delete(_output, recursive: true);

    [Fact]
    public void AFullPassWritesOneSummaryNamingTheRouteThatServedIt()
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
    public void AClaimNothingExercisedIsDistinguishableFromOneThatFailed()
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
    public void ASmokeRunDoesNotOverwriteAFullPass()
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