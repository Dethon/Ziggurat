using Domain.DTOs.WebChat;
using Shouldly;
using WebChat.Client.Components.Chat;

namespace Tests.Unit.WebChat.Client.Components;

public sealed class ToolDisplayTests
{
    private static ToolCallStep Answered(string result, ToolCallOutcome outcome = ToolCallOutcome.Done) =>
        new() { CallId = "c1", Result = result, Outcome = outcome };

    [Theory]
    [InlineData("mcp__timers__timer_set", "timer_set", "timers")]
    [InlineData("domain__filesystem__exec", "exec", "filesystem")]
    [InlineData("mcp__mcp-vault-8080__fs_glob", "fs_glob", "vault")]
    [InlineData("glob", "glob", null)]
    [InlineData(null, "tool", null)]
    public void NameOf_IsTheBareNameAndTheServerItRanOn(string? name, string bare, string? server) =>
        ToolDisplay.NameOf(name).ShouldBe(new ToolDisplay.ToolName(bare, server));

    [Fact]
    public void Summary_IsTheArgumentThatSaysMost_OnOneLine()
    {
        ToolDisplay.Summary("""{"path":"/home","command":"ls -la\n| head"}""").ShouldBe("ls -la | head");
        ToolDisplay.Summary("""{"limit":5,"other":"x"}""").ShouldBe("x");
        ToolDisplay.Summary("""{"limit":5}""").ShouldBeEmpty();
        ToolDisplay.Summary("not json").ShouldBeEmpty();
    }

    [Fact]
    public void Fields_LayOutEachArgument_LongOrNestedOnesAsBlocks()
    {
        var content = new string('x', 200);
        var fields = ToolDisplay.Fields("{\"path\":\"/a.txt\",\"content\":\"" + content + "\",\"options\":{\"force\":true}}");

        fields.Select(f => (f.Key, f.Block)).ShouldBe([("path", false), ("content", true), ("options", true)]);
        fields[0].Value.ShouldBe("/a.txt");
        fields[2].Json!.Value.GetProperty("force").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void ACommandsAnswer_ReadsAsItsOutput()
    {
        var view = ToolDisplay.ResultOf(Answered("""{"stdout":"a.txt\n","stderr":"","exitCode":0,"durationMs":11,"cwd":"/home"}"""));

        view.ShouldBe(new ToolDisplay.CommandOutput("a.txt\n", "", 0, "11 ms · /home"));
    }

    [Fact]
    public void AFailuresEnvelope_ReadsAsTheFailure()
    {
        var view = ToolDisplay.ResultOf(Answered(
            """{"ok":false,"errorCode":"not_found","message":"No such file","hint":"List the folder first"}""",
            ToolCallOutcome.Failed));

        view.ShouldBe(new ToolDisplay.Failure("not_found", "No such file", "List the folder first"));
    }

    [Fact]
    public void JsonReadsIndented_AndTextAsItIs()
    {
        ToolDisplay.ResultOf(Answered("""{"n":2}""")).ShouldBeOfType<ToolDisplay.Json>().Value.GetProperty("n").GetInt32().ShouldBe(2);
        ToolDisplay.ResultOf(Answered("""[1,2]""")).ShouldBeOfType<ToolDisplay.Json>();
        ToolDisplay.ResultOf(Answered("plain words")).ShouldBe(new ToolDisplay.Text("plain words"));
        ToolDisplay.ResultOf(new ToolCallStep { CallId = "c1" }).ShouldBeNull();
    }

    // Arguments and answers are whatever a model wrote or a page said: they come back as text, which the
    // page escapes, never as markup.
    [Fact]
    public void Markup_ComesBackAsText()
    {
        ToolDisplay.ResultOf(Answered("<img src=x onerror=alert(1)>"))
            .ShouldBe(new ToolDisplay.Text("<img src=x onerror=alert(1)>"));
    }

    [Fact]
    public void ACallWithACommand_RunsACommand()
    {
        ToolDisplay.RunsACommand(new ToolCallStep { CallId = "c", Arguments = """{"command":"ls"}""" }).ShouldBeTrue();
        ToolDisplay.RunsACommand(new ToolCallStep { CallId = "c", Arguments = """{"pattern":"*"}""" }).ShouldBeFalse();
    }
}