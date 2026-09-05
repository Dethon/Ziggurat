using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Infrastructure.Agents.Mcp;

// The mount as the agent holds it: every operation is a call to the server on the other side of the
// seam. `advertisedOperations` is what that server's tool list said it can do, and it has no default
// on purpose: a caller that forgot to pass it would silently turn off the rules of every mount it
// built, which is the shape of unreachable guard ADR-0015 exists to remove. Null says "nothing
// listed" out loud.
internal class McpFileSystemBackend(
    McpClient client,
    string filesystemName,
    IReadOnlySet<string>? advertisedOperations,
    ILogger? logger = null) : IFileSystemBackend
{
    public string FilesystemName => filesystemName;

    public Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct) =>
        CallTypedAsync<FsReadResult>("fs_read", WithFilesystem(new Dictionary<string, object?>
        {
            ["path"] = path,
            ["offset"] = offset,
            ["limit"] = limit
        }), ct);

    public Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct) =>
        CallTypedAsync<FsCreateResult>("fs_create", WithFilesystem(new Dictionary<string, object?>
        {
            ["path"] = path,
            ["content"] = content,
            ["overwrite"] = overwrite,
            ["createDirectories"] = createDirectories
        }), ct);

    public Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct) =>
        CallTypedAsync<FsEditResult>("fs_edit", WithFilesystem(new Dictionary<string, object?>
        {
            ["path"] = path,
            ["edits"] = edits.Select(e => new Dictionary<string, object?>
            {
                ["oldString"] = e.OldString,
                ["newString"] = e.NewString,
                ["replaceAll"] = e.ReplaceAll
            }).ToList()
        }), ct);

    public Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct) =>
        CallTypedAsync<FsGlobResult>("fs_glob", WithFilesystem(new Dictionary<string, object?>
        {
            ["basePath"] = basePath,
            ["pattern"] = pattern
        }), ct);

    public Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path, string? directoryPath,
        string? filePattern, int maxResults, int contextLines, VfsTextSearchOutputMode outputMode, CancellationToken ct) =>
        CallTypedAsync<FsSearchResult>("fs_search", WithFilesystem(new Dictionary<string, object?>
        {
            ["query"] = query,
            ["regex"] = regex,
            ["path"] = path,
            ["directoryPath"] = directoryPath,
            ["filePattern"] = filePattern,
            ["maxResults"] = maxResults,
            ["contextLines"] = contextLines,
            ["outputMode"] = outputMode.ToString()
        }), ct);

    public Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct) =>
        CallTypedAsync<FsMoveResult>("fs_move", WithFilesystem(new Dictionary<string, object?>
        {
            ["sourcePath"] = sourcePath,
            ["destinationPath"] = destinationPath
        }), ct);

    public Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct) =>
        CallTypedAsync<FsRemoveResult>("fs_delete", WithFilesystem(new Dictionary<string, object?>
        {
            ["path"] = path
        }), ct);

    public Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct) =>
        CallTypedAsync<FsInfoResult>("fs_info", WithFilesystem(new Dictionary<string, object?>
        {
            ["path"] = path
        }), ct);

    public Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct) =>
        CallTypedAsync<FsExecResult>("fs_exec", WithFilesystem(new Dictionary<string, object?>
        {
            ["path"] = path,
            ["command"] = command,
            ["timeoutSeconds"] = timeoutSeconds
        }), ct);

    public Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct) =>
        CallTypedAsync<FsCopyResult>("fs_copy", WithFilesystem(new Dictionary<string, object?>
        {
            ["sourcePath"] = sourcePath,
            ["destinationPath"] = destinationPath,
            ["overwrite"] = overwrite,
            ["createDirectories"] = createDirectories
        }), ct);

    // The one operation the proxy answers for itself. Its default is inverted like the backend
    // base's: a server advertises exactly what its backend overrides, so a mount that never
    // registered the check has no rule to state, and silence is "allowed" rather than "refused".
    // Failing closed instead would block every cross-mount move out of the vault, the sandbox and
    // the timers, none of which will ever have a rule.
    public Task<FsResult<FsMoveOutCheckResult>> MoveOutCheckAsync(string path, CancellationToken ct) =>
        advertisedOperations?.Contains(FileSystemOperations.MoveOutCheck) == true
            ? CallTypedAsync<FsMoveOutCheckResult>(FileSystemOperations.MoveOutCheck,
                WithFilesystem(new Dictionary<string, object?> { ["path"] = path }), ct)
            : Task.FromResult(FsMoveOutCheckResult.Allow(path));

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        const int chunkSize = 256 * 1024;
        long offset = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var node = await CallToolAsync("fs_blob_read", WithFilesystem(new Dictionary<string, object?>
            {
                ["path"] = path,
                ["offset"] = offset,
                ["length"] = chunkSize
            }), ct);

            var blobError = ToolErrorResult.FromEnvelope(node);
            if (blobError is not null)
            {
                throw Refused("fs_blob_read", blobError);
            }

            var bytes = Convert.FromBase64String(node["contentBase64"]!.GetValue<string>());
            if (bytes.Length > 0)
            {
                offset += bytes.Length;
                yield return bytes;
            }

            if (node["eof"]!.GetValue<bool>())
            {
                yield break;
            }

            if (bytes.Length == 0)
            {
                // Defensive: server reported !eof but sent nothing — break to avoid infinite loop.
                yield break;
            }
        }
    }

    public async Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct)
    {
        long offset = 0;

        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            var node = await CallToolAsync("fs_blob_write", WithFilesystem(new Dictionary<string, object?>
            {
                ["path"] = path,
                ["contentBase64"] = Convert.ToBase64String(chunk.Span),
                ["offset"] = offset,
                ["overwrite"] = overwrite,
                ["createDirectories"] = createDirectories
            }), ct);

            var blobError = ToolErrorResult.FromEnvelope(node);
            if (blobError is not null)
            {
                throw Refused("fs_blob_write", blobError);
            }

            offset += chunk.Length;
        }

        if (offset == 0)
        {
            // Empty source: still create the file with zero bytes.
            var node = await CallToolAsync("fs_blob_write", WithFilesystem(new Dictionary<string, object?>
            {
                ["path"] = path,
                ["contentBase64"] = "",
                ["offset"] = 0L,
                ["overwrite"] = overwrite,
                ["createDirectories"] = createDirectories
            }), ct);

            var blobError = ToolErrorResult.FromEnvelope(node);
            if (blobError is not null)
            {
                throw Refused("fs_blob_write", blobError);
            }
        }

        return offset;
    }

    // The server refused, and the chunk operations are streams with no envelope to say so in. The
    // refusal travels as the typed exception so the far end's code and retryability reach
    // VfsCopyTool intact — flattening it left a permanent denial looking retryable.
    private static FileSystemOperationException Refused(string toolName, ToolErrorResult error) =>
        new(error with { Message = $"{toolName} failed: {error.Message}" });

    private Dictionary<string, object?> WithFilesystem(Dictionary<string, object?> args)
    {
        args["filesystem"] = filesystemName;
        return args;
    }

    private async Task<FsResult<T>> CallTypedAsync<T>(
        string toolName, Dictionary<string, object?> args, CancellationToken ct) where T : class
    {
        var node = await CallToolAsync(toolName, args, ct);

        var error = ToolErrorResult.FromEnvelope(node);
        if (error is not null)
        {
            return new FsResult<T>.Err(error);
        }

        var value = node.Deserialize<T>(FsResultContract.ValidationOptions)
            ?? throw new InvalidOperationException($"Failed to deserialize '{toolName}' payload.");
        return new FsResult<T>.Ok(value);
    }

    // The turn's conversation context rides this hop as `_meta`, exactly as QualifiedMcpTool stamps
    // a tool the model calls directly: a mount that answers by who is calling — the Home Assistant
    // watches record their creating agent — sees the same context either way. Without it the first
    // watch written in prod was refused for carrying no caller.
    protected internal virtual async Task<JsonNode> CallToolAsync(string toolName, Dictionary<string, object?> args, CancellationToken ct)
    {
        CallToolResult result;
        try
        {
            result = await client.CallToolAsync(new CallToolRequestParams
            {
                Name = toolName,
                Arguments = args.ToDictionary(a => a.Key, a => JsonSerializer.SerializeToElement(a.Value)),
                Meta = ConversationContextMeta.TryBuild(FunctionInvokingChatClient.CurrentContext?.Options)
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Client-side dispatch failure: the call never reached the server's
            // AddCallToolFilter (e.g. tool not registered, schema mismatch, transport error).
            // Server-side rejections arrive as result.IsError=true with an envelope payload
            // and are handled below.
            return ToolError.Create(
                ToolError.Codes.UnsupportedOperation,
                $"The '{filesystemName}' filesystem does not support the '{toolName}' operation. " +
                $"This tool is not available on this filesystem backend. Details: {ex.Message}",
                hint: "Pick a different mount or a different operation.");
        }

        return InterpretResult(result, toolName);
    }

    // The shipped servers' call-tool filter always answers JSON, but an SDK-generated rejection or
    // a third-party server can answer plain text or nothing at all — this layer's promise is the
    // envelope at every exit, so a text that does not parse is handled, never thrown.
    internal JsonNode InterpretResult(CallToolResult result, string toolName)
    {
        var text = string.Join("\n", result.Content
            .OfType<TextContentBlock>()
            .Select(c => c.Text));

        var parsed = TryParse(text);

        if (result.IsError == true)
        {
            return ToolErrorResult.IsErrorEnvelope(parsed)
                ? parsed!
                : ToolError.Create(
                    ToolError.Codes.InternalError,
                    $"Error calling '{toolName}' on the '{filesystemName}' filesystem: {text}");
        }

        if (parsed is null)
        {
            return MalformedPayload(toolName, "the response is not JSON");
        }

        return FsResultContract.TryValidate(toolName, parsed, out var validationError)
            ? parsed
            : MalformedPayload(toolName, validationError);
    }

    private JsonNode MalformedPayload(string toolName, string? detail)
    {
        logger?.LogWarning(
            "Filesystem '{Filesystem}' returned a malformed '{Tool}' payload: {Error}",
            filesystemName, toolName, detail);

        return ToolError.Create(
            ToolError.Codes.InternalError,
            $"The '{filesystemName}' filesystem returned a malformed '{toolName}' payload " +
            "that does not match the expected schema.",
            hint: "This is a backend bug; the payload was rejected to protect the conversation.");
    }

    private static JsonNode? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}