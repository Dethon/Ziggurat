using System.ComponentModel;
using System.Reflection;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Infrastructure.Utils;

// Registers a server's fs_* tools from its backend. A tool exists for exactly the operations the
// backend overrides, and its description comes from the hook next to that implementation — so a
// server cannot advertise an operation its backend does not implement, and there is no second
// place for the capability list to drift from.
//
// Capability is per operation, not per path: a backend may override an operation and still refuse
// particular paths. That coarseness is intended — the list tells the model which operations exist
// on a mount, not which will succeed on a given file.
public static class FileSystemServerTools
{
    private sealed record Wiring(
        Func<FileSystemBackendBase, string> Describe,
        Func<FileSystemBackendBase, Delegate> Handler);

    // Every operation in the one list must say how it reaches the wire, or registration would
    // throw at startup instead of failing a test. Asserted by FileSystemOperationsTests.
    public static bool HasWiring(string toolName) => _wiring.ContainsKey(toolName);

    // How each operation reaches the wire. The operations themselves — their names, their backend
    // methods — come from the one list; this only adds the tool signature and the description hook.
    private static readonly IReadOnlyDictionary<string, Wiring> _wiring = new Dictionary<string, Wiring>(StringComparer.Ordinal)
    {
        ["fs_read"] = new(b => b.DescribeRead, b =>
            async (string path, int? offset = null, int? limit = null, CancellationToken ct = default) =>
                ToolResponse.Create(await b.ReadAsync(path, offset, limit, ct))),

        ["fs_info"] = new(b => b.DescribeInfo, b =>
            async (string path, CancellationToken ct = default) =>
                ToolResponse.Create(await b.InfoAsync(path, ct))),

        ["fs_glob"] = new(b => b.DescribeGlob, b =>
            async (string pattern, string basePath = "", CancellationToken ct = default) =>
                ToolResponse.Create(await b.GlobAsync(basePath, pattern, ct))),

        ["fs_search"] = new(b => b.DescribeSearch, b =>
            async (string query, bool regex = false, string? path = null, string? directoryPath = null,
                    string? filePattern = null, int maxResults = 50, int contextLines = 1,
                    string outputMode = "content", CancellationToken ct = default) =>
                ToolResponse.Create(await SearchAsync(
                    b, query, regex, path, directoryPath, filePattern, maxResults, contextLines,
                    outputMode, ct))),

        ["fs_create"] = new(b => b.DescribeCreate, b =>
            async (string path, string content, bool overwrite = false, bool createDirectories = true, CancellationToken ct = default) =>
                ToolResponse.Create(await b.CreateAsync(path, content, overwrite, createDirectories, ct))),

        ["fs_edit"] = new(b => b.DescribeEdit, b =>
            async (string path, IReadOnlyList<TextEdit> edits, CancellationToken ct = default) =>
                ToolResponse.Create(await b.EditAsync(path, edits, ct))),

        ["fs_move"] = new(b => b.DescribeMove, b =>
            async (string sourcePath, string destinationPath, CancellationToken ct = default) =>
                ToolResponse.Create(await b.MoveAsync(sourcePath, destinationPath, ct))),

        ["fs_delete"] = new(b => b.DescribeDelete, b =>
            async (string path, CancellationToken ct = default) =>
                ToolResponse.Create(await b.DeleteAsync(path, ct))),

        ["fs_exec"] = new(b => b.DescribeExec, b =>
            async (string path, string command, int? timeoutSeconds = null, CancellationToken ct = default) =>
                ToolResponse.Create(await b.ExecAsync(path, command, timeoutSeconds, ct))),

        ["fs_copy"] = new(b => b.DescribeCopy, b =>
            async (string sourcePath, string destinationPath, bool overwrite = false, bool createDirectories = true, CancellationToken ct = default) =>
                ToolResponse.Create(await b.CopyAsync(sourcePath, destinationPath, overwrite, createDirectories, ct))),

        ["fs_blob_read"] = new(b => b.DescribeBlobRead, b =>
            async (string path, long offset = 0, int length = 262144, CancellationToken ct = default) =>
                ToolResponse.Create(await b.ReadBlobAsync(path, offset, length, ct))),

        ["fs_blob_write"] = new(b => b.DescribeBlobWrite, b =>
            async (string path, string contentBase64, long offset = 0, bool overwrite = false, bool createDirectories = true, CancellationToken ct = default) =>
                ToolResponse.Create(await b.WriteBlobAsync(path, contentBase64, offset, overwrite, createDirectories, ct))),

        [FileSystemOperations.MoveOutCheck] = new(b => b.DescribeMoveOutCheck, b =>
            async (string path, CancellationToken ct = default) =>
                ToolResponse.Create(await b.MoveOutCheckAsync(path, ct)))
    };

    public static IMcpServerBuilder AddFileSystemTools<TBackend>(this IMcpServerBuilder builder)
        where TBackend : FileSystemBackendBase
    {
        foreach (var toolName in SupportedToolNames(typeof(TBackend)))
        {
            var wiring = _wiring[toolName];
            builder.Services.AddSingleton<McpServerTool>(sp =>
            {
                var backend = sp.GetRequiredService<TBackend>();
                return McpServerTool.Create(wiring.Handler(backend), new McpServerToolCreateOptions
                {
                    Name = toolName,
                    Description = wiring.Describe(backend)
                });
            });
        }

        return builder;
    }

    // The operations a backend really implements, asked of the one list so the registrar and the
    // refusal an unsupported call answers with cannot disagree about what a mount can do.
    public static IReadOnlyList<string> SupportedToolNames(Type backendType) =>
        [.. FileSystemOperations.SupportedBy(backendType).Select(o => o.ToolName)];

    // The wire hands outputMode over as a string; a value that names neither mode is the caller's
    // error and answers the invalid-argument envelope instead of silently becoming Content.
    internal static async Task<FsResult<FsSearchResult>> SearchAsync(FileSystemBackendBase backend,
        string query, bool regex, string? path, string? directoryPath, string? filePattern,
        int maxResults, int contextLines, string outputMode, CancellationToken ct) =>
        TryParseOutputMode(outputMode, out var mode)
            ? await backend.SearchAsync(
                query, regex, path, directoryPath, filePattern, maxResults, contextLines, mode, ct)
            : FsError.Invalid<FsSearchResult>($"outputMode must be 'content' or 'filesOnly'; got '{outputMode}'.");

    private static bool TryParseOutputMode(string outputMode, out VfsTextSearchOutputMode mode)
    {
        mode = outputMode.Equals("filesOnly", StringComparison.OrdinalIgnoreCase)
            ? VfsTextSearchOutputMode.FilesOnly
            : VfsTextSearchOutputMode.Content;
        return mode is VfsTextSearchOutputMode.FilesOnly
            || outputMode.Equals("content", StringComparison.OrdinalIgnoreCase);
    }
}