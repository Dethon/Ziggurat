using Shouldly;

namespace Tests.Eval.Harness;

// The harness's own checks, proven deterministically. A broken check makes the whole suite green
// and worthless, so each one is shown failing on the thing it exists to catch and passing on the
// thing it must tolerate.
public class ScenarioChecksTests
{
    private const string Create = "domain__filesystem_create";
    private const string Read = "domain__filesystem_read";
    private const string Remove = "domain__filesystem_remove";
    private const string Glob = "domain__filesystem_glob";

    private static Scenario Timer() => new()
    {
        Name = "eight-minute pasta timer",
        AgentId = "nabu",
        Turn = new EvalTurn { Text = "pon un temporizador de ocho minutos", Sender = "jack" },
        Instant = new DateTimeOffset(2026, 8, 18, 20, 0, 0, TimeSpan.FromHours(2)),
        CallCeiling = 4,
        Required =
        [
            new CallExpectation
            {
                Label = "create",
                Tool = Create,
                Arguments = [Arg.Is("path", "/timers/pasta/timer.json")]
            }
        ]
    };

    [Fact]
    public async Task ACallOutsideRequiredPlusPermitted_FailsAndNamesIt()
    {
        var recording = await ScriptedTurn.RunAsync(
            "listo",
            ScriptedTurn.Call(Create, "/timers/pasta/timer.json"),
            ScriptedTurn.Call(Create, "/schedules/pasta/task.json"));

        var failures = ScenarioChecks.Failures(Timer(), recording);

        failures.ShouldHaveSingleItem().ShouldContain("/schedules/pasta/task.json");
    }

    [Fact]
    public async Task AnEntityThatMovedAndWasNotDeclared_FailsTheScenario()
    {
        // What the permitted set cannot catch: one tolerated call whose script or scene cascades
        // into three devices. The recording says a permitted action ran; only the diff says the
        // whole living room went dark with it.
        var recording = await ScriptedTurn.RunAsync("Hecho", ScriptedTurn.Call(Create, "/timers/pasta/timer.json"));
        recording.StateBefore = new Dictionary<string, string> { ["light.kitchen"] = "on", ["climate.salon"] = "cool" };
        recording.StateAfter = new Dictionary<string, string> { ["light.kitchen"] = "off", ["climate.salon"] = "off" };

        var scenario = Timer() with { Changes = [new StateChange("light.kitchen", "off")] };

        ScenarioChecks.Failures(scenario, recording).ShouldHaveSingleItem().ShouldContain("climate.salon");
    }

    [Fact]
    public async Task ADeclaredChangeThatDidNotHappen_FailsEvenWhenTheReplySaysItDid()
    {
        var recording = await ScriptedTurn.RunAsync(
            "Listo, he encendido el aire", ScriptedTurn.Call(Create, "/timers/pasta/timer.json"));
        recording.StateBefore = new Dictionary<string, string> { ["climate.salon"] = "off" };
        recording.StateAfter = new Dictionary<string, string> { ["climate.salon"] = "off" };

        var scenario = Timer() with { Changes = [new StateChange("climate.salon", "cool")] };

        ScenarioChecks.Failures(scenario, recording).ShouldHaveSingleItem().ShouldContain("climate.salon");
    }

    [Fact]
    public async Task ATurnThatOnlyRead_LeavesNothingInTheDiff()
    {
        var recording = await ScriptedTurn.RunAsync("Veintiún grados", ScriptedTurn.Call(Read, "/ha/x/state.json"));
        recording.StateBefore = new Dictionary<string, string> { ["climate.salon"] = "cool" };
        recording.StateAfter = new Dictionary<string, string> { ["climate.salon"] = "cool" };

        var scenario = Timer() with
        {
            Required = [],
            Permitted = [new CallPermission(Read, "*")]
        };

        ScenarioChecks.Failures(scenario, recording).ShouldBeEmpty();
    }

    [Fact]
    public async Task AReplyThatBreaksItsOwnContract_FailsTheScenario()
    {
        // The reply travels with the calls rather than beside them: a turn that called exactly the
        // right tools and then read a file path aloud has not honoured the contract.
        var recording = await ScriptedTurn.RunAsync(
            "Listo, lo he escrito en /timers/pasta/timer.json",
            ScriptedTurn.Call(Create, "/timers/pasta/timer.json"));

        var scenario = Timer() with { Reply = new ReplyExpectation { Spoken = true } };

        ScenarioChecks.Failures(scenario, recording).ShouldHaveSingleItem().ShouldContain("a file path");
    }

    [Fact]
    public async Task APermittedCallWithUnexpectedArguments_DoesNotFail()
    {
        // Permission is by tool and path, never by argument: a scenario that had to predict every
        // argument of every tolerated call would fail on wording rather than on behaviour.
        var recording = await ScriptedTurn.RunAsync(
            "listo",
            ScriptedTurn.Call(Glob, "/timers"),
            ScriptedTurn.Call(Create, "/timers/pasta/timer.json"));

        var scenario = Timer() with { Permitted = [new CallPermission(Glob, "/timers*")] };

        ScenarioChecks.Failures(scenario, recording).ShouldBeEmpty();
    }

