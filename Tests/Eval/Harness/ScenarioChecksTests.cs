using Shouldly;
using Tests.Eval.Fixtures;

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
    private const string Exec = "domain__filesystem_exec";

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
    public async Task ANoteThatLostSyntaxTheEditDidNotTouch_FailsTheScenario()
    {
        // The whole risk of editing somebody's notes: the change asked for lands, and a wikilink
        // becomes a markdown link on the way past. Only the file's own text after the turn says so.
        var recording = await Written(
            "/vault/Cocina/Pasta.md", "---\ntags: [receta]\n---\nVer [salsas](Salsas.md)\n");

        var scenario = Timer() with
        {
            Required = [],
            Permitted = [new CallPermission(Create, "*")],
            Files = [new FileExpectation { Path = "/vault/Cocina/Pasta.md", Contains = ["[[Salsas]]"] }]
        };

        ScenarioChecks.Failures(scenario, recording).ShouldHaveSingleItem().ShouldContain("[[Salsas]]");
    }

    [Fact]
    public async Task ATextTheEditWasSupposedToRemove_FailsWhileItIsStillThere()
    {
        var recording = await Written("/vault/Cocina/Pasta.md", "Ver [[Salsas]]\n");

        var scenario = Timer() with
        {
            Required = [],
            Permitted = [new CallPermission(Create, "*")],
            Files = [new FileExpectation { Path = "/vault/Cocina/Pasta.md", Absent = ["[[Salsas]]"] }]
        };

        ScenarioChecks.Failures(scenario, recording).ShouldHaveSingleItem().ShouldContain("still");
    }

    [Fact]
    public async Task AFileDeclaredUntouched_FailsWhenTheTurnRewroteIt()
    {
        var recording = await Written("/vault/Cocina/Pasta.md", "rewritten");
        recording.FilesBefore = new Dictionary<string, string> { ["/vault/Cocina/Pasta.md"] = "original" };

        var scenario = Timer() with
        {
            Required = [],
            Permitted = [new CallPermission(Create, "*")],
            Files = [new FileExpectation { Path = "/vault/Cocina/Pasta.md", Unchanged = true }]
        };

        ScenarioChecks.Failures(scenario, recording).ShouldHaveSingleItem().ShouldContain("unchanged");
    }

    [Fact]
    public async Task AFileTheTurnWasSupposedToWrite_FailsWhenItIsNotThere()
    {
        var recording = await Written("/vault/Cocina/Otra.md", "algo");

        var scenario = Timer() with
        {
            Required = [],
            Permitted = [new CallPermission(Create, "*")],
            Files = [new FileExpectation { Path = "/vault/Cocina/Tortilla.md", Contains = ["patata"] }]
        };

        ScenarioChecks.Failures(scenario, recording).ShouldHaveSingleItem().ShouldContain("does not exist");
    }

    [Fact]
    public async Task AFileTheTurnWasSupposedToRemove_FailsWhileItIsStillThere()
    {
        // A rename that copies and leaves the original updates every incoming link correctly and
        // still leaves the user with two notes where they had one.
        var recording = await Written("/vault/Cocina/Salsas.md", "El pesto va en [[Pasta al pesto]].");

        var scenario = Timer() with
        {
            Required = [],
            Permitted = [new CallPermission(Create, "*")],
            Files = [new FileExpectation { Path = "/vault/Cocina/Salsas.md", Deleted = true }]
        };

        ScenarioChecks.Failures(scenario, recording).ShouldHaveSingleItem().ShouldContain("still there");
    }

    private static async Task<Recording> Written(string path, string content)
    {
        var recording = await ScriptedTurn.RunAsync("Hecho", ScriptedTurn.Call(Create, path));
        recording.FilesAfter = new Dictionary<string, string> { [path] = content };
        return recording;
    }

    [Fact]
    public async Task ATurnThatDelegatedWhenNothingWasDeclared_Fails()
    {
        // Declaring nothing is how a scenario says "do this yourself": a lookup that takes one call
        // is slower and worse through a worker, and nothing else in the recording would show it.
        var recording = await ScriptedTurn.RunAsync("Hecho", ScriptedTurn.Call(Create, "/timers/pasta/timer.json"));
        recording.Delegations = [new Delegation("jonas-worker", "mira el temporizador")];

        ScenarioChecks.Failures(Timer(), recording).ShouldHaveSingleItem().ShouldContain("jonas-worker");
    }

    [Fact]
    public async Task ADeclaredDelegation_DoesNotAlsoHaveToBePermittedAsACall()
    {
        // The declaration is the permission. A scenario that had to say it twice would report a
        // correct decision as an unnecessary call the first time somebody forgot the second line.
        var recording = await ScriptedTurn.RunAsync(
            "Hecho", ScriptedTurn.Call(EvalTools.Subagent, "/ignored"));
        recording.Delegations = [new Delegation("jonas-worker", "resume la carpeta Cocina")];

        var scenario = Timer() with
        {
            Required = [],
            Delegates = [new DelegationExpectation { Profile = "jonas-worker", Carries = ["Cocina"] }]
        };

        ScenarioChecks.Failures(scenario, recording).ShouldBeEmpty();
    }

    [Fact]
    public async Task ATolerateDelegation_IsNotAFailure()
    {
        var recording = await ScriptedTurn.RunAsync("Hecho", ScriptedTurn.Call(Create, "/timers/pasta/timer.json"));
        recording.Delegations = [new Delegation("jonas-worker", "mira una cosa")];

        ScenarioChecks.Failures(Timer() with { MayDelegateTo = ["jonas-worker"] }, recording)
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task ADelegatedPromptMissingWhatTheTaskNeeds_Fails()
    {
        // The worker has no conversation history, so a url, a name or a requirement the parent left
        // out of the prompt is simply gone by the time the work starts.
        var recording = await ScriptedTurn.RunAsync("Hecho", ScriptedTurn.Call(Create, "/timers/pasta/timer.json"));
        recording.Delegations = [new Delegation("jonas-worker", "busca el precio")];

        var scenario = Timer() with
        {
            Delegates = [new DelegationExpectation { Profile = "jonas-worker", Carries = ["ejemplo.com"] }]
        };

        ScenarioChecks.Failures(scenario, recording).ShouldHaveSingleItem().ShouldContain("ejemplo.com");
    }

    [Fact]
    public async Task TwoDeclaredDelegations_AreNotSatisfiedByOne()
    {
        // Two independent halves are two workers running at once; one worker told to do both is
        // the sequence the delegation exists to avoid, and its prompt would satisfy both.
        var recording = await ScriptedTurn.RunAsync("Hecho", ScriptedTurn.Call(Create, "/timers/pasta/timer.json"));
        recording.Delegations = [new Delegation("jonas-worker", "el tiempo en Madrid y el precio del bitcoin")];

        var scenario = Timer() with
        {
            Delegates =
            [
                new DelegationExpectation { Profile = "jonas-worker", Carries = ["tiempo"] },
                new DelegationExpectation { Profile = "jonas-worker", Carries = ["bitcoin"] }
            ]
        };

        ScenarioChecks.Failures(scenario, recording).ShouldHaveSingleItem().ShouldContain("bitcoin");
    }

    [Fact]
    public async Task TwoDelegationsCarryingWhatTheyNeed_Pass()
    {
        var recording = await ScriptedTurn.RunAsync("Hecho", ScriptedTurn.Call(Create, "/timers/pasta/timer.json"));
        recording.Delegations =
        [
            new Delegation("jonas-worker", "dime el tiempo en Madrid mañana"),
            new Delegation("jonas-worker", "dime el precio del bitcoin hoy")
        ];

        var scenario = Timer() with
        {
            Delegates =
            [
                new DelegationExpectation { Profile = "jonas-worker", Carries = ["tiempo", "Madrid"] },
                new DelegationExpectation { Profile = "jonas-worker", Carries = ["bitcoin"] }
            ]
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

    [Fact]
    public async Task AStaleFactThatSurvivedTheTurn_FailsTheScenario()
    {
        var recording = await ScriptedTurn.RunAsync("Vale, apuntado");
        recording.MemoriesAfter = ["Trabaja en Acme", "Le gusta el café solo"];

        var scenario = Corrected();

        ScenarioChecks.Failures(scenario, recording).ShouldHaveSingleItem().ShouldContain("Acme");
    }

    [Fact]
    public async Task AFactNobodyAskedToForgetAndIsGone_FailsTheScenario()
    {
        // The failure mode a forget-by-query has: one call, no error, and everything the search
        // reached deleted with it. Nothing in the call log says so — only the store afterwards.
        var recording = await ScriptedTurn.RunAsync("Vale, apuntado");
        recording.MemoriesAfter = [];

        ScenarioChecks.Failures(Corrected(), recording)
            .ShouldHaveSingleItem().ShouldContain("Le gusta el café solo");
    }

    [Fact]
    public async Task ForgettingExactlyTheStaleFact_Passes()
    {
        var recording = await ScriptedTurn.RunAsync("Vale, apuntado");
        recording.MemoriesAfter = ["Le gusta el café solo"];

        ScenarioChecks.Failures(Corrected(), recording).ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadingAnActionsManual_IsPermittedWithoutPermittingTheAction()
    {
        // An action file's arguments are read by running it with --help, which is an exec like any
        // other. A scenario that had to permit exec on the directory to tolerate the manual would
        // be permitting every action in it, including the one it exists to forbid.
        var recording = await ScriptedTurn.RunAsync(
            "listo",
            ScriptedTurn.Exec("/ha/entities/media_player/altavoz", "media_seek.sh --help"),
            ScriptedTurn.Exec("/ha/entities/media_player/altavoz", "media_seek.sh --seek_position 1"));

        var scenario = Seeking();

        ScenarioChecks.Failures(scenario, recording).ShouldBeEmpty();
    }

    [Fact]
    public async Task AnActionRunForRealUnderTheSamePermission_IsStillUnnecessary()
    {
        var recording = await ScriptedTurn.RunAsync(
            "listo",
            ScriptedTurn.Exec("/ha/entities/media_player/altavoz", "media_seek.sh --seek_position 1"),
            ScriptedTurn.Exec("/ha/entities/media_player/altavoz", "music_assistant.play_media.sh --media_id x"));

        ScenarioChecks.Failures(Seeking(), recording)
            .ShouldHaveSingleItem().ShouldContain("play_media");
    }

    private static Scenario Seeking() => new()
    {
        Name = "start it over",
        AgentId = "nabu",
        Turn = new EvalTurn { Text = "ponlo desde el principio", Sender = "jack" },
        Instant = new DateTimeOffset(2026, 8, 18, 20, 0, 0, TimeSpan.FromHours(2)),
        CallCeiling = 4,
        Required =
        [
            new CallExpectation
            {
                Label = "seek",
                Tool = Exec,
                Arguments = [Arg.Matches("command", @"^media_seek\.sh\b")]
            }
        ],
        Permitted = [CallPermission.Manual(Exec, "/ha*")]
    };

    [Fact]
    public async Task ARequiredCallNamedByPattern_MatchesTheServerThatActuallyAnswered()
    {
        // An MCP tool is named after the endpoint it was dialled on — host and port — and the port
        // is whatever was free when the stack came up. A scenario that spelled the whole name would
        // pass on nothing.
        var recording = await ScriptedTurn.RunAsync(
            "listo", ScriptedTurn.Call("mcp__localhost-49812__web_search"));

        ScenarioChecks.Failures(Searching(), recording).ShouldBeEmpty();
    }

    [Fact]
    public async Task APatternThatMatchesNothingCalled_StillFails()
    {
        var recording = await ScriptedTurn.RunAsync(
            "listo", ScriptedTurn.Call("mcp__localhost-49812__web_browse"));

        ScenarioChecks.Failures(Searching(), recording)
            .ShouldContain(f => f.Contains("never happened"));
    }

    private static Scenario Searching() => new()
    {
        Name = "search before browsing",
        AgentId = "jonas",
        Turn = new EvalTurn { Text = "busca la receta", Sender = "jack" },
        Instant = new DateTimeOffset(2026, 8, 18, 20, 0, 0, TimeSpan.FromHours(2)),
        CallCeiling = 3,
        Required = [new CallExpectation { Label = "search", Tool = "mcp__*__web_search" }],
        Permitted = [new CallPermission("mcp__*__web_browse")]
    };

    [Fact]
    public async Task AWarmUpProbeThatAsksNothing_IsNotCountedAgainstTheScenario()
    {
        // Observed on 2026-08-19: the model opened a home-automation turn with
        // web_search "noop" and then did the work correctly. It draws whichever scenario it lands
        // on, so counting it would make one random scenario per run red for the model clearing its
        // throat. It is recorded as a finding instead, and the dump still shows the call.
        var recording = await ScriptedTurn.RunAsync(
            "listo",
            ScriptedTurn.Search("mcp__localhost-1234__web_search", "noop"),
            ScriptedTurn.Call(Create, "/timers/pasta/timer.json"));

        ScenarioChecks.Failures(Timer() with { CallCeiling = 1 }, recording).ShouldBeEmpty();
    }

    [Fact]
    public async Task ASearchThatActuallyAsksSomething_IsStillAnUnnecessaryCall()
    {
        var recording = await ScriptedTurn.RunAsync(
            "listo",
            ScriptedTurn.Search("mcp__localhost-1234__web_search", "temporizadores de pasta"),
            ScriptedTurn.Call(Create, "/timers/pasta/timer.json"));

        ScenarioChecks.Failures(Timer(), recording)
            .ShouldHaveSingleItem().ShouldContain("web_search");
    }

    private static Scenario Corrected() => new()
    {
        Name = "a corrected employer is forgotten",
        AgentId = "nabu",
        Turn = new EvalTurn { Text = "ya no trabajo en Acme", Sender = "jack" },
        Instant = new DateTimeOffset(2026, 8, 18, 20, 0, 0, TimeSpan.FromHours(2)),
        CallCeiling = 2,
        Remembered =
        [
            new RememberedFact("Trabaja en Acme") { Forgotten = true },
            new RememberedFact("Le gusta el café solo")
        ]
    };

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