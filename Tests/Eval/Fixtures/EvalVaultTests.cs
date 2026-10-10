using Shouldly;
using Tests.Eval.Harness;
using Tests.Eval.Scenarios;

namespace Tests.Eval.Fixtures;

// "How many words" has no one answer: `wc -w` counts the frontmatter and a list's dash, a word
// regex over the bodies counts neither, and both are a real count over the real bytes. The shell
// scenario exists to tell a computed number from one done by eye, so every definition a script
// reaches for must pass and a guess must not.
public class EvalVaultTests
{
    private static IReadOnlyList<string> Answering(string reply) =>
        ReplyChecks.Failures(ShellScenarios.WordsAcrossTheVault.Reply!, reply);

    [Theory]
    [InlineData(331)] // wc -w over every note, frontmatter included
    [InlineData(294)] // wc -w over the bodies, or \w+ over every note
    [InlineData(289)] // words with hyphenated compounds as one, frontmatter included
    [InlineData(271)] // \w+ over the bodies
    [InlineData(268)] // words with hyphenated compounds as one, over the bodies
    [InlineData(323)] // cat *.md | wc -w: a note with no final newline runs into the next one
    public void ACountUnderAnyDefinitionAScriptUses_IsTheFoldersWordCount(int count) =>
        Answering($"Las 9 notas de la carpeta Cocina suman {count} palabras en total.").ShouldBeEmpty();

    [Fact]
    public void ACountNoDefinitionProduces_IsNotTheFoldersWordCount() =>
        Answering("Las 9 notas de la carpeta Cocina suman unas 300 palabras en total.").ShouldNotBeEmpty();
}