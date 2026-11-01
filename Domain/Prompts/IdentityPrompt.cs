namespace Domain.Prompts;

public static class IdentityPrompt
{
    private static readonly PromptTemplate _template =
        PromptDocuments.Section(PromptManifest.Identity).Template("name", "description");

    public static string Build(string name, string? description) =>
        _template.Fill(
            ("name", name),
            ("description", string.IsNullOrWhiteSpace(description) ? "" : $" {description}"));
}