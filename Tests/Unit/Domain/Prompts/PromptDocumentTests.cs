using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

// A prompt section is a text document: a header saying what its declaration says, and the words,
// with named holes for what only the host knows. These hold the format to its contract — what a
// header declares, where a body starts and ends, and that a hole the host fills cannot be dropped
// by an edit of the words without the edit failing.
public class PromptDocumentTests
{
    private const string Timers =
        """
        ---
        # Why the budget is what it is travels with the budget.
        name: timers_prompt
        purpose: Which mechanism a moment is.
        priority: Client
        budget: 900
        served-by: mcp-timers
        audience-agents: jonas, nabu
        governs: refusals, tool-use
        beats: core_directive
        placeholders: roster
        claims:
        - timers.duration-is-a-countdown: A duration up to four hours becomes a countdown.
        # A comment between two claims is still a comment.
        - timers.duration-capped-at-four-hours: durationSeconds is never written above four hours.
        ---

        ## Timers

        Short countdowns live at `/timers`.{{roster}}

        <!-- part: roster -->

        ### Voice satellites

        {{roster}}

        """;

    [Fact]
    public void Parse_TheHeader_DeclaresTheSection()
    {
        var declaration = PromptDocument.Parse("timers_prompt.md", Timers).AsSection();

        declaration.Name.ShouldBe("timers_prompt");
        declaration.Purpose.ShouldBe("Which mechanism a moment is.");
        declaration.Priority.ShouldBe(PromptPriority.Client);
        declaration.TokenBudget.ShouldBe(900);
        declaration.ServedBy.ShouldBe("mcp-timers");
        declaration.Audience.Agents.ShouldBe(["jonas", "nabu"]);
        declaration.Audience.Channels.ShouldBeEmpty();
        declaration.Conflict.Claims.ShouldBe([PromptRules.Refusals, PromptRules.ToolUse]);
        declaration.Conflict.Overrides.ShouldBe([PromptManifest.CoreDirective]);
        declaration.Claims.ShouldBe([
            new PromptClaim("timers.duration-is-a-countdown", "A duration up to four hours becomes a countdown."),
            new PromptClaim("timers.duration-capped-at-four-hours", "durationSeconds is never written above four hours.")
        ]);
    }

    [Fact]
    public void Parse_TheBodyAndEachPart_AreTheirWordsWithoutTheBlankLinesAroundThem()
    {
        var document = PromptDocument.Parse("timers_prompt.md", Timers);

        document.Body.ShouldBe("## Timers\n\nShort countdowns live at `/timers`.{{roster}}");
        document.Parts.Keys.ShouldBe(["roster"]);
        document.Parts["roster"].ShouldBe("### Voice satellites\n\n{{roster}}");
    }

    // A checkout on Windows hands the document over with CRLF; the words the model reads must not
    // depend on the machine that built the image.
    [Fact]
    public void Parse_ACrlfCheckout_ReadsTheSameWords()
    {
        var unix = PromptDocument.Parse("timers_prompt.md", Timers);
        var windows = PromptDocument.Parse("timers_prompt.md", Timers.Replace("\n", "\r\n"));

        windows.Body.ShouldBe(unix.Body);
        windows.Parts["roster"].ShouldBe(unix.Parts["roster"]);
    }

    [Fact]
    public void Parse_ABodyThatDropsADeclaredPlaceholder_FailsNamingIt()
    {
        var dropped = Timers.Replace("{{roster}}", "");

        var ex = Should.Throw<FormatException>(() => PromptDocument.Parse("timers_prompt.md", dropped));

        ex.Message.ShouldContain("timers_prompt.md");
        ex.Message.ShouldContain("{{roster}}");
    }

    [Fact]
    public void Parse_ABodyUsingAPlaceholderTheHeaderDoesNotDeclare_FailsNamingIt()
    {
        var undeclared = Timers.Replace("live at `/timers`.", "live at `{{mount}}`.");

        var ex = Should.Throw<FormatException>(() => PromptDocument.Parse("timers_prompt.md", undeclared));

        ex.Message.ShouldContain("{{mount}}");
    }

