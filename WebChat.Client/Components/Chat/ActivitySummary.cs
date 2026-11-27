namespace WebChat.Client.Components.Chat;

// What a reply's activity says in one line, folded: while it runs, what it is doing now and the latest
// of it; once over, what it did — how many tools, which, and how many of them failed.
// Ended: a reply that stopped before it answered, by what stopped it — a call the person turned down, or
// a steer or a stop (interrupted).
public sealed record ActivitySummary(
    string Title, string? Preview, int Tools, int Failed, IReadOnlyList<string> ToolNames, string? Ended)
{
    private const int PreviewLength = 140;
    private const int NamesShown = 3;

    public static ActivitySummary Of(IReadOnlyList<ActivityStep> steps, bool live, bool answered)
    {
        var tools = steps.Where(s => s.Tool is not null).Select(s => s.Tool!).ToList();
        var names = tools.Select(t => ToolDisplay.NameOf(t.Name).Bare).Distinct().ToList();
        var failed = tools.Count(t => t.Outcome is Domain.DTOs.WebChat.ToolCallOutcome.Failed);
        var shown = names.Count <= NamesShown ? names : [.. names.Take(NamesShown), $"+{names.Count - NamesShown}"];
        return new ActivitySummary(
            live ? Doing(steps) : Did(steps, tools.Count),
            live ? PreviewOf(steps.LastOrDefault()) : null,
            tools.Count, failed, shown,
            live || answered ? null
            : tools.LastOrDefault()?.Outcome is Domain.DTOs.WebChat.ToolCallOutcome.Declined ? "Turned down"
            : "Interrupted");
    }

    private static string Doing(IReadOnlyList<ActivityStep> steps) => steps.LastOrDefault() switch
    {
        null => "Working",
        { Kind: ActivityKind.Reasoning } => "Thinking",
        { Kind: ActivityKind.Words } => "Writing",
        { Tool: { HasAnswered: false } tool } => $"Running {ToolDisplay.NameOf(tool.Name).Bare}",
        _ => "Working"
    };

    private static string Did(IReadOnlyList<ActivityStep> steps, int tools) =>
        tools > 0 ? $"Used {tools} tool{(tools == 1 ? "" : "s")}"
        : steps.Any(s => s.Kind == ActivityKind.Reasoning) ? "Reasoning"
        : "Worked";

    private static string? PreviewOf(ActivityStep? step)
    {
        var text = step switch
        {
            { Tool: { } tool } => ToolDisplay.Summary(tool.Arguments),
            { Text: { } words } => words.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(),
            _ => null
        };
        return string.IsNullOrWhiteSpace(text) ? null
            : text.Length <= PreviewLength ? text
            : "…" + text[^PreviewLength..];
    }
}

// Which groups, steps and calls a person opened, kept by the page rather than by each component, so a
// group stays as it was left when its reply ends and its rows are drawn again from the kept messages.
public sealed class ActivityExpansion
{
    private readonly HashSet<string> _open = [];

    public bool IsOpen(string key) => _open.Contains(key);

    public void Toggle(string key)
    {
        if (!_open.Remove(key))
        {
            _open.Add(key);
        }
    }

    public void Clear() => _open.Clear();
}