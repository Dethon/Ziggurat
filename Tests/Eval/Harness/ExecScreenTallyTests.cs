using Domain.DTOs;
using Domain.DTOs.Metrics;
using Infrastructure.Agents.ChatClients;
using Shouldly;

namespace Tests.Eval.Harness;

// What the exec screen did across a pass. AutoApproveHandler approves every prompt, so the screen
// changes no scenario's verdict; what it asked about on the scenarios' real sandbox traffic is its
// false-positive rate, and this is where that number is counted. Deterministic harness code, so it
// runs on a bare invocation.
public class ExecScreenTallyTests : IDisposable
{
    private readonly string _output = Directory.CreateTempSubdirectory("eval-screen").FullName;

    public void Dispose() => Directory.Delete(_output, recursive: true);

    private static ExecScreenEvent Screened(string reach, string outcome, int? tokens = 510, decimal? cost = 0.00002m) => new()
    {
        Reach = reach,
        Outcome = outcome,
        InputTokens = tokens,
        Cost = cost,
        Model = "jev-test"
    };

    [Fact]
    public void ARecording_CountsWhatWasScreened_AndWhatWasAsked_ByReach()
    {
        var recording = new Recording();

        recording.Publish(Screened("contained", ExecScreenOutcomes.Ran));
        recording.Publish(Screened("contained", ExecScreenOutcomes.Asked));
        recording.Publish(Screened("host", ExecScreenOutcomes.Asked));
        recording.Publish(Screened("host", ExecScreenOutcomes.Ran));

        recording.Screens.ShouldBe(new ExecScreenTally(Screened: 4, AskedContained: 1, AskedHost: 1));
    }

    // The screen's event counts what it did; what it cost is the judge's own usage event. A call
    // that got no verdict because nothing was sent — no key, the local box — has none.
    [Fact]
    public void AScreen_IsCountedButNotPriced()
    {
        var recording = new Recording();

        recording.Publish(Screened("host", ExecScreenOutcomes.Asked, tokens: null, cost: null));

        recording.Screens.Screened.ShouldBe(1);
        recording.Spend.ShouldBe(Spend.Nothing);
    }

    [Fact]
    public async Task TheRunner_SumsTheScreensOfEveryRunItTook()
    {
        var result = await ScenarioRunner.RunAsync(new RunPolicy(2, 2), _ =>
            Task.FromResult(new RunReading([], []) { Screens = new ExecScreenTally(3, 1, 0) }));

        result.Screens.ShouldBe(new ExecScreenTally(6, 2, 0));
    }

    [Fact]
    public void TheScorecard_ReportsTheScreen_PerScenarioAndPerPass()
    {
        Scorecard.Write(_output, EvalTier.Full, new ServedRoute("m", "p"), [new ClaimOutcome("c", 1, 1)],
        [
            new ScenarioOutcome("a checksum", 2, 2, Spend: Spend.Of("m", new ModelSpend(0.1m, 1_000, null, 10, 2)))
            {
                Screens = new ExecScreenTally(2, 1, 0)
            },
            new ScenarioOutcome("an outpost", 1, 1) { Screens = new ExecScreenTally(1, 0, 1) },
            new ScenarioOutcome("a joke", 1, 1, Spend: Spend.Of("m", new ModelSpend(0.01m, 100, null, 5, 1)))
        ]);

        var card = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, "scorecard-full.json"))).RootElement;

        var checksum = card.GetProperty("scenarios").GetProperty("a checksum");
        checksum.GetProperty("execScreen").GetProperty("screened").GetInt32().ShouldBe(2);
        checksum.GetProperty("execScreen").GetProperty("asked").GetProperty("contained").GetInt32().ShouldBe(1);
        checksum.GetProperty("execScreen").GetProperty("asked").GetProperty("host").GetInt32().ShouldBe(0);
        card.GetProperty("scenarios").GetProperty("a joke").TryGetProperty("execScreen", out _).ShouldBeFalse();

        var pass = card.GetProperty("summary").GetProperty("execScreen");
        pass.GetProperty("screened").GetInt32().ShouldBe(3);
        pass.GetProperty("asked").GetProperty("contained").GetInt32().ShouldBe(1);
        pass.GetProperty("asked").GetProperty("host").GetInt32().ShouldBe(1);
    }
}