using Shouldly;

namespace Tests.Eval.Harness;

// A stochastic failure cannot be reproduced by re-running it, so a failed run writes everything
// needed to understand it. What is being pinned here is that "everything" is literally everything
// the ticket lists: a dump missing one of them sends somebody back to a run that no longer exists.
public class FailureDumpTests : IDisposable
{
    private readonly string _output = Directory.CreateTempSubdirectory("eval-dump").FullName;

    public void Dispose() => Directory.Delete(_output, recursive: true);

    [Fact]
    public async Task AFailedRun_WritesEverythingNeededToUnderstandIt()
    {
        var recording = await ScriptedTurn.RunAsync(
            "Listo, ocho minutos.",
            ScriptedTurn.Call("domain__filesystem_create", "/schedules/pasta/task.json"));

        var scenario = Scenario();
        var path = FailureDump.Write(_output, new FailedRun(
            scenario, recording, TurnText(scenario), recording.Route,
            ["unnecessary call: /schedules"]));

        var dump = await File.ReadAllTextAsync(path);

        dump.ShouldContain("scripted system prompt");
        dump.ShouldContain("scripted/model");
        dump.ShouldContain("Scripted");
        dump.ShouldContain("Message from jack (in kitchen via kitchen-1)");
        dump.ShouldContain("domain__filesystem_create");
        dump.ShouldContain("/schedules/pasta/task.json");
        dump.ShouldContain("Listo, ocho minutos.");
        dump.ShouldContain("unnecessary call: /schedules");
    }

    [Fact]
    public async Task ThePathOfTheDump_IsWhatTheFailureMessageSays()
    {
        var recording = await ScriptedTurn.RunAsync("listo");
        var scenario = Scenario();

        var message = FailureDump
            .Describe(_output, new FailedRun(
                scenario, recording, TurnText(scenario), recording.Route,
                ["required call 'create' never happened"]))
            .ShouldNotBeNull();

        message.ShouldContain("required call 'create' never happened");
        var path = message.Split('\n').First(l => l.Contains(_output)).Trim();
        File.Exists(path).ShouldBeTrue();
    }

    [Fact]
    public async Task APassingScenario_WritesNothing()
    {
        var recording = await ScriptedTurn.RunAsync("listo");

        FailureDump
            .Describe(_output, new FailedRun(Scenario(), recording, "turn", recording.Route, []))
            .ShouldBeNull();

        Directory.GetFiles(_output).ShouldBeEmpty();
    }

    [Fact]
    public async Task AFlakedRun_IsKeptUnderFlakes_AndFailsNothing()
    {
        // The scenario passed its k of N, so nothing must red the suite — but the run that
        // failed is as irrecoverable as any other, and a chronic two-of-three is diagnosed
        // from these instead of from another armed run.
        var recording = await ScriptedTurn.RunAsync("listo");

        FailureDump
            .Describe(_output, new FailedRun(
                Scenario(), recording, "turn", recording.Route,
                ["passed 2 of 3 runs, needed 2", "unnecessary call: web_search"]),
                passed: true)
            .ShouldBeNull();

        var flake = Directory.GetFiles(Path.Combine(_output, "flakes")).ShouldHaveSingleItem();
        var dump = await File.ReadAllTextAsync(flake);
        dump.ShouldContain("passed 2 of 3 runs");
        dump.ShouldContain("unnecessary call: web_search");
    }

    [Fact]
    public async Task AFailedScenario_StillDumpsBesideTheScorecard()
    {
        var recording = await ScriptedTurn.RunAsync("listo");

        FailureDump
            .Describe(_output, new FailedRun(
                Scenario(), recording, "turn", recording.Route,
                ["required call 'create' never happened"]),
                passed: false)
            .ShouldNotBeNull();

        Directory.GetFiles(_output).ShouldHaveSingleItem();
    }

    [Fact]
    public void TheOutputDirectory_IsGitIgnored()
    {
        // The dumps and the scorecard land in one place, and a stochastic wobble must never dirty
        // the working tree — a run that reds the suite must not also make it look like an edit.
        var root = Directory.GetParent(FailureDump.DefaultDirectory)!.FullName;

        File.ReadAllLines(Path.Combine(root, ".gitignore"))
            .ShouldContain(Path.GetFileName(FailureDump.DefaultDirectory) + "/");
    }

    private static string TurnText(Scenario scenario) =>
        $"[Current time: {scenario.Instant:yyyy-MM-dd HH:mm:ss zzz}] " +
        "Message from jack (in kitchen via kitchen-1):\npon un temporizador de ocho minutos";

    private static Scenario Scenario() => new()
    {
        Name = "eight-minute pasta timer",
        AgentId = "nabu",
        Turn = new EvalTurn
        {
            Text = "pon un temporizador de ocho minutos",
            Sender = "jack",
            Room = "kitchen",
            SatelliteId = "kitchen-1"
        },
        Instant = new DateTimeOffset(2026, 8, 18, 20, 0, 0, TimeSpan.FromHours(2)),
        CallCeiling = 3
    };
}