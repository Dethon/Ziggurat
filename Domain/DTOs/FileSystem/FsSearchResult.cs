namespace Domain.DTOs.FileSystem;

public sealed record FsSearchResult
{
    public required string Query { get; init; }
    public required bool Regex { get; init; }
    public required string Path { get; init; }
    public required int FilesSearched { get; init; }
    public required int FilesWithMatches { get; init; }
    public required int TotalMatches { get; init; }

    // More matched than the caller asked for. Distinct from BudgetReached: this says the search
    // found what it was asked to and stopped, that says it stopped before the tree ended.
    public required bool Truncated { get; init; }

    // How much of the tree the answer covers, and whether a budget is why it stops there — spelled
    // as on the glob result, so the word means one thing. Read beside FilesSearched: a large scan
    // against zero files searched is a file pattern that excluded everything, which no flag alone
    // can explain. Defaulted rather than required, because a backend whose entries are a finite
    // in-memory set has no tree to walk away into and cannot trip either budget.
    public int EntriesScanned { get; init; }

    public bool BudgetReached { get; init; }

    public required IReadOnlyList<FsSearchFileResult> Results { get; init; }
}

public sealed record FsSearchFileResult
{
    public required string File { get; init; }
    public int? MatchCount { get; init; }
    public IReadOnlyList<FsSearchMatch>? Matches { get; init; }
}

public sealed record FsSearchMatch
{
    public required int Line { get; init; }
    public required string Text { get; init; }
    public string? Section { get; init; }
    public FsSearchContext? Context { get; init; }
}

public sealed record FsSearchContext
{
    public required IReadOnlyList<string> Before { get; init; }
    public required IReadOnlyList<string> After { get; init; }
}