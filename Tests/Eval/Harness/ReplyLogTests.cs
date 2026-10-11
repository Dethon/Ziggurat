using Infrastructure.Agents.ChatClients;
using Shouldly;

namespace Tests.Eval.Harness;

// A mark with no reply beside it cannot be argued with. The failure dump keeps a reply only when
// a run fails its contract, and a graded run rarely does — so what the judge read, and what it
// said about it, was gone the moment the scorecard was written.
public class ReplyLogTests : IDisposable
{
    private readonly string _output = Directory.CreateTempSubdirectory("eval-replies").FullName;

    public void Dispose() => Directory.Delete(_output, recursive: true);

    private static Scenario TheScenario => new()
    {
        Name = "research: a sprain is answered",
        AgentId = "jonas",
        Turn = new EvalTurn { Text = "Me he torcido el tobillo", Sender = "fran" },
        Instant = new DateTimeOffset(2026, 8, 15, 20, 0, 0, TimeSpan.FromHours(2)),
        CallCeiling = 14
    };

    [Fact]
    public void AGradedRun_LeavesItsReplyAndItsMarks_UnderTheModelThatGaveThem()
    {
        var recording = new Recording { Reply = "Hielo 15-20 minutos cada 2-3 horas." };
        recording.OnTurn(new TurnObservation("prompt", new ServedRoute("anthropic/claude-haiku-5.5", "Anthropic")));

        var path = ReplyLog.Write(_output, TheScenario, recording,
            [new GradeMark("accuracy", 5), new GradeMark("actionability", 7, "one generic line added")], run: 2);

        path.ShouldBe(Path.Combine(
            _output, "replies", "anthropic-claude-haiku-5.5", "research-a-sprain-is-answered-run2.md"));
        var log = File.ReadAllText(path);
        log.ShouldContain("Me he torcido el tobillo");
        log.ShouldContain("Hielo 15-20 minutos cada 2-3 horas.");
        log.ShouldContain("accuracy: 5");
        log.ShouldContain("actionability: 7 — one generic line added");
    }

    [Fact]
    public void ARunThatNeverAnswered_SaysSoWhereTheReplyWouldBe()
    {
        var recording = new Recording { TimedOut = true };

        var log = File.ReadAllText(ReplyLog.Write(_output, TheScenario, recording, [], run: 0));

        log.ShouldContain("(the run timed out before it answered)");
    }
}