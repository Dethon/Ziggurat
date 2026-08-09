using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Tools.FileSystem;
using Microsoft.Extensions.Logging;

namespace Domain.Agents;

// Putting an attachment into the agent's sandbox as a real file, so the model can run something
// against it rather than only look at it. The sandbox is the only filesystem where an attachment
// appears as a file: the upload store is never mounted, because one store serves every
// conversation and a mount is visible to the model (ADR 0021).
//
// A directory per conversation and one per message, keeping the name the person used. The
// per-message directory is what removes collisions entirely, so two `scan.pdf`s in one
// conversation both survive without anything being renamed.
public static class AttachmentLanding
{
    public const string Root = "uploads";

    public static async Task<IReadOnlyList<string>> LandAsync(
        IVirtualFileSystemRegistry? registry,
        IReadOnlyList<AttachmentReference> attachments,
        Func<string, CancellationToken, Task<byte[]?>> fetch,
        string conversationId,
        string messageKey,
        ILogger logger,
        CancellationToken ct)
    {
        var mountPoint = FindSandboxMountPoint(registry);
        if (registry is null || mountPoint is null || attachments.Count == 0)
        {
            return [];
        }

        var landed = new List<string>();
        foreach (var attachment in attachments)
        {
            var path = $"{mountPoint}/{Root}/{ConversationDirectory(conversationId)}/{messageKey}/{attachment.FileName}";
            if (await TryWriteAsync(registry, path, attachment, fetch, logger, ct))
            {
                landed.Add(path);
            }
        }

        return landed;
    }

    // The sandbox is the mount that can run something, which is the whole reason a file belongs
    // there. Asking the capability rather than the name keeps this from depending on one server's
    // spelling.
    private static string? FindSandboxMountPoint(IVirtualFileSystemRegistry? registry) =>
        registry?.GetMounts()
            .FirstOrDefault(m => m.Capabilities.Contains(VfsExecTool.Name))
            ?.MountPoint;

    // A failed write is logged and the turn proceeds as context-only: a broken tool must not cost
    // an answer the model could still give.
    private static async Task<bool> TryWriteAsync(
        IVirtualFileSystemRegistry registry,
        string path,
        AttachmentReference attachment,
        Func<string, CancellationToken, Task<byte[]?>> fetch,
        ILogger logger,
        CancellationToken ct)
    {
        try
        {
            if (registry.Resolve(path) is not FsResult<FileSystemResolution>.Ok resolved)
            {
                return false;
            }

            var bytes = await fetch(attachment.Id, ct);
            if (bytes is null)
            {
                return false;
            }

            await resolved.Value.Backend.WriteChunksAsync(
                resolved.Value.RelativePath, One(bytes), overwrite: true, createDirectories: true, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Writing attachment {FileName} into the sandbox at {Path} failed; the turn continues as context only",
                attachment.FileName, path);
            return false;
        }
    }

    public static string Describe(IReadOnlyList<string> paths) =>
        $"[The attached files are in the sandbox at: {string.Join(", ", paths)}]";

    private static string ConversationDirectory(string conversationId) => conversationId.Replace(':', '-');

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> One(ReadOnlyMemory<byte> bytes)
    {
        await Task.CompletedTask;
        yield return bytes;
    }
}