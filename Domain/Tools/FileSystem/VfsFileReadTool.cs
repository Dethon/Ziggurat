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
        An image (png, jpg, jpeg, gif, webp) returns its path, media type and size, and the picture itself arrives inside the tool result, straight after this envelope. offset and limit do not apply to an image and are ignored.
        """;

    private const string IgnoredWindowNote =
        "offset and limit do not apply to an image and were ignored.";

    private const string NoCapabilityNote =
        "The model running this turn does not accept images, so this one was not shown. "
        + "Say so rather than describing what the file might contain.";

    private const string NoStoreNote =
        "This host cannot show images read from a file, so this one was not shown. "
        + "Say so rather than describing what the file might contain.";

    // A fourth reason, and it gets its own words: wearing the host-limitation note told the model
    // something false about the deployment. It means the tool ran outside a tool call — a harness or
    // a direct invocation — so there is no call id to key the bytes under.
    private const string NoCallIdNote =
        "This read did not run as a tool call, so there was nowhere to key the image and it was not "
        + "shown. Say so rather than describing what the file might contain.";

    // A fifth reason: the bytes are keyed by the conversation id the turn's own context carries,
    // because that is the id the send looks them up under. A run with no context (a harness, a
    // benchmark) would store bytes no send can ever fetch — and the model, told the image was
    // shown, would wait for a picture that cannot arrive.
    private const string NoConversationNote =
        "This run carries no conversation context, so there was nowhere to key the image and it "
        + "was not shown. Say so rather than describing what the file might contain.";

    private const string EmptyFileNote =
        "The file is empty — zero bytes — so there is no image to show. "
        + "Say so rather than describing what it might contain.";

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
        // Resolved once, so "this host cannot show images" has a single representation rather than
        // being expressible both as a missing record and as a record with a missing store.
        var support = images ?? ReadImageSupport.None;
        var ceiling = support.MaxBytes;

        JsonNode Envelope(long? sizeBytes, string? refusal) => FsResultContract.ToNode(
            new FsImageReadResult
            {
                FilePath = filePath,
                MediaType = mediaType,
                SizeBytes = sizeBytes,
                Shown = refusal is null,
                Note = Note(refusal, windowIgnored)
            });

        // A refusal must never cost the transfer: a 15 MB ceiling exists so one turn cannot become
        // enormous, and pulling a file off somebody's laptop in full just to name its size in the
        // refusal is the same cost wearing a different envelope. So the mount is statted first, and
        // everything decidable without the bytes is decided before the first one crosses the wire.
        var stated = await TryStatSizeAsync(resolution, ct);

        if (stated == 0)
        {
            // A zero-byte file has no picture in it, and a provider rejects an empty image block.
            return Envelope(0, EmptyFileNote);
        }

        if (stated > ceiling)
        {
            return Envelope(stated, OverCeilingNote(stated.Value, exact: true, ceiling));
        }

        var callId = support.CurrentCallId();
        var conversationId = support.CurrentConversationId();
        var refusal = (support.ModelAcceptsImages(), support.Store, callId, conversationId) switch
        {
            (false, _, _, _) => NoCapabilityNote,
            (_, null, _, _) => NoStoreNote,
            (_, _, null, _) => NoCallIdNote,
            (_, _, _, null) => NoConversationNote,
            _ => null
        };
        if (refusal is not null)
        {
            return Envelope(stated, refusal);
        }

        var read = await ReadCappedAsync(resolution, ceiling, ct);
        if (!read.TryGetValue(out var capped, out var unreadable))
        {
            return unreadable.ToNode();
        }

        if (capped.TotalBytes == 0)
        {
            return Envelope(0, EmptyFileNote);
        }

        if (capped.Bytes is null)
        {
            // Only a mount that could not stat lands here — or one whose file grew between the
            // stat and the read — so the count is a floor, and the note says so.
            return Envelope(capped.TotalBytes, OverCeilingNote(capped.TotalBytes, exact: false, ceiling));
        }

        await support.Store!.PutAsync(
            conversationId!,
            callId!,
            new ReadImage
            {
                VirtualPath = filePath, MediaType = mediaType, Bytes = capped.Bytes
            },
            ct);

        return Envelope(capped.TotalBytes, refusal: null);
    }

    private static string OverCeilingNote(long size, bool exact, long ceiling) =>
        $"The image is {(exact ? "" : "at least ")}{size} bytes, over this host's {ceiling}-byte "
        + "limit for showing an image. Resize it below the limit, or tell the person it is too "
        + "large to look at.";

    // The stat is a courtesy asked before the transfer. Any way it cannot answer — an unsupported
    // operation, a wire refusal, a file it does not see, a size it does not report — falls through
    // to the read, whose own errors are the ones that reach the model.
    private static async Task<long?> TryStatSizeAsync(FileSystemResolution resolution, CancellationToken ct)
    {
        try
        {
            var info = await resolution.Backend.InfoAsync(resolution.RelativePath, ct);
            return info.TryGetValue(out var stat, out _) && stat.Exists ? stat.Size : null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (FileSystemOperationException)
        {
            return null;
        }
    }

    private static string? Note(string? refusal, bool windowIgnored) =>
        (refusal, windowIgnored) switch
        {
            (null, false) => null,
            (null, true) => IgnoredWindowNote,
            (_, false) => refusal,
            _ => $"{refusal} {IgnoredWindowNote}"
        };

    // The pull stops the moment the ceiling is passed — leaving the loop disposes the enumerator,
    // which stops a wire backend requesting further chunks — so an oversized file on a mount that
    // could not be statted costs one ceiling's worth of transfer, never the whole file. Null bytes
    // mean the cap was hit, and TotalBytes is then a floor rather than the size.
    private static async Task<FsResult<CappedBytes>> ReadCappedAsync(
        FileSystemResolution resolution, long ceiling, CancellationToken ct)
    {
        var held = new MemoryStream();
        long total = 0;

        try
        {
            await foreach (var chunk in resolution.Backend
                               .ReadChunksAsync(resolution.RelativePath, ct)
                               .WithCancellation(ct))
            {
                total += chunk.Length;
                if (total > ceiling)
                {
                    return new FsResult<CappedBytes>.Ok(new CappedBytes(null, total));
                }

                held.Write(chunk.Span);
            }
        }
        catch (NotSupportedException ex)
        {
            // The three mounts with no bytes behind them render JSON and markdown. A path spelled
            // with an image extension still reaches them, and the honest answer is that this mount
            // cannot serve bytes — not a picture decoded from a rendered document.
            return FsError.Fail<CappedBytes>(
                ToolError.Codes.UnsupportedOperation,
                $"{resolution.RelativePath} cannot be read as an image: {ex.Message}");
        }
        catch (FileSystemOperationException ex)
        {
            // The wire backend has no NotSupportedException to throw: every refusal a server
            // answers — a missing file, a jailed path, a mount with no bytes behind it — arrives as
            // this exception carrying the backend's own envelope. That envelope reaches the model,
            // as it does for a transfer, rather than escaping as a generic function failure.
            return new FsResult<CappedBytes>.Err(ex.Error);
        }

        return new FsResult<CappedBytes>.Ok(new CappedBytes(held.ToArray(), total));
    }

    private sealed record CappedBytes(byte[]? Bytes, long TotalBytes);
}