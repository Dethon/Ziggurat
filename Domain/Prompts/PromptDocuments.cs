namespace Domain.Prompts;

// The prompt documents this repo ships, read out of Domain's own assembly. Embedded rather than
// copied beside the binaries, for the reason the public suffix list is: they are what the code
// says to the model, not a policy a host may override — a container that quietly edited its own
// prompt would make every snapshot a statement about some other prompt. Every host that serves a
// section references Domain, so each one has them without its Dockerfile knowing.
public static class PromptDocuments
{
    private const string SectionResources = "Domain.Prompts.Sections.";
    private const string Extension = ".md";

    public static IReadOnlyList<PromptDocument> Sections { get; } = Load(SectionResources);

    private static readonly Dictionary<string, PromptDocument> _sections =
        Sections.ToDictionary(document => document.Name, StringComparer.OrdinalIgnoreCase);

    // A section's document, or a refusal: a missing one is a build that lost its words, and it has
    // to stop the host that needs them rather than degrade to a prompt nobody declared.
    public static PromptDocument Section(string name) =>
        _sections.GetValueOrDefault(name)
        ?? throw new InvalidOperationException(
            $"No prompt document declares the section '{name}'. Its words live in " +
            $"Domain/Prompts/Sections/{name}{Extension}, embedded into Domain.");

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
                        : throw new FormatException($"{file} declares the section '{document.Name}'; a document is named after its file");
                })
        ];
    }
}