    [Fact]
    public async Task APermittedToolOnADifferentPath_Fails()
    {
        var recording = await ScriptedTurn.RunAsync(
            "listo",
            ScriptedTurn.Call(Glob, "/schedules"),
            ScriptedTurn.Call(Create, "/timers/pasta/timer.json"));

        var scenario = Timer() with { Permitted = [new CallPermission(Glob, "/timers*")] };

        ScenarioChecks.Failures(scenario, recording).ShouldHaveSingleItem().ShouldContain("/schedules");
    }

    [Fact]
    public async Task AMissingRequiredCall_FailsNamingWhatWasExpectedAndWhatWasSeen()
    {
        var recording = await ScriptedTurn.RunAsync(
            "listo", ScriptedTurn.Call(Create, "/timers/pasta/wrong.json"));

        var failures = ScenarioChecks.Failures(Timer(), recording);

        failures.ShouldContain(f => f.Contains("/timers/pasta/timer.json") && f.Contains("wrong.json"));
    }

    [Fact]
    public async Task ExceedingTheCeiling_Fails_AndTheRecordingKeptEveryCall()
    {
        var recording = await ScriptedTurn.RunAsync(
            "listo",
            ScriptedTurn.Call(Glob, "/timers"),
            ScriptedTurn.Call(Glob, "/timers"),
            ScriptedTurn.Call(Glob, "/timers"),
            ScriptedTurn.Call(Glob, "/timers"),
            ScriptedTurn.Call(Create, "/timers/pasta/timer.json"));

        var scenario = Timer() with { Permitted = [new CallPermission(Glob, "*")], CallCeiling = 4 };

        ScenarioChecks.Failures(scenario, recording).ShouldContain(f => f.Contains("5") && f.Contains("4"));
        recording.Calls.Count.ShouldBe(5);
    }

    [Fact]
    public async Task ACallNamingItsPathSomethingElse_IsStillMatchedByPath()
    {
        // Create and read say `filePath`, remove and info say `path`, glob says `basePath`. A
        // scenario asks about the path, and a check that only knew one spelling would tolerate a
        // write to anywhere as long as the tool spelled its argument differently.
        var recording = await ScriptedTurn.RunAsync(
            "listo",
            new ScriptedTurn.Step(Create, new Dictionary<string, object?>
            {
                ["filePath"] = "/schedules/pasta/task.json"
            }));

        var scenario = Timer() with { Permitted = [new CallPermission(Create, "/timers*")] };

        ScenarioChecks.Failures(scenario, recording).ShouldContain(f => f.Contains("unnecessary"));
    }

    [Fact]
    public async Task AnOrderingPairInTheWrongOrder_Fails()
    {
        var recording = await ScriptedTurn.RunAsync(
            "listo",
            ScriptedTurn.Call(Remove, "/timers/pasta"),
            ScriptedTurn.Call(Read, "/timers/pasta/status.json"));

        ScenarioChecks.Failures(Extending(), recording)
            .ShouldContain(f => f.Contains("before") && f.Contains("status"));
    }

    [Fact]
    public async Task UnrelatedCallsBetweenAConstrainedPair_DoNotFail()
    {
        var recording = await ScriptedTurn.RunAsync(
            "listo",
            ScriptedTurn.Call(Read, "/timers/pasta/status.json"),
            ScriptedTurn.Call(Glob, "/timers"),
            ScriptedTurn.Call(Remove, "/timers/pasta"));

        var scenario = Extending() with { Permitted = [new CallPermission(Glob, "*")] };

        ScenarioChecks.Failures(scenario, recording).ShouldBeEmpty();
    }

    [Fact]
    public async Task ARepeatedCallOnTheWrongSideOfThePair_DoesNotHideAValidOne()
    {
        // The model deleted, thought better of it, read the status and deleted again. There is a
        // status read followed by a delete, which is what the contract asks for; matching only the
        // first of each would report the order as broken because of the false start.
        var recording = await ScriptedTurn.RunAsync(
            "listo",
            ScriptedTurn.Call(Remove, "/timers/pasta"),
            ScriptedTurn.Call(Read, "/timers/pasta/status.json"),
            ScriptedTurn.Call(Remove, "/timers/pasta"));

        ScenarioChecks.Failures(Extending(), recording).ShouldBeEmpty();
    }

    [Fact]
    public async Task APairWhoseSecondCallNeverHappened_FailsDistinguishablyFromAnOutOfOrderPair()
    {
        var recording = await ScriptedTurn.RunAsync(
            "listo", ScriptedTurn.Call(Read, "/timers/pasta/status.json"));

        var failures = ScenarioChecks.Failures(Extending(), recording);

        failures.ShouldContain(f => f.Contains("never happened"));
        failures.ShouldNotContain(f => f.Contains("out of order"));
    }

    private static Scenario Extending() => new()
    {
        Name = "extend a running timer",
        AgentId = "nabu",
        Turn = new EvalTurn { Text = "añade dos minutos al temporizador de la pasta", Sender = "jack" },
        Instant = new DateTimeOffset(2026, 8, 18, 20, 0, 0, TimeSpan.FromHours(2)),
        CallCeiling = 6,
        Required =
        [
            new CallExpectation
            {
                Label = "status",
                Tool = Read,
                Arguments = [Arg.Is("path", "/timers/pasta/status.json")]
            },
            new CallExpectation
            {
                Label = "delete",
                Tool = Remove,
                Arguments = [Arg.Is("path", "/timers/pasta")]
            }
        ],
        Ordering = [new OrderingConstraint("status", "delete")]
    };
}