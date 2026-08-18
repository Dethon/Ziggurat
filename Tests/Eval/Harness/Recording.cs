using Domain.Contracts;
using Infrastructure.Agents.ChatClients;

namespace Tests.Eval.Harness;

// What one run of a scenario produced. Every assertion in the suite reads this and nothing else,
// so what a scenario checks does not depend on where a tool lives or how the agent is built.
public sealed class Recording : IToolInvocationObserver
{
    private readonly List<ToolInvocation> _calls = [];
    private readonly Lock _gate = new();

    // Sorted by the position the seam stamped, not by the order the observations arrived:
    // concurrent invocations complete in whichever order they finish, and the contract a
    // scenario declares is about the order they were issued in.
    public IReadOnlyList<ToolInvocation> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.OrderBy(c => c.Sequence).ToArray();
            }
        }
    }

    // What the turn was sent with and what answered it. Last write wins: a scenario is one turn,
    // and a harness that re-used a recording across turns would be reporting the first turn's
    // prompt beside the last turn's calls.
    public string? SystemPrompt { get; private set; }

    public ServedRoute? Route { get; private set; }

    // The home before and after the turn, set by the runner that drove it. Two snapshots rather
    // than a list of observed calls, because the question a scenario asks — did anything else
    // move — is only answerable against the whole home: a service call whose script cascades into
    // three devices reports one call and changes three states.
    public IReadOnlyDictionary<string, string> StateBefore { get; set; } =
        new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> StateAfter { get; set; } =
        new Dictionary<string, string>();

    // The vault before and after the turn, keyed by the path the agent sees. Notes are asserted on
    // by their own text rather than through the calls that wrote them: a surgical edit and a
    // whole-file rewrite are the same tool call, and only the file afterwards says which happened.
    public IReadOnlyDictionary<string, string> FilesBefore { get; set; } =
        new Dictionary<string, string>();

    public IReadOnlyDictionary<string, string> FilesAfter { get; set; } =
        new Dictionary<string, string>();

    // The agent's own answer, set by the runner that drove it: it comes back as the response
    // rather than through the seam, because a reply is what the agent returned and not something
    // observed on its way past.
    public string Reply { get; set; } = "";

    public void OnTurn(TurnObservation turn)
    {
        lock (_gate)
        {
            SystemPrompt = turn.SystemPrompt ?? SystemPrompt;
            Route = turn.Route ?? Route;
        }
    }

    public void OnInvoked(ToolInvocation invocation)
    {
        lock (_gate)
        {
            _calls.Add(invocation);
        }
    }
}