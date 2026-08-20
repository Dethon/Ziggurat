namespace Domain.Prompts;

public static class PromptTokens
{
    // The same four-characters-per-token estimate the truncator bills a message with, and
    // deliberately the same one: a budget stated in tokens the truncator would not recognise
    // measures a different prompt from the one that competes with the conversation for context.
    // It is an estimate on both sides, and the budgets are set with headroom for that.
    public static int Estimate(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;
}