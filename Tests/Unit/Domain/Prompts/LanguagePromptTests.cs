using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

// Build is a lookup with three outcomes — a shipped template, the generic fallback with the name
// interpolated, or null — so each test here picks a branch. The wording inside a template is a
// const string; why it is worded that way belongs in LanguagePrompt, not in an assertion that a
// constant contains part of itself.
public class LanguagePromptTests
{
    [Fact]
    public void Build_NoLanguage_ReturnsNull()
    {
        LanguagePrompt.Build(null).ShouldBeNull();
        LanguagePrompt.Build("   ").ShouldBeNull();
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