namespace Domain.Contracts;

// A glob in progress. The matches arrive as they are found rather than as a finished collection,
// which is what lets a caller stop pulling once it has all it will report — on a tree whose root is
// a container root, that early exit is the difference between answering and enumerating everything.
public sealed class GlobWalk(IAsyncEnumerable<string> matches)
{
    public IAsyncEnumerable<string> Matches { get; } = matches;
}