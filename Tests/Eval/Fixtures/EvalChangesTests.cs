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

    // The selection is handed in rather than read off the process: asking EvalChanges.Selected
    // here would make the assertion a claim about the caller's shell, and a bare `dotnet test`
    // with ZIGGURAT_EVAL_CHANGED exported would shell out to git at discovery and fail.
    [Fact]
    public void WhenNothingIsSelected_TheTierHoldsOnePlaceholderRow_PerShard()
    {
        // A theory with no rows fails discovery; a row that skips is a tier that is present.
        var rows = ((IEnumerable<object[]>)EvalSuite.Named(EvalTier.Changed, [], 0, of: 4))
            .Select(row => (string)row[0]).ToList();

        rows.ShouldBe([EvalChanges.NothingSelected]);
    }

    // The other half of the same rule: a shard the selection did reach names its scenarios and
    // carries no placeholder.
    [Fact]
    public void WhenAShardHasSelectedScenarios_ItNamesThem()
    {
        var selected = EvalSuite.All.Take(4).ToList();

        var rows = ((IEnumerable<object[]>)EvalSuite.Named(EvalTier.Changed, selected, 0, of: 4))
            .Select(row => (string)row[0]).ToList();

        rows.ShouldBe([selected[0].Name]);
        rows.ShouldNotContain(EvalChanges.NothingSelected);
    }
}