using System.Text;
using Domain.Apps;
using Domain.Monitor;
using Domain.Prompts;
using Domain.Tools;
using Shouldly;

namespace Tests.Unit.Domain.Apps;

// The app ask's wire: what a page may send, what an app's instructions may be, and what each refusal of
// the agent's is to the app.
public class AppAsksTests
{
    // The bound is what keeps an app's instructions inside the section they are composed into: at the
    // bound, the section is never reported over its budget.
    [Fact]
    public void TheInstructionsBound_FitsTheAppInstructionsSectionsBudget()
    {
        var budget = PromptManifest.Find(PromptManifest.AppInstructions)!.TokenBudget;

        PromptTokens.Estimate(new string('x', AppAsks.MaxInstructionsBytes)).ShouldBeLessThanOrEqualTo(budget);
    }

    public static TheoryData<string, string, bool> Refusals => new()
    {
        { RefusalAnswers.NotVerified, ToolError.Codes.PermissionDenied, true },
        { RefusalAnswers.Suspended, ToolError.Codes.PermissionDenied, true },
        { RefusalAnswers.Disabled, ToolError.Codes.PermissionDenied, true },
        { RefusalAnswers.AppsOff, ToolError.Codes.PermissionDenied, true },
        { RefusalAnswers.AgentGone("jonas"), ToolError.Codes.PermissionDenied, true },
        { RefusalAnswers.WithoutKey, ToolError.Codes.PermissionDenied, true },
        { RefusalAnswers.KeyRefused, ToolError.Codes.PermissionDenied, true },
        { RefusalAnswers.OutOfCredit, ToolError.Codes.PermissionDenied, true },
        // Whether the household lets it through could not be read: worth asking again.
        { RefusalAnswers.Unchecked, ToolError.Codes.TransientDependency, true },
        // No refusal: what the agent caught, kept from the page.
        { RefusalAnswers.SetupFailed, ToolError.Codes.TransientDependency, false },
        { "Provider returned 500: {\"error\":\"credits\"}", ToolError.Codes.TransientDependency, false }
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void ARefusal_IsTheEnvelopeItsCodeSays_InTheAgentsWordsOnlyWhenItIsARefusal(
        string answer, string code, bool shown)
    {
        var envelope = AppAsks.Refusal(answer);

        envelope.ErrorCode.ShouldBe(code);
        (envelope.Message == answer).ShouldBe(shown);
        envelope.Hint.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void ARefusalNamingTheAgent_NamesItToTheApp() =>
        AppAsks.Refusal(RefusalAnswers.AgentGone("jonas")).Message.ShouldContain("'jonas'");

    [Fact]
    public void AnAsksText_IsRead()
    {
        AppAskRequest.Parse("""{"text":"How much is this?"}"""u8, maxText: 100)
            .ShouldBeOfType<AppAskRequestParse.Valid>().Request.Text.ShouldBe("How much is this?");
    }

    [Theory]
    [InlineData("""{"text":""}""", "1 to 10 characters")]
    [InlineData("""{"text":"   "}""", "1 to 10 characters")]
    [InlineData("""{"text":"eleven char"}""", "1 to 10 characters")]
    [InlineData("""{}""", "text")]
    [InlineData("""{"text":null}""", "text")]
    [InlineData("""{"text":"hi","conversation":"c"}""", "conversation")]
    [InlineData("""["hi"]""", "JSON object")]
    [InlineData("""not json""", "JSON object")]
    public void AnAskOutsideItsShape_IsRefusedWhole(string body, string words)
    {
        AppAskRequest.Parse(Encoding.UTF8.GetBytes(body), maxText: 10)
            .ShouldBeOfType<AppAskRequestParse.Refused>().Problem.ShouldContain(words);
    }

    [Fact]
    public void Instructions_AreTheFilesText()
    {
        AppAsks.ReadInstructions("instructions.md", "Answer in one word."u8.ToArray())
            .ShouldBeOfType<AppInstructionsRead.Read>().Text.ShouldBe("Answer in one word.");
    }

    public static TheoryData<byte[]?, string> UnreadableInstructions => new()
    {
        { null, "not a file of the app" },
        { new byte[AppAsks.MaxInstructionsBytes + 1].Select(_ => (byte)'x').ToArray(), $"over the {AppAsks.MaxInstructionsBytes} bytes" },
        { " \n\t"u8.ToArray(), "empty" },
        { [0xff, 0xfe, 0x41], "not UTF-8" }
    };

    [Theory]
    [MemberData(nameof(UnreadableInstructions))]
    public void Instructions_ThatAreNoFileOrOverTheBound_AreRefusedNamingWhy(byte[]? file, string words)
    {
        AppAsks.ReadInstructions("asks/estimate.md", file)
            .ShouldBeOfType<AppInstructionsRead.Refused>().Problem.ShouldContain(words);
    }

    [Fact]
    public void Instructions_AtTheBound_AreRead() =>
        AppAsks.ReadInstructions("i.md", Enumerable.Repeat((byte)'x', AppAsks.MaxInstructionsBytes).ToArray())
            .ShouldBeOfType<AppInstructionsRead.Read>();
}