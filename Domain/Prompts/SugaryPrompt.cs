namespace Domain.Prompts;

// How the sugary agent estimates a meal (Sections/sugary_prompt.md), served by mcp-sugary: the
// standalone Sugary app's system prompt word for word, whose dose rule beats the core directive.
public static class SugaryPrompt
{
    public const string Name = "sugary_prompt";

    public const string Description =
        "How a meal's carbohydrate is estimated for a person with type 1 diabetes, and that no insulin dose is ever suggested";

    private static readonly PromptDocument _document = PromptDocuments.Section(Name);

    public static readonly string Prompt = _document.Text;

    public static readonly PromptClaim NeverSuggestsADose =
        _document.Claim("sugary.never-suggests-a-dose");

    public static readonly PromptClaim AsksAboutAmountsFirst =
        _document.Claim("sugary.asks-about-amounts-first");

    public static readonly PromptClaim LeavesOutCountedFoods =
        _document.Claim("sugary.leaves-out-counted-foods");
}