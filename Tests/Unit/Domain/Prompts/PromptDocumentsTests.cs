using Domain.DTOs.Tunnel;
using Domain.Outposts;
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

    // What a new household's agents start from: a barebones Jonas its owner names, and Sugary.
    [Fact]
    public void AgentTemplates_AreABarebonesJonasAndSugary_EachADefinition()
    {
        PromptDocuments.AgentTemplates.Select(d => d.Name).ShouldBe(["jonas", "sugary"], ignoreOrder: true);

        var jonas = AgentTemplates.Named("jonas").ShouldNotBeNull();
        jonas.Id.ShouldBe("jonas");
        jonas.Name.ShouldBe("Jonas");
        jonas.Description.ShouldNotBeNullOrWhiteSpace();
        jonas.EnabledFeatures.ShouldBe(["filesystem", "subagents", "memory"]);
        jonas.ProviderRouting.ShouldNotBeNull().Ignore.ShouldBe(["relace", "morph"]);
        jonas.UsesOutposts.ShouldBeTrue();

        var sugary = AgentTemplates.Named("sugary").ShouldNotBeNull();
        sugary.NoTools.ShouldBeTrue();
        sugary.McpServerEndpoints.ShouldBe(["http://mcp-sugary:8080/mcp"]);
        AgentTemplates.Named("jack").ShouldBeNull();
    }

    // Barebones: a template dials the deployment's shared servers at their compose addresses, and a
    // household's own servers by their server name — the tenant kit's, which each turn reaches only
    // where the household set it up. Never anything else, and never a machine: outposts are the
    // agent's by uses-outposts, not by name.
    [Fact]
    public void AgentTemplates_DialTheSharedServers_AndTheKitsOwnServersByName()
    {
        AgentTemplates.All.SelectMany(t => t.McpServerEndpoints)
            .ShouldAllBe(endpoint => endpoint.StartsWith("http://", StringComparison.Ordinal)
                                     || (TunnelCredentialProtocol.IsServerName(endpoint)
                                         && OutpostServerName.NameOf(endpoint) == null));
        AgentTemplates.Named("jonas")!.McpServerEndpoints.ShouldContain("vault");
    }

    [Fact]
    public void AgentTemplates_EveryDocumentInTheSourceTree_IsShipped_UnderItsOwnName()
    {
        var files = Directory.EnumerateFiles(
                Path.Combine(McpServerRegistrations.RepoRoot, "Domain", "Prompts", "Agents"), "*.md")
            .Select(Path.GetFileNameWithoutExtension)
            .ToList();

        files.ShouldNotBeEmpty();
        PromptDocuments.AgentTemplates.Select(d => d.Name).ShouldBe(files, ignoreOrder: true);
    }

    // A template's header is what the definition carries; a key it does not know is a typo that would
    // otherwise be read as nothing.
    [Fact]
    public void AgentTemplate_AnUnknownKey_IsRefused()
    {
        var document = PromptDocument.Parse("x.md", "---\nname: x\nagent-name: X\nmodel: m\nmodle: m\n---\n\nWords.");

        Should.Throw<FormatException>(() => AgentTemplates.Read(document)).Message.ShouldContain("modle");
    }
}