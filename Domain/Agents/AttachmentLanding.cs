using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.WebChat;
using Domain.Tools.FileSystem;
using Microsoft.Extensions.Logging;

namespace Domain.Agents;

// Putting an attachment into the agent's sandbox as a real file, so the model can run something
// against it rather than only look at it. The sandbox is the only filesystem where an attachment
// appears as a file: the upload store is never mounted, because one store serves every
// conversation and a mount is visible to the model (ADR 0021).
//
// The mount says where it can be written and this puts the file there (ADR 0025): under the
// workspace, a directory per conversation and one per message, keeping the name the person used. The
// per-message directory is what separates one message's files from another's, so two `scan.pdf`s
// sent in one conversation both survive; two in the *same* message are separated one level
// further down. Nothing is ever renamed, which is the property the layout exists to keep.
public static class AttachmentLanding
{
    public const string Root = "uploads";

    // One message's files on their way to the sandbox: where they can land, what they are, how
    // their bytes are fetched, and the conversation and message that name their directory.
    public sealed record Landing(
        IVirtualFileSystemRegistry? Registry,
        IReadOnlyList<AttachmentReference> Attachments,
        Func<string, CancellationToken, Task<byte[]?>> Fetch,
        string ConversationId,
        string MessageKey);

    // What one message's landing produced: the paths the model can act on, and the names of the
    // files it cannot, because a partly landed message is where a bare count would make the model
    // guess which file it lost.
    public sealed record LandingOutcome(IReadOnlyList<string> Landed, IReadOnlyList<string> Failed)
    {
        public static readonly LandingOutcome Nothing = new([], []);
    }

    public static async Task<LandingOutcome> LandAsync(
        Landing landing, ILogger logger, CancellationToken ct)
    {
        var (registry, attachments, fetch, conversationId, messageKey) = landing;
        var sandbox = FindSandbox(registry);
        if (registry is null || sandbox is null || attachments.Count == 0)
        {
            return LandingOutcome.Nothing;
        }

        // A mount that can run something but declares nowhere to write it lands nothing. Falling
        // back to the mount root is what shipped: the sandbox mount is the container root, the
        // image never creates a directory there and the container runs unprivileged, so every
        // write failed and every turn continued as if no file had been sent (ADR 0025).
        if (sandbox.Workspace is null)
        {
            logger.LogWarning(
                "The {Mount} mount can run commands but declares no workspace, so {Count} attachment(s) "
                + "were not landed; the model is told which files it cannot act on",
                sandbox.Name, attachments.Count);
            return new LandingOutcome([], [.. attachments.Select(a => a.FileName)]);
        }

        var directory = FileSystemResolution.ToVirtualPath(
            sandbox.MountPoint,
            $"{sandbox.Workspace}/{Root}/{AttachmentEndpointPaths.ConversationDirectory(conversationId)}/{messageKey}");
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var landed = new List<string>();
        var failed = new List<string>();
        foreach (var attachment in attachments)
        {
            // The per-message directory separates one message's files from another's, which is
            // every collision between messages. Within one message two files can still share a
            // name — a person picking `scan.pdf` from two folders — and there the second gets a
            // directory of its own rather than overwriting the first or being renamed. Nothing is
            // renamed either way, which is the property the layout exists to keep.
            var path = taken.Add(attachment.FileName)
                ? $"{directory}/{attachment.FileName}"
                : $"{directory}/{Disambiguate(attachment)}/{attachment.FileName}";

            if (await TryWriteAsync(registry, path, attachment, fetch, logger, ct))
            {
                landed.Add(path);
            }
            else
            {
                failed.Add(attachment.FileName);
            }
        }

        return new LandingOutcome(landed, failed);
    }

    // The attachment's own id, which is what already tells two same-named files apart in the
    // upload store. Its last segment alone: the first is the conversation, already in the path.
    private static string Disambiguate(AttachmentReference attachment) =>
        attachment.Id.Split('/').Last();

    // The sandbox is the mount that can run something, which is the whole reason a file belongs
    // there. Asking the capability rather than the name keeps this from depending on one server's
    // spelling — and is why the mount has to say where it can be written, rather than this knowing.
    private static FileSystemMount? FindSandbox(IVirtualFileSystemRegistry? registry) =>
        registry?.GetMounts().FirstOrDefault(m => m.Capabilities.Contains(VfsExecTool.Name));

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

    // Named rather than counted: a partly landed message is where a count would make the model
    // guess which file it lost.
    public static string DescribeFailures(IReadOnlyList<string> fileNames) =>
        $"[These attached files could not be put in the sandbox: {string.Join(", ", fileNames)}. "
        + "They are not on any filesystem you can reach, so do not run commands against them, and "
        + "say so if the answer needed them.]";

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> One(ReadOnlyMemory<byte> bytes)
    {
        await Task.CompletedTask;
        yield return bytes;
    }
}