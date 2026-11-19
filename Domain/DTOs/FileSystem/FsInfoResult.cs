using System.Text.Json.Serialization;

namespace Domain.DTOs.FileSystem;

public sealed record FsInfoResult
{
    public required bool Exists { get; init; }
    public required string Path { get; init; }
    public bool? IsDirectory { get; init; }
    public long? Size { get; init; }
    public string? LastModified { get; init; }

    // An action file: it runs and cannot be opened. Defaulted and left off the wire while false,
    // so every backend without actions answers byte for byte what it answered before the flag
    // existed, and a response from an older server still deserializes.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Executable { get; init; }

    // An entry is here that this caller may not see (with Exists false): its name is taken, and nothing
    // more about it is said. A filesystem sharing a mount point asks so before it creates an entry of
    // that name. Off the wire while false, as Executable is.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Hidden { get; init; }
}