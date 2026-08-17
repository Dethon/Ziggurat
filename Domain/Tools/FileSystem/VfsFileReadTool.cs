using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.FileSystem;

namespace Domain.Tools.FileSystem;

public class VfsFileReadTool(IVirtualFileSystemRegistry registry, ReadImageSupport? images = null)
{
    public const string Key = "read";
    public const string Name = "file_read";

    public const string ToolDescription = """
        Reads a file — text or an image.
        A text file returns its content with line numbers, formatted as "1: first line\n2: second line\n..." with trailing metadata. Large files are truncated — use offset and limit for pagination.
        An image (png, jpg, jpeg, gif, webp) returns its path, media type and size, and the picture itself is shown to you in the message straight after the tool results. offset and limit do not apply to an image and are ignored.
        """;

    private const string IgnoredWindowNote =
        "offset and limit do not apply to an image and were ignored.";

    private const string NoCapabilityNote =
        "The model running this turn does not accept images, so this one was not shown. "
        + "Say so rather than describing what the file might contain.";

    private const string NoStoreNote =
        "This host cannot show images read from a file, so this one was not shown. "
        + "Say so rather than describing what the file might contain.";

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path to file (e.g., /library/notes/todo.md)")]
        string filePath,
        [Description("Start from this line number (1-based, default: 1). Ignored for an image.")]
        int? offset = null,
        [Description("Max lines to return. Ignored for an image.")]
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        if (!registry.Resolve(filePath).TryGetValue(out var resolution, out var unresolved))
        {
            return unresolved.ToNode();
        }

        if (ImageFileKinds.MediaTypeFor(filePath) is { } mediaType)
        {
            return await ReadImageAsync(
                resolution, filePath, mediaType,
                windowIgnored: offset is not null || limit is not null,
                cancellationToken);
        }

        var result = await resolution.Backend.ReadAsync(resolution.RelativePath, offset, limit, cancellationToken);
        // The backend reports the file path in its own coordinates — a disk root even reports the
        // container-absolute one, which the registry would refuse if reused. Echoing the virtual path
        // the caller passed keeps the result reusable as input to every other filesystem tool.
        return result.Map(read => read with { FilePath = filePath }).ToNode();
    }

    private async Task<JsonNode> ReadImageAsync(
        FileSystemResolution resolution, string filePath, string mediaType, bool windowIgnored, CancellationToken ct)
    {
        var ceiling = images?.MaxBytes ?? ReadImageSupport.DefaultMaxBytes;

        // Read before deciding, so the envelope names the real size whatever the verdict is: a model
        // told an image is too large has to know by how much, and one told it cannot be shown at all
        // still learns what the file is.
        var read = await ReadBoundedAsync(resolution, ceiling, ct);
        if (!read.TryGetValue(out var bounded, out var unreadable))
        {
            return unreadable.ToNode();
        }

        var callId = images?.CallId();
        var refusal = (bounded.Bytes, images?.Store, images?.ModelAcceptsImages() ?? true, callId) switch
        {
            (null, _, _, _) =>
                $"The image is {bounded.TotalBytes} bytes, over this host's {ceiling}-byte limit for "
                + "showing an image. Resize it below the limit, or tell the person it is too large to look at.",
            (_, _, false, _) => NoCapabilityNote,
            (_, null, _, _) => NoStoreNote,
            (_, _, _, null) => NoStoreNote,
            _ => null
        };

        if (refusal is null)
        {
            await images!.Store!.PutAsync(
                images.ConversationId,
                callId!,
                new ReadImage
                {
                    VirtualPath = filePath, MediaType = mediaType, Bytes = bounded.Bytes!
                },
                ct);
        }

        return FsResultContract.ToNode(new FsImageReadResult
        {
            FilePath = filePath,
            MediaType = mediaType,
            SizeBytes = bounded.TotalBytes,
            Shown = refusal is null,
            Note = Note(refusal, windowIgnored)
        });
    }

    private static string? Note(string? refusal, bool windowIgnored) =>
        (refusal, windowIgnored) switch
        {
            (null, false) => null,
            (null, true) => IgnoredWindowNote,
            (_, false) => refusal,
            _ => $"{refusal} {IgnoredWindowNote}"
        };

    // Counted in full and held only up to the ceiling. The envelope has to name the real size, and
    // buffering an enormous file in order to measure it is the thing the ceiling exists to prevent —
    // so past the ceiling the bytes are dropped and only the count keeps going.
    private static async Task<FsResult<BoundedBytes>> ReadBoundedAsync(
        FileSystemResolution resolution, long ceiling, CancellationToken ct)
    {
        var held = new MemoryStream();
        long total = 0;
        var over = false;

        try
        {
            await foreach (var chunk in resolution.Backend
                               .ReadChunksAsync(resolution.RelativePath, ct)
                               .WithCancellation(ct))
            {
                total += chunk.Length;
                if (over)
                {
                    continue;
                }

                if (total > ceiling)
                {
                    over = true;
                    held.Dispose();
                    held = new MemoryStream();
                    continue;
                }

                held.Write(chunk.Span);
            }
        }
        catch (NotSupportedException ex)
        {
            // The three mounts with no bytes behind them render JSON and markdown. A path spelled
            // with an image extension still reaches them, and the honest answer is that this mount
            // cannot serve bytes — not a picture decoded from a rendered document.
            return FsError.Fail<BoundedBytes>(
                ToolError.Codes.UnsupportedOperation,
                $"{resolution.RelativePath} cannot be read as an image: {ex.Message}");
        }

        return new FsResult<BoundedBytes>.Ok(
            new BoundedBytes(over ? null : held.ToArray(), total));
    }

    private sealed record BoundedBytes(byte[]? Bytes, long TotalBytes);
}