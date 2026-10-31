using Domain.Prompts;
using Shouldly;
using Tests.Integration.McpServers;

namespace Tests.Unit.Domain.Prompts;

// The shipped documents, read the way the deployment reads them: out of Domain's own assembly. The
// manifest is what they say, so a section with no document, or a document nobody embedded, is a
// prompt the deployment does not have.
public class PromptDocumentsTests
{
    private static readonly string _sourceDirectory =
        Path.Combine(McpServerRegistrations.RepoRoot, "Domain", "Prompts", "Sections");

    private static readonly string _skillDirectory =
        Path.Combine(McpServerRegistrations.RepoRoot, "Domain", "Prompts", "Skills");

    [Fact]
    public void Sections_AreWhatTheManifestDeclares()
    {
        PromptManifest.Declarations.Select(d => d.Name)
            .ShouldBe(PromptDocuments.Sections.Select(d => d.Name), ignoreOrder: true);

        foreach (var document in PromptDocuments.Sections)
        {
            var declared = PromptManifest.Find(document.Name).ShouldNotBeNull();
            declared.TokenBudget.ShouldBe(document.AsSection().TokenBudget, document.Name);
            declared.Claims.ShouldBe(document.Claims, document.Name);
        }
    }

    // Embedded by a wildcard, so a document the wildcard missed would simply not ship.
    [Fact]
    public void Sections_EveryDocumentInTheSourceTree_IsShipped_UnderItsOwnName()
    {
        var files = Directory.EnumerateFiles(_sourceDirectory, "*.md")
            .Select(Path.GetFileNameWithoutExtension)
            .ToList();

        files.ShouldNotBeEmpty();
        PromptDocuments.Sections.Select(d => d.Name).ShouldBe(files, ignoreOrder: true);
    }

    [Fact]
    public void Skills_AreWhatTheManifestDeclares()
    {
        PromptManifest.Skills.Select(s => s.Name)
            .ShouldBe(PromptDocuments.Skills.Select(d => d.Name), ignoreOrder: true);

        foreach (var document in PromptDocuments.Skills)
        {
            var declared = PromptManifest.FindSkill(document.Name).ShouldNotBeNull();
            declared.ShouldBeEquivalentTo(document.AsSkill());
        }
    }

    [Fact]
    public void Skills_EveryDocumentInTheSourceTree_IsShipped_UnderItsOwnName()
    {
        var files = Directory.EnumerateFiles(_skillDirectory, "*.md")
            .Select(Path.GetFileNameWithoutExtension)
            .ToList();

        files.ShouldNotBeEmpty();
        PromptDocuments.Skills.Select(d => d.Name).ShouldBe(files, ignoreOrder: true);
    }

    [Fact]
    public void Skill_ANameNoDocumentDeclares_IsRefused()
    {
        Should.Throw<InvalidOperationException>(() => PromptDocuments.Skill("no-such-skill"))
            .Message.ShouldContain("no-such-skill");
    }

    public static TheoryData<string, string> ShippedPlaceholders =>
        PromptDocuments.Sections.Select(d => (Folder: "Sections", Document: d))
            .Concat(PromptDocuments.Skills.Select(d => (Folder: "Skills", Document: d)))
            .SelectMany(row => row.Document.Placeholders.Order(StringComparer.Ordinal)
                .Select(p => (File: $"{row.Folder}/{row.Document.Name}.md", Placeholder: p)))
            .Aggregate(new TheoryData<string, string>(), (data, row) =>
            {
                data.Add(row.File, row.Placeholder);
                return data;
            });

    // The holes the host fills — the date, an agent's name, the language, the satellites, the
    // formats, the mounts, the setup, a skill's time zone and sandbox paths — cannot be edited out
    // of a shipped document's words.
    [Theory]
    [MemberData(nameof(ShippedPlaceholders))]
    public void EveryShippedDocument_WithOneOfItsPlaceholdersDropped_FailsValidation(string file, string placeholder)
    {
        var name = Path.GetFileName(file);
        var source = File.ReadAllText(Path.Combine(McpServerRegistrations.RepoRoot, "Domain", "Prompts", file));
        var close = source.IndexOf("\n---\n", StringComparison.Ordinal);
        var dropped = source[..close] + source[close..].Replace($"{{{{{placeholder}}}}}", "");

        Should.Throw<FormatException>(() => PromptDocument.Parse(name, dropped))
            .Message.ShouldContain($"{{{{{placeholder}}}}}");
    }

    [Fact]
    public void Section_ANameNoDocumentDeclares_IsRefused()
    {
        Should.Throw<InvalidOperationException>(() => PromptDocuments.Section("no_such_section"))
            .Message.ShouldContain("no_such_section");
    }
}