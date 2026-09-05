using System.Text;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Printing;
using Domain.Prompts;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Domain.Tools.Printing;

namespace Domain.Tools.Printing.Vfs;

public sealed class PrinterQueueFileSystem(
    IPrintSpool spool,
    IPrinterClient printer,
    PrintQueueGate gate,
    string supportedFormats,
    TimeSpan? regexMatchTimeout = null) : FileSystemBackendBase
{
    public const string Name = "print-queue";

    public override string FilesystemName => Name;

    protected override TimeSpan SearchMatchTimeout => regexMatchTimeout ?? base.SearchMatchTimeout;

    public override string DescribeMount =>
        "A printer exposed as a flat filesystem. Copy or create a document at /print-queue/<filename> "
        + "to print it on the configured printer; the document is submitted automatically. Accepted "
        + $"formats: {PrintingPrompt.DescribeFormats(supportedFormats)} - any other format is rejected "
        + "on copy-in, so first convert whatever you want to print into an accepted format (typically "
        + "text or JPEG, e.g. render a PDF or PNG to a JPEG) and copy that in. Remove a file with "
        + "fs_delete to cancel it if it has not finished printing yet. Read /print-queue/status.json "
        + "for the state of every queued job (queued/pending/processing). Finished jobs disappear "
        + "from the listing automatically. Supported: read, create, edit (text only), copy, glob, "
        + "search, delete, and binary copy-in. Not supported: move and exec.";

    // The words the model reads about each operation, next to the behaviour they describe.
    public override string DescribeRead => "Read a queued document's text, or read status.json for the queue state.";

    public override string DescribeInfo => "Get metadata for a queued document or the queue root.";

    public override string DescribeGlob => "List queued documents (plus status.json) matching a glob pattern.";

    public override string DescribeSearch => "Search the text content of queued documents.";

    public override string DescribeCreate => "Queue a new text document for printing at /print-queue/<filename>.";

    public override string DescribeEdit =>
        "Edit a queued text document; cancels the old job and re-queues the new version.";

    public override string DescribeDelete => "Remove a queued document. Cancels it if it has not finished printing.";

    public override string DescribeCopy =>
        "Duplicate a queued document under a new name (queues another print job).";

    public override string DescribeBlobRead =>
        "Read a chunk of a queued document's raw bytes as base64. Returns { contentBase64, eof, totalBytes }.";

    public override string DescribeBlobWrite =>
        "Write a chunk of raw bytes (base64) to a queued document. offset=0 starts it; the document "
        + "prints once writes go quiet.";

    // Only /print-queue/<filename> holds raw bytes, and the spool reads and writes ranges directly,
    // so both blob operations answer from it rather than draining the chunk stream.
    public override async Task<FsResult<FsBlobReadResult>> ReadBlobAsync(
        string path, long offset, int length, CancellationToken ct)
    {
        var node = PrinterQueuePath.Parse(path);
        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            return FsError.NotFound<FsBlobReadResult>(path);
        }

        var (bytes, eof, total) = await spool.ReadBytesAsync(node.FileName!, offset, length, ct);
        return new FsResult<FsBlobReadResult>.Ok(new FsBlobReadResult
        {
            ContentBase64 = Convert.ToBase64String(bytes),
            Eof = eof,
            TotalBytes = total
        });
    }

    public override async Task<FsResult<FsBlobWriteResult>> WriteBlobAsync(
        string path, string contentBase64, long offset, bool overwrite, bool createDirectories, CancellationToken ct)
    {
        var node = PrinterQueuePath.Parse(path);
        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            return Invalid<FsBlobWriteResult>($"Cannot write to '{path}'. Write documents to /print-queue/<filename>.");
        }

        var fileName = node.FileName!;
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(contentBase64);
        }
        catch (FormatException ex)
        {
            return Invalid<FsBlobWriteResult>($"contentBase64 is not valid base64: {ex.Message}");
        }

        // The first chunk carries the file header; reject formats the printer cannot render before
        // anything is spooled, so unsupported files fail the copy instead of printing as gibberish.
        if (offset == 0)
        {
            var format = PrintableContent.DetectFormat(bytes);
            if (!PrintableContent.IsSupported(format, supportedFormats))
            {
                return Fail<FsBlobWriteResult>(ToolError.Codes.UnsupportedOperation,
                    $"'{fileName}' looks like '{format}', which this printer cannot render. Supported formats: {supportedFormats}.",
                    hint: "Convert it to a supported format first (e.g. export an image as JPEG).");
            }
        }

        await spool.WriteBytesAsync(fileName, "application/octet-stream", bytes, offset, overwrite, ct);
        var entry = await spool.GetAsync(fileName, ct);
        return new FsResult<FsBlobWriteResult>.Ok(new FsBlobWriteResult
        {
            Path = path,
            BytesWritten = bytes.Length,
            TotalBytes = entry?.SizeBytes ?? bytes.Length
        });
    }

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public override async Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        var node = PrinterQueuePath.Parse(path);

        if (node.Kind == PrinterNodeKind.StatusFile)
        {
            var status = await RenderStatusAsync(ct);
            return Ok(path, status);
        }

        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            return NotFound<FsReadResult>(path);
        }

        var bytes = await spool.ReadAllBytesAsync(node.FileName!, ct);
        if (bytes is null)
        {
            return NotFound<FsReadResult>(path);
        }

        if (!IsText(bytes))
        {
            return new FsResult<FsReadResult>.Err(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.UnsupportedOperation,
                Message = $"'{node.FileName}' is a binary document and cannot be read as text.",
                Hint = "Read /print-queue/status.json for its print state, or use fs_blob_read for raw bytes."
            });
        }

        return Ok(path, Encoding.UTF8.GetString(bytes));
    }

    public override async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        var node = PrinterQueuePath.Parse(path);

        if (node.Kind == PrinterNodeKind.Root)
        {
            return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = path, IsDirectory = true });
        }

        if (node.Kind == PrinterNodeKind.StatusFile)
        {
            return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = path, IsDirectory = false });
        }

        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = false, Path = path });
        }

        var entry = await spool.GetAsync(node.FileName!, ct);
        if (entry is null)
        {
            return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = false, Path = path });
        }

        return new FsResult<FsInfoResult>.Ok(new FsInfoResult
        {
            Exists = true,
            Path = path,
            IsDirectory = false,
            Size = entry.SizeBytes,
            LastModified = entry.LastWriteAt.UtcDateTime.ToString("O")
        });
    }

    public override async Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct)
    {
        using var _ = await gate.AcquireAsync(ct);
        var node = PrinterQueuePath.Parse(path);
        if (node.Kind == PrinterNodeKind.StatusFile)
        {
            return ReadOnly<FsCreateResult>(path);
        }

        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            return Invalid<FsCreateResult>($"Create a document at /print-queue/<filename> (got '{path}')");
        }

        if (!overwrite && await spool.GetAsync(node.FileName!, ct) is not null)
        {
            return new FsResult<FsCreateResult>.Err(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.AlreadyExists,
                Message = $"A job named '{node.FileName}' is already queued. Pass overwrite=true to replace it."
            });
        }

        await CancelIfSubmittedAsync(node.FileName!, ct);
        var bytes = Encoding.UTF8.GetBytes(content);
        await spool.WriteBytesAsync(node.FileName!, "text/plain", bytes, 0, true, ct);

        return new FsResult<FsCreateResult>.Ok(new FsCreateResult
        {
            Status = "queued",
            FilePath = path,
            Size = bytes.Length.ToString(),
            Lines = content.Split('\n').Length
        });
    }

    public override async Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct)
    {
        using var _ = await gate.AcquireAsync(ct);
        var node = PrinterQueuePath.Parse(path);
        if (node.Kind == PrinterNodeKind.StatusFile)
        {
            return ReadOnly<FsEditResult>(path);
        }

        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            return NotFound<FsEditResult>(path);
        }

        var bytes = await spool.ReadAllBytesAsync(node.FileName!, ct);
        if (bytes is null)
        {
            return NotFound<FsEditResult>(path);
        }

        if (!IsText(bytes))
        {
            return Fail<FsEditResult>(ToolError.Codes.UnsupportedOperation,
                $"'{node.FileName}' is a binary document and cannot be edited as text.");
        }

        var text = Encoding.UTF8.GetString(bytes);
        var total = 0;
        var details = new List<FsEditDetail>();
        foreach (var edit in edits)
        {
            var count = CountOccurrences(text, edit.OldString);
            if (count == 0)
            {
                return Invalid<FsEditResult>($"Text not found: '{edit.OldString}'");
            }

            var replaced = edit.ReplaceAll ? count : 1;
            text = TextEdits.ReplaceFirstOrAll(text, edit.OldString, edit.NewString, edit.ReplaceAll);
            total += replaced;
            details.Add(new FsEditDetail { OccurrencesReplaced = replaced, AffectedLines = new FsLineRange { Start = 0, End = 0 } });
        }

        // Replacing the spooled bytes (offset 0) resets the lifecycle to unsubmitted; cancel any in-flight job first.
        await CancelIfSubmittedAsync(node.FileName!, ct);
        await spool.WriteBytesAsync(node.FileName!, "text/plain", Encoding.UTF8.GetBytes(text), 0, true, ct);

        return new FsResult<FsEditResult>.Ok(new FsEditResult
        {
            Status = "queued",
            FilePath = path,
            TotalOccurrencesReplaced = total,
            Edits = details
        });
    }

    // The print queue is flat: every entry is a document (or status.json) at the root. basePath and
    // the dirs-only convention still apply, and both correctly yield nothing here — there are no
    // directories to list and nothing below the root to scope to.
    public override async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        if (!GlobPrologue(basePath, pattern).TryGetValue(out var scope, out var invalidPattern))
        {
            return new FsResult<FsGlobResult>.Err(invalidPattern);
        }

        var (dirsOnly, matches) = scope;
        if (dirsOnly)
        {
            return Glob([]);
        }

        var entries = await spool.ListAsync(ct);
        var names = entries.Select(e => e.FileName).Append(PrinterQueuePath.StatusFileName);

        return Glob(pattern, () =>
            names.Where(matches).OrderBy(n => n, StringComparer.Ordinal).Select(n => "/" + n).ToList());
    }

    public override async Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path,
        string? directoryPath, string? filePattern, int maxResults, int contextLines,
        VfsTextSearchOutputMode outputMode, CancellationToken ct)
    {
        if (!CompileFilePattern(filePattern).TryGetValue(out var admits, out var patternError))
        {
            return new FsResult<FsSearchResult>.Err(patternError);
        }

        var entries = await spool.ListAsync(ct);
        // Lazy on purpose: the file names are the caller's own, so the match runs inside the scan,
        // where a pattern that backtracks past the timeout becomes the timeout envelope.
        var scoped = entries.Where(e => admits(e.FileName));

        return await SearchNodesAsync(
            scoped,
            async (entry, token) =>
            {
                var bytes = await spool.ReadAllBytesAsync(entry.FileName, token);
                return ("/" + entry.FileName, bytes is null || !IsText(bytes) ? null : Encoding.UTF8.GetString(bytes));
            },
            new FsSearchScan
            {
                Query = query,
                Regex = regex,
                Path = path ?? directoryPath ?? "/",
                MaxResults = maxResults,
                ContextLines = contextLines,
                OutputMode = outputMode
            },
            ct);
    }

    public override async Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct)
    {
        using var _ = await gate.AcquireAsync(ct);
        var node = PrinterQueuePath.Parse(path);
        if (node.Kind == PrinterNodeKind.StatusFile)
        {
            return ReadOnly<FsRemoveResult>(path);
        }

        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            return NotFound<FsRemoveResult>(path);
        }

        var entry = await spool.GetAsync(node.FileName!, ct);
        if (entry is null)
        {
            return NotFound<FsRemoveResult>(path);
        }

        // The crux: cancelling before the printer finishes means it will not print.
        await CancelIfSubmittedAsync(node.FileName!, ct);
        await spool.RemoveAsync(node.FileName!, ct);

        return new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
        {
            Status = "removed",
            Message = entry.IsSubmitted ? "Print job cancelled and removed from the queue." : "Removed from the queue before printing.",
            OriginalPath = path,
            TrashPath = ""
        });
    }

    public override async Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        using var _ = await gate.AcquireAsync(ct);
        var src = PrinterQueuePath.Parse(sourcePath);
        var dst = PrinterQueuePath.Parse(destinationPath);
        if (src.Kind != PrinterNodeKind.DocumentFile || dst.Kind != PrinterNodeKind.DocumentFile)
        {
            return Invalid<FsCopyResult>("Copy within the print queue requires document file paths.");
        }

        var bytes = await spool.ReadAllBytesAsync(src.FileName!, ct);
        if (bytes is null)
        {
            return NotFound<FsCopyResult>(sourcePath);
        }

        if (!overwrite && await spool.GetAsync(dst.FileName!, ct) is not null)
        {
            return new FsResult<FsCopyResult>.Err(new ToolErrorResult
            {
                ErrorCode = ToolError.Codes.AlreadyExists,
                Message = $"A job named '{dst.FileName}' is already queued."
            });
        }

        // No format check needed: the source is already a spooled document, so it passed the
        // format gate on the way in and cannot introduce an unsupported payload.
        var srcEntry = await spool.GetAsync(src.FileName!, ct);
        await CancelIfSubmittedAsync(dst.FileName!, ct);
        await spool.WriteBytesAsync(dst.FileName!, srcEntry!.ContentType, bytes, 0, true, ct);

        return new FsResult<FsCopyResult>.Ok(new FsCopyResult
        {
            Status = "queued",
            Source = sourcePath,
            Destination = destinationPath,
            Bytes = bytes.Length
        });
    }

    public override async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var node = PrinterQueuePath.Parse(path);
        var bytes = node.Kind == PrinterNodeKind.DocumentFile
            ? await spool.ReadAllBytesAsync(node.FileName!, ct)
            : null;

        if (bytes is null)
        {
            yield break;
        }

        const int chunkSize = 256 * 1024;
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            ct.ThrowIfCancellationRequested();
            yield return bytes.AsMemory(offset, Math.Min(chunkSize, bytes.Length - offset));
        }
    }

    public override async Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        using var _ = await gate.AcquireAsync(ct);
        var node = PrinterQueuePath.Parse(path);
        if (node.Kind != PrinterNodeKind.DocumentFile)
        {
            throw new InvalidOperationException($"Cannot write to '{path}' in the print queue.");
        }

        long offset = 0;
        await CancelIfSubmittedAsync(node.FileName!, ct);
        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            // The first chunk carries the file header; reject formats the printer cannot render before
            // anything is spooled. The MCP fs_blob_write tool guards too, but enforcing it here keeps
            // the backend self-consistent for any in-process caller.
            if (offset == 0)
            {
                var format = PrintableContent.DetectFormat(chunk.Span);
                if (!PrintableContent.IsSupported(format, supportedFormats))
                {
                    throw new InvalidOperationException(
                        $"'{node.FileName}' looks like '{format}', which this printer cannot render. Supported formats: {supportedFormats}.");
                }
            }

            await spool.WriteBytesAsync(node.FileName!, "application/octet-stream", chunk, offset, overwrite && offset == 0, ct);
            offset += chunk.Length;
        }

        if (offset == 0)
        {
            await spool.WriteBytesAsync(node.FileName!, "application/octet-stream", ReadOnlyMemory<byte>.Empty, 0, true, ct);
        }

        return offset;
    }

    private async Task CancelIfSubmittedAsync(string fileName, CancellationToken ct)
    {
        var entry = await spool.GetAsync(fileName, ct);
        if (entry is { IsSubmitted: true })
        {
            await printer.CancelAsync(entry.JobId!.Value, ct);
        }
    }

    private async Task<string> RenderStatusAsync(CancellationToken ct)
    {
        var entries = await spool.ListAsync(ct);
        var active = (await printer.GetActiveJobsAsync(ct)).ToDictionary(j => j.JobId);

        var rows = entries.Select(e => new
        {
            filename = e.FileName,
            jobId = e.JobId,
            state = !e.IsSubmitted
                ? PrintJobState.Queued.ToString()
                : active.TryGetValue(e.JobId!.Value, out var job) ? job.State.ToString() : PrintJobState.Processing.ToString(),
            submittedAt = e.SubmittedAt?.UtcDateTime.ToString("O"),
            sizeBytes = e.SizeBytes
        }).OrderBy(r => r.filename);

        return JsonSerializer.Serialize(rows, _json);
    }

    private static bool IsText(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return false;
        }

        try
        {
            _ = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle))
        {
            return 0;
        }

        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static FsResult<FsReadResult> Ok(string path, string content) =>
        new FsResult<FsReadResult>.Ok(new FsReadResult
        {
            FilePath = path,
            Content = content,
            TotalLines = content.Split('\n').Length,
            Truncated = false
        });

}