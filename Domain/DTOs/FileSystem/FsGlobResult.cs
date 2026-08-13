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
}