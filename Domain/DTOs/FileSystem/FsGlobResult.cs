namespace Domain.DTOs.FileSystem;

public sealed record FsGlobResult
{
    public required IReadOnlyList<string> Entries { get; init; }

    // More matched than the response carries. Distinct from BudgetReached, and the two answer
    // different questions: this one says the walk found more than it could report, that one says
    // the walk stopped before the tree ended.
    public required bool Truncated { get; init; }

    // Matches found before the walk stopped, which on a bounded walk is not the tree's true count.
    public required int Total { get; init; }

    // How much of the tree the answer covers, and whether a budget is why it stops there. Defaulted
    // rather than required: a backend whose entries are a finite in-memory set enumerates nothing
    // and cannot trip a budget, and a response from an older server still deserializes.
    public int EntriesScanned { get; init; }

    public bool BudgetReached { get; init; }

    // The entries that are action files, spelled exactly as they are in Entries so the tool
    // translates both the same way. A parallel list rather than a mark on each entry: an entry is
    // a path ready for the next tool, and a decorated one would not be. Null (left off the wire)
    // on every backend without actions, so their answers are unchanged.
    public IReadOnlyList<string>? Executables { get; init; }

    // What this answer leaves out that a reader would otherwise take as absent - a part of the
    // mount that could not be asked this time. Null (left off the wire) whenever the answer is whole.
    public string? Note { get; init; }

    // The filesystems of a shared mount point that did not answer this listing (unreachable, or
    // failing for now), by name, so an empty or short answer is never read as "there is nothing".
    // A member that refused the caller is not here: for that caller it holds nothing. Null (left
    // off the wire) on every other answer.
    public IReadOnlyList<string>? Unavailable { get; init; }
}