namespace Domain.Tools.FileSystem.Bridge;

// How large a file a sandbox command may read or write through the mounts. The bridge carries a
// file whole — one request each way, held in the agent's memory while it is — so without a ceiling
// `grep -r` over a media library pulls every film through the agent. Documents fit; media does
// not, and a command that reaches for it is told so (EFBIG) rather than served slowly. The file
// tools are not bound by this: a copy streams.
public sealed record VfsBridgeSettings
{
    public long MaxFileBytes { get; init; } = 64 * 1024 * 1024;
}