using Domain.Tools;

namespace Infrastructure.Agents.ChatClients;

// The calls of one request that have not finished, in the order the model wrote them. A call
// takes its place the moment it is handed over and is told which earlier calls it has to wait
// for: the ones still running whose footprint conflicts with its own, and no others. So a
// removal and the create written after it happen in that order, while a read of something else
// beside them runs alongside both.
//
// A place is taken in the order calls arrive, and that is the order they were written: the
// function-invoking client starts a message's calls one after another on one thread, each
// running as far as its first real wait before the next begins, which is why a place has to be
// taken before anything is awaited. A call that never arrives — one naming a tool nothing
// serves — takes no place, so nothing can be left waiting for it. And nothing here can deadlock:
// a call waits only for calls that arrived before it, each of which gives its place up however
// it ends.
internal sealed class WrittenOrder
{
    private readonly Lock _gate = new();
    private readonly List<(CallFootprint Footprint, Task Finished)> _running = [];

    public Place Take(CallFootprint footprint)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _running.RemoveAll(call => call.Finished.IsCompleted);
            var ahead = _running
                .Where(call => call.Footprint.ConflictsWith(footprint))
                .Select(call => call.Finished)
                .ToArray();
            _running.Add((footprint, finished.Task));
            return new Place(Task.WhenAll(ahead), finished);
        }
    }

    public sealed class Place(Task ahead, TaskCompletionSource finished)
    {
        // The earlier calls this one conflicts with have all finished — returned, thrown, been
        // refused or been cancelled; how they ended is theirs to report, never this call's.
        public Task TurnAsync(CancellationToken ct) => ahead.WaitAsync(ct);

        public void GiveUp() => finished.TrySetResult();
    }
}