    // Skill bodies teach Jinja and Templater, whose holes are spelled with spaces inside the braces.
    [Fact]
    public void Parse_BracesAroundSpacedWords_AreProseRatherThanAPlaceholder()
    {
        var jinja = Timers.Replace("live at `/timers`.", "live at `{{ trigger.to_state.state }}`.");

        PromptDocument.Parse("timers_prompt.md", jinja).Body.ShouldContain("{{ trigger.to_state.state }}");
    }

    [Fact]
    public void Template_ForTheHolesTheBodyHas_FillsEachWithItsValue()
    {
        var roster = PromptDocument.Parse("timers_prompt.md", Timers).Part("roster", "roster");

        roster.Fill(("roster", "- kitchen-01 — Kitchen")).ShouldBe("### Voice satellites\n\n- kitchen-01 — Kitchen");
    }

    // What the host fills is the other half of the contract: an edit that removes a hole from the
    // header and the words alike still parses, and the host taking the body fails on it.
    [Fact]
    public void Template_ForHolesTheBodyDoesNotHave_FailsNamingThem()
    {
        var withoutRoster = Timers
            .Replace("placeholders: roster\n", "")
            .Replace("{{roster}}", "");
        var document = PromptDocument.Parse("timers_prompt.md", withoutRoster);

        var ex = Should.Throw<InvalidOperationException>(() => document.Template("roster"));

        ex.Message.ShouldContain("timers_prompt.md");
        ex.Message.ShouldContain("{{roster}}");
    }

    [Fact]
    public void Fill_WithAValueMissing_Fails()
    {
        var body = PromptDocument.Parse("timers_prompt.md", Timers).Template("roster");

        Should.Throw<InvalidOperationException>(() => body.Fill()).Message.ShouldContain("{{roster}}");
    }

    // One pass, so a value the host fills is the value: a user's text that happens to spell a hole
    // is not filled again.
    [Fact]
    public void Fill_AValueSpellingAPlaceholder_IsLeftAsWritten()
    {
        var body = PromptDocument.Parse("timers_prompt.md", Timers).Template("roster");

        body.Fill(("roster", " {{roster}}")).ShouldEndWith("`/timers`. {{roster}}");
    }

    [Fact]
    public void Text_OfABodyWithAHole_Fails()
    {
        var document = PromptDocument.Parse("timers_prompt.md", Timers);

        Should.Throw<InvalidOperationException>(() => document.Text);
    }

    [Fact]
    public void Claim_TheHeaderDeclares_IsReturned_AndOneItDoesNotIsRefused()
    {
        var document = PromptDocument.Parse("timers_prompt.md", Timers);

        document.Claim("timers.duration-is-a-countdown").Statement
            .ShouldBe("A duration up to four hours becomes a countdown.");
        Should.Throw<InvalidOperationException>(() => document.Claim("timers.gone"))
            .Message.ShouldContain("timers.gone");
    }

    [Theory]
    [InlineData("budget: 900", "budgte: 900", "budgte")]
    [InlineData("priority: Client", "priority: Clients", "Clients")]
    [InlineData("governs: refusals, tool-use", "governs: refusal", "refusal")]
    public void AsSection_AHeaderTheManifestCannotRead_FailsNamingTheValue(string line, string typo, string named)
    {
        var document = PromptDocument.Parse("timers_prompt.md", Timers.Replace(line, typo));

        Should.Throw<FormatException>(() => document.AsSection()).Message.ShouldContain(named);
    }

    [Fact]
    public void Parse_TextWithNoHeader_Fails()
    {
        Should.Throw<FormatException>(() => PromptDocument.Parse("bare.md", "## Just words"))
            .Message.ShouldContain("bare.md");
    }
}