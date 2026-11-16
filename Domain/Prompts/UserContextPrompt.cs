namespace Domain.Prompts;

// Who the conversation belongs to, for every user-scoped tool call in it.
public static class UserContextPrompt
{
    private static readonly PromptTemplate _template =
        PromptDocuments.Section(PromptManifest.UserContext).Template("user");

    public static string Build(string userId) => _template.Fill(("user", userId));
}