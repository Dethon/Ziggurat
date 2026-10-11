namespace Infrastructure.Agents.ChatClients;

// One tool invocation as the function-invoking client saw it: what was called, with what, and
// what came back. Both failure shapes are invocations too — a tool that threw and a call naming
// a tool nothing serves are exactly the calls an eval has to see, and both leave no result.
public sealed record ToolInvocation
{
    // The position the call was issued at, stamped where the calls of one iteration are still in
    // the order the model asked for them. Concurrent invocations complete out of order, so an
    // observer that numbered them on arrival would report an order the model never chose.
    public required int Sequence { get; init; }

    public required string ToolName { get; init; }

    // The arguments as JSON, not as a parsed shape: a scenario matches on what the model wrote,
    // and re-typing it here would lose the difference between an absent key and a null one.
    public required string Arguments { get; init; }

    public string? Result { get; init; }

    public string? Error { get; init; }

    public required ToolInvocationOutcome Outcome { get; init; }

    // Whose call this was, as the agent's metrics name it. One observer hears every agent a host
    // builds — a worker's client beside its parent's, each numbering its own calls from zero —
    // so without this a delegated task's calls read as the parent's.
    public string? AgentId { get; init; }
}

public enum ToolInvocationOutcome
{
    Completed,
    Failed,
    NotFound
}