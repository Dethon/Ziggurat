using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

// Build is a lookup with three outcomes — a shipped template, the generic fallback with the name
// interpolated, or null — so each test here picks a branch. The wording inside a template is a
// const string; why it is worded that way belongs in LanguagePrompt, not in an assertion that a
// constant contains part of itself.
public class LanguagePromptTests
{
    // An agent with no pinned language used to get no language section at all, and jonas answered
    // a Spanish message in English (eval, 2026-09-10): the whole context it reads is English, so
    // silence about the language is a vote for English. The unpinned case now gets the relative
    // rule, stated the same defensive way as the pins — naming the English context as not counting.
    [Fact]
    public void Build_NoLanguage_TellsTheModelToFollowTheMessage()
    {
        foreach (var configured in new[] { null, "   " })
        {
            var result = LanguagePrompt.Build(configured)!;

            result.ShouldStartWith("## Language");
            result.ShouldContain("language the user wrote in");
            result.ShouldContain("English", customMessage: "the rule has to say the English context does not count");
        }
    }

    [Theory]
    [InlineData("es")]
    [InlineData("es-ES")]
    [InlineData("spanish")]
    [InlineData("Español")]
    [InlineData("castellano")]
    public void Build_SpanishAlias_RendersTheDirectiveInSpanish(string configured)
    {
        var result = LanguagePrompt.Build(configured);

        result.ShouldNotBeNull();
        result.ShouldStartWith("## Idioma");
        result.ShouldContain("SIEMPRE en español");
    }

    [Fact]
    public void Build_English_RendersTheDirectiveInEnglish()
    {
        var result = LanguagePrompt.Build("en")!;

        result.ShouldStartWith("## Language");
        result.ShouldContain("always reply in English");
    }

    // A language with no shipped template still gets a directive; the configured value is used
    // verbatim as the language name.
    [Fact]
    public void Build_UnknownLanguage_FallsBackToAnEnglishDirectiveNamingIt()
    {
        var result = LanguagePrompt.Build("Galician")!;

        result.ShouldStartWith("## Language");
        result.ShouldContain("always reply in Galician");
    }
}