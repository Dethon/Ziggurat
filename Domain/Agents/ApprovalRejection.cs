namespace Domain.Agents;

// What a call the person turned down leaves behind. The tool's result is what the model reads, and
// the turn ends on it; the line is what a transcript shows in its place, since the turn has no text of
// its own and a reload would otherwise show the question with nothing under it.
public static class ApprovalRejection
{
    private const string Prefix = "Tool execution was rejected by user: ";
    private const string Suffix = ". Waiting for new input.";

    public static string ResultFor(string toolName) => $"{Prefix}{toolName}{Suffix}";

    // A built app's ask asks nobody, so nobody turned the call down and no input is coming: the model is
    // told so and to answer without it, since the app waits on its words. Not the person's prefix, so a
    // transcript never shows it as turned down.
    public static string UnaskableResultFor(string toolName) =>
        $"Tool execution was refused: {toolName} needs a person's approval, which an app's request cannot ask for, "
        + "so it was not run. Carry on without it: answer with what you have, or say it could not be done.";

    public static string? ToolOf(string? result) =>
        result is not null && result.StartsWith(Prefix, StringComparison.Ordinal) && result.EndsWith(Suffix, StringComparison.Ordinal)
            ? result[Prefix.Length..^Suffix.Length]
            : null;

    public static string Line(string toolName) => $"{toolName.Split("__").Last()} was not run: it was turned down.";
}