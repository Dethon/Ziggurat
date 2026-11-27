namespace Domain.DTOs.WebChat;

// How a tool call answered: ran, ran and failed (a thrown call or an `ok:false` envelope), or never ran
// because nobody let it (a person turned it down, or an app's ask could ask nobody).
public enum ToolCallOutcome
{
    Done,
    Failed,
    Declined
}

// One tool call as a page draws it: what was called, with what, and what came back. The call and its
// answer travel apart (the call as the model writes it, the answer once it ran), so each is a step of
// its own under the same CallId, and a page folds them together (Merge, Upsert). Arguments and Result
// are already shaped for reading (ToolSteps): a long value is cut where it stands, saying how much was
// left out, so neither a file write's content nor a web page's text travels whole.
public sealed record ToolCallStep
{
    public required string CallId { get; init; }

    // As the model called it, server-qualified (mcp__timers__timer_set); none on an answer alone.
    public string? Name { get; init; }

    // A JSON object; none on an answer alone.
    public string? Arguments { get; init; }

    // JSON when the tool answered JSON, else its text; none until it answered.
    public string? Result { get; init; }

    public ToolCallOutcome? Outcome { get; init; }

    public bool HasAnswered => Outcome is not null;

    // The later step's fields over this one's: an answer keeps its call's name and arguments.
    public ToolCallStep Merge(ToolCallStep later) => this with
    {
        Name = later.Name ?? Name,
        Arguments = later.Arguments ?? Arguments,
        Result = later.Result ?? Result,
        Outcome = later.Outcome ?? Outcome
    };

    // A step folded into a list by its call: the call it answers updated in place, or appended. Doing it
    // twice changes nothing, so a page handed a step again (a resume) draws it once.
    public static IReadOnlyList<ToolCallStep> Upsert(IReadOnlyList<ToolCallStep>? steps, ToolCallStep step)
    {
        var list = steps ?? [];
        var at = list.Select((s, i) => (s, i)).FirstOrDefault(x => x.s.CallId == step.CallId, (null!, -1)).Item2;
        return at < 0 ? [.. list, step] : [.. list.Take(at), list[at].Merge(step), .. list.Skip(at + 1)];
    }

    public static IReadOnlyList<ToolCallStep> Upsert(IReadOnlyList<ToolCallStep>? steps, IEnumerable<ToolCallStep> more) =>
        more.Aggregate(steps ?? [], Upsert);
}

// One thing a turn did on its way to its words, as the history keeps it: some reasoning, or a tool call
// with its answer. Exactly one of the two is set.
public sealed record TurnStep(string? Reasoning = null, ToolCallStep? Tool = null);