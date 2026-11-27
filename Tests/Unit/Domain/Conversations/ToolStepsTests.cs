using System.Text.Json;
using Domain.Agents;
using Domain.Conversations;
using Domain.DTOs.WebChat;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.Conversations;

public class ToolStepsTests
{
    [Fact]
    public void ACall_IsItsNameAndItsArgumentsAsJson()
    {
        var step = ToolSteps.Call(new FunctionCallContent("c1", "mcp__timers__timer_set",
            new Dictionary<string, object?> { ["minutes"] = 5, ["label"] = "tea" }));

        step.CallId.ShouldBe("c1");
        step.Name.ShouldBe("mcp__timers__timer_set");
        JsonDocument.Parse(step.Arguments!).RootElement.GetProperty("label").GetString().ShouldBe("tea");
        step.Outcome.ShouldBeNull();
        step.Result.ShouldBeNull();
    }

    [Fact]
    public void ALongArgument_IsShortenedInPlace_AndTheArgumentsStayJson()
    {
        var content = new string('x', ToolSteps.MaxStringLength + 500);
        var step = ToolSteps.Call(new FunctionCallContent("c1", "file_create",
            new Dictionary<string, object?> { ["path"] = "/a.txt", ["content"] = content }));

        var shown = JsonDocument.Parse(step.Arguments!).RootElement.GetProperty("content").GetString()!;
        shown.Length.ShouldBeLessThan(content.Length);
        shown.ShouldEndWith("[500 more characters]");
    }

    [Fact]
    public void AResultThatIsAnObject_IsItsJson()
    {
        var result = JsonSerializer.SerializeToElement(new { stdout = "hi\n", exitCode = 0 });

        var step = ToolSteps.Result(new FunctionResultContent("c1", result));

        step.CallId.ShouldBe("c1");
        JsonDocument.Parse(step.Result!).RootElement.GetProperty("stdout").GetString().ShouldBe("hi\n");
        step.Outcome.ShouldBe(ToolCallOutcome.Done);
    }

    [Fact]
    public void AResultThatIsJsonText_IsReadAsJson()
    {
        var step = ToolSteps.Result(new FunctionResultContent("c1", """{"ok":true,"n":2}"""));

        JsonDocument.Parse(step.Result!).RootElement.GetProperty("n").GetInt32().ShouldBe(2);
    }

    [Fact]
    public void AResultThatIsPlainText_IsThatText()
    {
        ToolSteps.Result(new FunctionResultContent("c1", "all done")).Result.ShouldBe("all done");
    }

    [Fact]
    public void AnErrorEnvelope_Failed()
    {
        var result = JsonSerializer.SerializeToElement(new { ok = false, errorCode = "not_found", message = "no such file" });

        ToolSteps.Result(new FunctionResultContent("c1", result)).Outcome.ShouldBe(ToolCallOutcome.Failed);
    }

    [Fact]
    public void ACallThatThrew_Failed()
    {
        var step = ToolSteps.Result(new FunctionResultContent("c1", null) { Exception = new InvalidOperationException("boom") });

        step.Outcome.ShouldBe(ToolCallOutcome.Failed);
        step.Result!.ShouldContain("boom");
    }

    [Fact]
    public void ACallThePersonTurnedDown_WasDeclined()
    {
        var step = ToolSteps.Result(new FunctionResultContent("c1", ApprovalRejection.ResultFor("mcp__x__delete")));

        step.Outcome.ShouldBe(ToolCallOutcome.Declined);
    }

    [Fact]
    public void ACallNobodyCouldBeAskedAbout_WasDeclined()
    {
        var step = ToolSteps.Result(new FunctionResultContent("c1", ApprovalRejection.UnaskableResultFor("mcp__x__delete")));

        step.Outcome.ShouldBe(ToolCallOutcome.Declined);
    }

    [Fact]
    public void AHugeResult_IsCutToTheCeiling_SayingHowMuchWasLeftOut()
    {
        var lines = string.Join("\n", Enumerable.Range(0, 5000).Select(i => $"line {i}"));

        var shown = ToolSteps.Result(new FunctionResultContent("c1", lines)).Result!;

        shown.Length.ShouldBeLessThanOrEqualTo(ToolSteps.MaxTextLength + 64);
        shown.ShouldEndWith("more characters]");
    }

    [Fact]
    public void AResultMergedOntoItsCall_KeepsTheCallAndTakesTheAnswer()
    {
        var call = ToolSteps.Call(new FunctionCallContent("c1", "glob", new Dictionary<string, object?> { ["pattern"] = "*" }));
        var result = ToolSteps.Result(new FunctionResultContent("c1", "a.txt"));

        var merged = call.Merge(result);

        merged.Name.ShouldBe("glob");
        merged.Arguments.ShouldBe(call.Arguments);
        merged.Result.ShouldBe("a.txt");
        merged.Outcome.ShouldBe(ToolCallOutcome.Done);
    }

    [Fact]
    public void Upsert_ReplacesTheStepWithTheSameCall_AndAppendsANewOne()
    {
        var call = ToolSteps.Call(new FunctionCallContent("c1", "glob", new Dictionary<string, object?>()));
        var other = ToolSteps.Call(new FunctionCallContent("c2", "read", new Dictionary<string, object?>()));
        var result = ToolSteps.Result(new FunctionResultContent("c1", "a.txt"));

        var steps = ToolCallStep.Upsert(ToolCallStep.Upsert([call], other), result);

        steps.Select(s => s.CallId).ShouldBe(["c1", "c2"]);
        steps[0].Result.ShouldBe("a.txt");
    }

    // An MCP tool answers in content parts. Live they are MEAI's TextContent; kept, they read back as the
    // store wrote them (PascalCase, with nulls). Either way the step is the tool's own text, read as JSON.
    [Fact]
    public void AnAnswerInContentParts_IsItsText_LiveAndKeptAlike()
    {
        const string text = """{"entries":[],"total":0}""";
        var live = ToolSteps.Result(new FunctionResultContent("c1", new TextContent(text)));
        var kept = ToolSteps.Result(new FunctionResultContent("c1", JsonSerializer.SerializeToElement(
            new Dictionary<string, object?> { ["$type"] = "text", ["Text"] = text, ["Annotations"] = null, ["AdditionalProperties"] = null })));
        var several = ToolSteps.Result(new FunctionResultContent("c1",
            new List<AIContent> { new TextContent(text) }));

        live.Result.ShouldBe(kept.Result);
        several.Result.ShouldBe(live.Result);
        JsonDocument.Parse(live.Result!).RootElement.GetProperty("total").GetInt32().ShouldBe(0);
    }

    // A picture or a file in an answer is named, never its bytes.
    [Fact]
    public void AnAnswersPicture_IsNamed_NotItsBytes()
    {
        var step = ToolSteps.Result(new FunctionResultContent("c1",
            new List<AIContent> { new TextContent("shot taken"), new DataContent(new byte[2048], "image/png") }));

        step.Result.ShouldBe("shot taken\n[image/png]");
    }
}