namespace Domain.Prompts;

// Everything one agent's system prompt is made of, at the moment it is made. Sections whose text
// belongs to somebody else — a feature's, a session's mounts, an MCP server's — arrive already
// bound to their declarations; the ones this composer writes itself are the four that only exist
// in the context of a running agent.
public sealed record PromptContext
{
    // The configured agent id, for sections that declare an audience. Not the display name, which
    // carries the conversation id and belongs to metrics.
    public required string AgentId { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    // Sections this agent's configuration selected by name (`promptSections`).
    public IReadOnlyList<PromptSection> Selected { get; init; } = [];

    public IReadOnlyList<PromptSection> Domain { get; init; } = [];

    public IReadOnlyList<PromptSection> FileSystem { get; init; } = [];

    public IReadOnlyList<PromptSection> Client { get; init; } = [];

    public string? CustomInstructions { get; init; }

    public string? Language { get; init; }

    public required DateTimeOffset Now { get; init; }
}

// One place that turns a context into a prompt. Nothing here decides an order — every section
// carries its own band and `PromptAssembly` sorts by it — so adding a section is a declaration
// plus a line that binds its text, and never an argument about where a Prepend should go.
public static class PromptComposer
{
    public static PromptAssembly Compose(PromptContext context)
    {
        IEnumerable<PromptSection> sections =
        [
            PromptManifest.Bind(PromptManifest.CoreDirective, BasePrompt.Instructions),
            .. Identity(context),
            .. context.Domain,
            .. context.FileSystem,
            .. context.Client,
            PromptManifest.Bind(PromptManifest.Date, Today(context.Now)),
            .. Optional(PromptManifest.CustomInstructions, context.CustomInstructions),
            .. context.Selected,
            .. Optional(PromptManifest.Language, LanguagePrompt.Build(context.Language))
        ];

        return PromptAssembly.Build(context.AgentId, sections);
    }

    private static IEnumerable<PromptSection> Identity(PromptContext context) =>
        Optional(
            PromptManifest.Identity,
            string.IsNullOrWhiteSpace(context.Name)
                ? null
                : IdentityPrompt.Build(context.Name, context.Description));

    private static IEnumerable<PromptSection> Optional(string name, string? text) =>
        string.IsNullOrEmpty(text) ? [] : [PromptManifest.Bind(name, text)];

    private static string Today(DateTimeOffset now) =>
        $"Today is {now.ToString("dddd, yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)}.";
}