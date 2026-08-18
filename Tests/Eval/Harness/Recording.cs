using Domain.Contracts;
using Domain.DTOs;

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

    public void OnInvoked(ToolInvocation invocation)
    {
        lock (_gate)
        {
            _calls.Add(invocation);
        }
    }
}