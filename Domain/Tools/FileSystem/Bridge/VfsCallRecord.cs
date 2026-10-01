using Domain.DTOs.FileSystem;

namespace Domain.Tools.FileSystem.Bridge;

// What a call recorded: the changes it made through the mounts, and the directories a walk saw
// only part of.
public sealed record VfsCallRecord(IReadOnlyList<VfsChange> Changes, IReadOnlyList<string> Truncated)
{
    public static readonly VfsCallRecord Empty = new([], []);
}