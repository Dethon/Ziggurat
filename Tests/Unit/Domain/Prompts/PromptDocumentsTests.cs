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

    public static TheoryData<string, string> ShippedPlaceholders =>
        PromptDocuments.Sections
            .SelectMany(d => d.Placeholders.Order(StringComparer.Ordinal).Select(p => (d.Name, Placeholder: p)))
            .Aggregate(new TheoryData<string, string>(), (data, row) =>
            {
                data.Add(row.Name, row.Placeholder);
                return data;
            });

    // The holes the host fills — the date, an agent's name, the language, the satellites, the
    // formats, the mounts, the setup — cannot be edited out of a shipped document's words.
    [Theory]
    [MemberData(nameof(ShippedPlaceholders))]
    public void EveryShippedDocument_WithOneOfItsPlaceholdersDropped_FailsValidation(string name, string placeholder)
    {
        var source = File.ReadAllText(Path.Combine(_sourceDirectory, $"{name}.md"));
        var close = source.IndexOf("\n---\n", StringComparison.Ordinal);
        var dropped = source[..close] + source[close..].Replace($"{{{{{placeholder}}}}}", "");

        Should.Throw<FormatException>(() => PromptDocument.Parse($"{name}.md", dropped))
            .Message.ShouldContain($"{{{{{placeholder}}}}}");
    }

    [Fact]
    public void Section_ANameNoDocumentDeclares_IsRefused()
    {
        Should.Throw<InvalidOperationException>(() => PromptDocuments.Section("no_such_section"))
            .Message.ShouldContain("no_such_section");
    }
}