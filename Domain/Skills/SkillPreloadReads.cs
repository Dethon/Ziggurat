using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.FileSystem;

namespace Domain.Skills;

// The reader a preload makes its declared reads with: the turn's own file_read over the
// session's mounts, so what lands in the conversation is exactly what the tool would have
// answered the model. A session with no filesystem yields no reader, and the preload is the
// skills alone.
public static class SkillPreloadReads
{
    public static PreloadFileReader? ReaderOver(IVirtualFileSystemRegistry? registry) =>
        registry is null
            ? null
            : async (path, ct) => await new VfsFileReadTool(registry).RunAsync(path, cancellationToken: ct);
}