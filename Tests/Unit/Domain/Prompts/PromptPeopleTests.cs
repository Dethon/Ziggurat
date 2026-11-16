using System.Text.RegularExpressions;
using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

// Every prompt document is every household's: the sections and skills a model reads, the templates a
// household's agents start from, and what each declares about itself. They were written for the home,
// and spoke of its one person — "her log", "her glucose", "a meal she photographs" — and named
// another of its people, so a member of another household was answered about "her" log, which was his.
// They speak of the person asking, or of the thing itself, never of one of the home's people.
public sealed partial class PromptPeopleTests
{
    [GeneratedRegex(@"\b(she|her|hers|herself|fran)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OnePerson();

    public static TheoryData<string> Documents() =>
        [.. PromptDocuments.Sections.Concat(PromptDocuments.Skills).Concat(PromptDocuments.AgentTemplates).Select(d => d.Source)];

    [Theory]
    [MemberData(nameof(Documents))]
    public void APromptDocument_SpeaksOfNoOneOfTheHomesPeople(string source)
    {
        var document = PromptDocuments.Sections.Concat(PromptDocuments.Skills).Concat(PromptDocuments.AgentTemplates)
            .Single(d => d.Source == source);
        // A claim's id is a key the scenarios cite, not words anybody reads, so it may keep its name.
        var read = new[] { document.Body }
            .Concat(document.Parts.Values)
            .Concat(document.Header.Where(h => h.Key != "claims").Select(h => h.Value))
            .Concat(document.Claims.Select(c => c.Statement));

        read.SelectMany(text => OnePerson().Matches(text).Select(m => m.Value)).ShouldBeEmpty(source);
    }
}