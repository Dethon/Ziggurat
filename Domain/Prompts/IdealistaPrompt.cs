namespace Domain.Prompts;

// Property search (Sections/idealista_guide.md), served by mcp-idealista.
public static class IdealistaPrompt
{
    public const string Name = "idealista_guide";

    public const string Description =
        "Guide for using Idealista property search for real estate market research";

    private static readonly PromptDocument _document = PromptDocuments.Section(Name);

    public static readonly string SystemPrompt = _document.Text;
}