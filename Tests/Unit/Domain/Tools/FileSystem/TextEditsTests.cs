using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

// The edit loop the rendered mounts share: an edit replaces its first occurrence or every one,
// reports how many it touched, and an edit whose text is not there fails by name before anything
// is changed.
public class TextEditsTests
{
    [Fact]
    public void Apply_ReplacesTheFirstOccurrence_AndReportsOne()
    {
        var result = TextEdits.Apply("a=1\nb=1\n", [new TextEdit("=1", "=2")]);

        var applied = result.ShouldBeOfType<FsResult<TextEdits.Applied>.Ok>().Value;
        applied.Text.ShouldBe("a=2\nb=1\n");
        applied.Total.ShouldBe(1);
        applied.Details.Single().OccurrencesReplaced.ShouldBe(1);
        applied.Details.Single().AffectedLines.ShouldBe(new FsLineRange { Start = 1, End = 3 });
    }

    [Fact]
    public void Apply_ReplaceAll_ReplacesEveryOccurrence_AndReportsTheCount()
    {
        var result = TextEdits.Apply("a=1\nb=1\n", [new TextEdit("=1", "=2", ReplaceAll: true)]);

        var applied = result.ShouldBeOfType<FsResult<TextEdits.Applied>.Ok>().Value;
        applied.Text.ShouldBe("a=2\nb=2\n");
        applied.Total.ShouldBe(2);
    }

    [Fact]
    public void Apply_RunsTheEditsInOrder_EachOnTheOutputOfTheLast()
    {
        var result = TextEdits.Apply("x", [new TextEdit("x", "y"), new TextEdit("y", "z")]);

        var applied = result.ShouldBeOfType<FsResult<TextEdits.Applied>.Ok>().Value;
        applied.Text.ShouldBe("z");
        applied.Details.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("")]
    public void Apply_TextThatIsNotThere_IsInvalidNamingIt(string oldString)
    {
        var result = TextEdits.Apply("a=1", [new TextEdit("a", "b"), new TextEdit(oldString, "c")]);

        var error = result.ShouldBeOfType<FsResult<TextEdits.Applied>.Err>().Error;
        error.ErrorCode.ShouldBe("invalid_argument");
        error.Message.ShouldContain($"Text not found: '{oldString}'");
    }

    [Theory]
    [InlineData("aXbXc", "X", "-", false, "a-bXc")]
    [InlineData("aXbXc", "X", "-", true, "a-b-c")]
    [InlineData("abc", "X", "-", false, "abc")]
    public void ReplaceFirstOrAll_IsOrdinal_AndLeavesAMissWhole(string text, string oldValue, string newValue, bool all, string expected)
    {
        TextEdits.ReplaceFirstOrAll(text, oldValue, newValue, all).ShouldBe(expected);
    }
}