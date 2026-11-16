namespace Domain.Prompts;

// The prompt documents this repo ships, read out of Domain's own assembly. Embedded rather than
// copied beside the binaries, for the reason the public suffix list is: they are what the code
// says to the model, not a policy a host may override — a container that quietly edited its own
// prompt would make every snapshot a statement about some other prompt. Every host that serves a
// section or a skill references Domain, so each one has them without its Dockerfile knowing.
//
// A skill's document sits here rather than beside the server that serves it: the server ships the
// skill (docs/adr/0039), but the agent, the eval and every budget test read its declaration from
// the manifest, and none of them references a server.
public static class PromptDocuments
{
    private const string SectionResources = "Domain.Prompts.Sections.";
    private const string SkillResources = "Domain.Prompts.Skills.";
    private const string AgentTemplateResources = "Domain.Prompts.Agents.";
    private const string Extension = ".md";

    public static IReadOnlyList<PromptDocument> Sections { get; } = Load(SectionResources);

    public static IReadOnlyList<PromptDocument> Skills { get; } = Load(SkillResources);

    // The agent templates a household's agent starts from (AgentTemplates), Agents/<name>.md.
    public static IReadOnlyList<PromptDocument> AgentTemplates { get; } = Load(AgentTemplateResources);

    private static readonly Dictionary<string, PromptDocument> _sections =
        Sections.ToDictionary(document => document.Name, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, PromptDocument> _skills =
        Skills.ToDictionary(document => document.Name, StringComparer.OrdinalIgnoreCase);

    // A section's document, or a refusal: a missing one is a build that lost its words, and it has
    // to stop the host that needs them rather than degrade to a prompt nobody declared.
    public static PromptDocument Section(string name) =>
        _sections.GetValueOrDefault(name)
        ?? throw new InvalidOperationException(
            $"No prompt document declares the section '{name}'. Its words live in " +
            $"Domain/Prompts/Sections/{name}{Extension}, embedded into Domain.");

    // A skill's document, refused the same way: a server serving a skill with no words would
    // advertise a load that returns nothing.
    public static PromptDocument Skill(string name) =>
        _skills.GetValueOrDefault(name)
        ?? throw new InvalidOperationException(
            $"No prompt document declares the skill '{name}'. Its words live in " +
            $"Domain/Prompts/Skills/{name}{Extension}, embedded into Domain.");

    private static IReadOnlyList<PromptDocument> Load(string prefix)
    {
        var assembly = typeof(PromptDocuments).Assembly;

        return
        [
            .. assembly.GetManifestResourceNames()
                .Where(resource => resource.StartsWith(prefix, StringComparison.Ordinal)
                                   && resource.EndsWith(Extension, StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)
                .Select(resource =>
                {
                    var file = resource[prefix.Length..];
                    using var stream = assembly.GetManifestResourceStream(resource)!;
                    using var reader = new StreamReader(stream);
                    var document = PromptDocument.Parse(file, reader.ReadToEnd());

                    return document.Name + Extension == file
                        ? document
                        : throw new FormatException($"{file} declares '{document.Name}'; a document is named after its file");
                })
        ];
    }
}