using System.Text.Json.Nodes;

namespace Domain.DTOs.FileSystem;

// One change a sandbox command made through the mounts, as the bridge decided it. Bash swallows a
// refusal at close (`echo x > f` exits 0 whatever the mount said), so this list on the exec result
// is the only reliable record of what a command really changed.
public sealed record VfsChange
{
    public required string Path { get; init; }
    public required string Operation { get; init; }
    public required string Status { get; init; }

    // Where a move went; the other operations have one path.
    public string? Destination { get; init; }

    // The mount's own refusal, exactly the envelope the matching tool call would have answered.
    public JsonObject? Error { get; init; }

    public static class Operations
    {
        public const string Write = "write";
        public const string Create = "create";
        public const string Delete = "delete";
        public const string Move = "move";
        public const string Action = "action";
    }

    public static class Statuses
    {
        public const string Applied = "applied";
        public const string Refused = "refused";

        // Arrived after the command was killed or the call cancelled, and so never applied.
        public const string Dropped = "dropped";
    }
}