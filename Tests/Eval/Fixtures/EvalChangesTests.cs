using Shouldly;
using Tests.Eval.Harness;

namespace Tests.Eval.Fixtures;

// The changed tier runs what a diff touched and nothing else. Off unless asked for, and when
// nothing is asked the tier's classes hold one row that skips, so a bare run reports the tier
// as present and idle rather than failing a theory with no data.
public class EvalChangesTests
{
    [Fact]
    public void Reference_NothingAsked_IsNothing()
    {
        EvalChanges.Reference(null).ShouldBeNull();
        EvalChanges.Reference("").ShouldBeNull();
        EvalChanges.Reference(" ").ShouldBeNull();
        EvalChanges.Reference("0").ShouldBeNull();
    }

    [Fact]
    public void Reference_Asked_IsTheMainBranch_OrTheRefNamed()
    {
        EvalChanges.Reference("1").ShouldBe("master");
        EvalChanges.Reference("true").ShouldBe("master");
        EvalChanges.Reference("HEAD~3").ShouldBe("HEAD~3");
        EvalChanges.Reference(" origin/master ").ShouldBe("origin/master");
    }

    [Fact]
    public void Parsed_JoinsTheDiffAndTheUntracked_AsRepositoryPaths()
    {
        var files = EvalChanges.Parsed(
            "Domain/Prompts/TimerPrompt.cs\nTests/Eval/Scenarios/TimerScenarios.cs\n",
            "Domain/Prompts/New.cs\n\n");

        files.ShouldBe(
            ["Domain/Prompts/TimerPrompt.cs", "Tests/Eval/Scenarios/TimerScenarios.cs", "Domain/Prompts/New.cs"]);
    }

    [Fact]
    public void WhenNothingIsAsked_TheTierHoldsOnePlaceholderRow_PerShard()
    {
        // A theory with no rows fails discovery; a row that skips is a tier that is present.
        var rows = ((IEnumerable<object[]>)EvalSuite.Named(EvalTier.Changed, 0, of: 4))
            .Select(row => (string)row[0]).ToList();

        rows.ShouldBe([EvalChanges.NothingSelected]);
    }
}