namespace Domain.Prompts;

// Everything about a prompt section except its words. Declaring it apart from the text is what
// lets a section whose words arrive at runtime — an MCP server's prompt, fetched when the session
// warms up — still have a purpose, a place, a budget and a stated position in every conflict it
// takes part in. The words are bound to it with `Bind`, and a `PromptSection` is the pair.
public sealed record PromptDeclaration
{
    public required string Name { get; init; }

    // One line saying what this section is for. Read by a person deciding whether a change belongs
    // in this section or a new one, and by the snapshot header.
    public required string Purpose { get; init; }

    public required PromptPriority Priority { get; init; }

    // What this section may cost. Not a limit the assembly enforces — an over-budget section still
    // reaches the model, because a prompt that grew is a worse outcome than a turn that fails —
    // but it is reported at runtime and it fails the build in the budget tests.
    public required int TokenBudget { get; init; }

    public PromptAudience Audience { get; init; } = PromptAudience.Everyone;

    public ConflictPolicy Conflict { get; init; } = ConflictPolicy.None;

    // The compose service that serves this section over MCP, for text this repo does not assemble
    // itself. Null means the words are built here. It is what the staleness tests walk: a server
    // added to an agent's endpoints without a declaration here has a prompt nobody budgeted.
    public string? ServedBy { get; init; }

    // False for a prompt that arrived from a server the manifest says nothing about. It still
    // reaches the model; it is simply unbudgeted and unplaced, and the assembly reports it.
    public bool Declared { get; init; } = true;

    public PromptSection Bind(string text) => new(this, text);
}

public sealed record PromptSection(PromptDeclaration Declaration, string Text)
{
    public string Name => Declaration.Name;

    public PromptPriority Priority => Declaration.Priority;

    public int TokenCount => PromptTokens.Estimate(Text);

    public bool IsOverBudget => TokenCount > Declaration.TokenBudget;
}