namespace Domain.Prompts;

// The vocabulary a section uses to say what it legislates. Two sections claiming the same rule
// contradict each other unless one of them names the other as overridden, and that is the whole
// mechanism — there is no scoring and no merge. Constants rather than free strings so a typo is a
// build error instead of a rule nobody claims.
public static class PromptRules
{
    // How an answer is shaped on the page: markdown, headings, lists, code blocks.
    public const string Formatting = "formatting";

    // How long an answer is and what it may leave out.
    public const string Verbosity = "verbosity";

    // Which language the reply is written in.
    public const string Language = "language";

    // When to call a tool, when to delegate, and what to say while it runs.
    public const string ToolUse = "tool-use";

    // Whether a request may be declined or hedged.
    public const string Refusals = "refusals";

    // What may be said about what is remembered.
    public const string Memory = "memory";
}