using System.Text.Json.Nodes;

namespace Domain.DTOs.FileSystem;

// One statement's answer over a mount's tables: what the columns are, the rows that fit under the
// cap, and how many there were in all, so a cut answer is never read as a whole one.
public sealed record FsQueryResult
{
    public required string Path { get; init; }
    public required IReadOnlyList<FsQueryColumn> Columns { get; init; }
    public required IReadOnlyList<IReadOnlyList<JsonNode?>> Rows { get; init; }
    public required int RowCount { get; init; }
    public required bool Truncated { get; init; }
    public string? Suggestion { get; init; }
}

// A column as the engine typed it, in the engine's own words (VARCHAR, DOUBLE, TIMESTAMPTZ): the
// type is what tells a reader how to compare and add the values.
public sealed record FsQueryColumn(string Name, string Type);