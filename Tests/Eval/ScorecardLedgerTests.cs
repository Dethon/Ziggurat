using System.Text.Json;
using Infrastructure.Agents.ChatClients;
using Shouldly;
using Tests.Eval.Harness;

namespace Tests.Eval;

// The scenario classes run as parallel collections, each disposing its own fixture whenever its
// last theory finishes — so the file must be a rewrite of everything recorded so far, never the
// disposer's own slice.
public class ScorecardLedgerTests : IDisposable
{
    private readonly string _output = Directory.CreateTempSubdirectory("eval-ledger").FullName;
    private readonly ScorecardLedger _ledger = new();

    private static readonly Scenario _first = EvalSuite.All.First();
    private static readonly Scenario _last = EvalSuite.All.Last();

    public void Dispose() => Directory.Delete(_output, recursive: true);

    [Fact]
    public void RecordingsFromTwoFixtures_LandInOneFile()
    {
        _ledger.Record(EvalTier.Full, _first, new ScenarioResult(true, 2, 3, []));
        _ledger.WriteAll(_output, Unresolved);

        _ledger.Record(EvalTier.Full, _last, new ScenarioResult(true, 1, 1, []));
        _ledger.WriteAll(_output, Unresolved);

        var scenarios = Read("scorecard-full.json").GetProperty("scenarios");

        scenarios.GetProperty(_first.Name).GetProperty("runs").GetInt32().ShouldBe(3);
        scenarios.GetProperty(_last.Name).GetProperty("runs").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void NothingRecorded_WritesNoFile()
    {
        _ledger.WriteAll(_output, Unresolved);

        Directory.EnumerateFiles(_output).ShouldBeEmpty();
    }

    [Fact]
    public void EachTier_FilesUnderItsOwnName()
    {
        _ledger.Record(EvalTier.Full, _first, new ScenarioResult(true, 3, 3, []));
        _ledger.Record(EvalTier.Smoke, _first, new ScenarioResult(true, 1, 1, []));
        _ledger.WriteAll(_output, Unresolved);

        Read("scorecard-full.json").GetProperty("scenarios").GetProperty(_first.Name)
            .GetProperty("runs").GetInt32().ShouldBe(3);
        Read("scorecard-smoke.json").GetProperty("scenarios").GetProperty(_first.Name)
            .GetProperty("runs").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void AClaimNoScenarioRan_StillAppears_AtANullRate()
    {
        _ledger.Record(EvalTier.Full, _first, new ScenarioResult(true, 3, 3, []));
        _ledger.WriteAll(_output, Unresolved);

        var claims = Read("scorecard-full.json").GetProperty("claims");
        var untouched = EvalSuite.All.SelectMany(s => s.Claims).First(c => !_first.Claims.Contains(c));

        claims.GetProperty(untouched).GetProperty("rate").ValueKind.ShouldBe(JsonValueKind.Null);
        claims.GetProperty(untouched).GetProperty("coverage").GetString().ShouldBe("cited");
    }

    [Fact]
    public void AConditionalClaim_IsTalliedOverItsOwnRuns_NotTheScenarios()
    {
        _ledger.Record(EvalTier.Full, _first, new ScenarioResult(true, 3, 3, [])
        {
            Conditionals = [new ClaimOutcome("subagents.prompt-is-self-contained", 1, 2)]
        });
        _ledger.WriteAll(_output, Unresolved);

        var claim = Read("scorecard-full.json").GetProperty("claims")
            .GetProperty("subagents.prompt-is-self-contained");
        claim.GetProperty("runs").GetInt32().ShouldBe(2);
        claim.GetProperty("passes").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void AConditionallyCitedClaim_IsCoveredAsConditional()
    {
        // "conditional" is what tells the reader a null rate means "no run delegated this pass"
        // rather than "nothing tests this": the claim is wired, its denominator is just not
        // every run the scenario took.
        _ledger.Record(EvalTier.Full, _first, new ScenarioResult(true, 1, 1, []));
        _ledger.WriteAll(_output, Unresolved);

        Read("scorecard-full.json").GetProperty("claims")
            .GetProperty("subagents.prompt-is-self-contained")
            .GetProperty("coverage").GetString().ShouldBe("conditional");
    }

    [Fact]
    public void ALaterWrite_HandsTheResolverTheRouteAlreadyResolved()
    {
        // One paid lookup per run: the resolved route is kept, so every dispose after the first
        // presents a route whose provider is already known and the lookup short-circuits.
        var seen = new List<ServedRoute?>();

        _ledger.Record(EvalTier.Full, _first, new ScenarioResult(true, 3, 3, []));
        _ledger.Observe(new ServedRoute("m", null, "gen-1"));

        _ledger.WriteAll(_output, route =>
        {
            seen.Add(route);
            return Task.FromResult<ServedRoute?>(route! with { Provider = "Fireworks" });
        });
        _ledger.WriteAll(_output, route =>
        {
            seen.Add(route);
            return Task.FromResult(route);
        });

        seen[0]!.Provider.ShouldBeNull();
        seen[1]!.Provider.ShouldBe("Fireworks");
        Read("scorecard-full.json").GetProperty("provider").GetString().ShouldBe("Fireworks");
    }

    private static Task<ServedRoute?> Unresolved(ServedRoute? route) => Task.FromResult(route);

    private JsonElement Read(string file) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(_output, file))).RootElement.Clone();
}