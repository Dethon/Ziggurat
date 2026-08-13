namespace Domain.Contracts;

// A glob in progress. The matches arrive as they are found rather than as a finished collection,
// which is what lets a caller stop pulling once it has all it will report — on a tree whose root is
// a container root, that early exit is the difference between answering and enumerating everything.
public sealed class GlobWalk
{
    // The walk hands itself to the sequence, so the counters below belong to the code doing the
    // walking. The sequence is lazy, so nothing reads them before the caller pulls.
    public GlobWalk(Func<GlobWalk, IAsyncEnumerable<string>> matches) => Matches = matches(this);

    public IAsyncEnumerable<string> Matches { get; }

    // What the walk cost, updated as it enumerates and final once the sequence ends or is
    // abandoned. Only the walk writes them. A caller that stopped early still reads how much of the
    // tree its answer covers, which is the one thing a finished collection could never tell it.
    public int EntriesScanned { get; set; }

    public bool BudgetReached { get; set; }
}