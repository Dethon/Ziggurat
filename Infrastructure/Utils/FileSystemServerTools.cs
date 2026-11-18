using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools;
using Domain.Tools.FileSystem;
using Mcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Infrastructure.Utils;

// Registers a server's fs_* tools from its backends. A tool exists for exactly the operations its
// backends override, and its description comes from the hook next to that implementation — so a
// server cannot advertise an operation no backend of its implements, and there is no second place
// for the capability list to drift from.
//
// A server may serve several filesystems (Home Assistant its home and its watches): one call per
// backend type, one tool per operation across them all, and each call dispatched by the
// `filesystem` the agent names — the first registered where it names none, which is every agent
// call an outpost or a kit server from before ever saw. What one filesystem can do is in its own
// resource (`operations`), since the tool list is now the union.
//
// Capability is per operation, not per path: a backend may override an operation and still refuse
// particular paths. That coarseness is intended — the list tells the model which operations exist
// on a mount, not which will succeed on a given file.
public static class FileSystemServerTools
{
    private sealed record Wiring(
        Func<FileSystemBackendBase, string> Describe,
        Func<ServedFileSystems, Delegate> Handler);

    // Every operation in the one list must say how it reaches the wire, or registration would
    // throw at startup instead of failing a test. Asserted by FileSystemOperationsTests.
    public static bool HasWiring(string toolName) => _wiring.ContainsKey(toolName);

    // How each operation reaches the wire. The operations themselves — their names, their backend
    // methods — come from the one list; this only adds the tool signature and the description hook.
    // Every handler takes the request too, which the SDK binds and keeps out of the schema: it is
    // where the caller comes from (As) and which of the server's filesystems the call is for.
    private static readonly IReadOnlyDictionary<string, Wiring> _wiring = new Dictionary<string, Wiring>(StringComparer.Ordinal)
    {
        ["fs_read"] = new(b => b.DescribeRead, served =>
            async (RequestContext<CallToolRequestParams> call, string path, int? offset = null, int? limit = null, CancellationToken ct = default) =>
                ToolResponse.Create(await served.Call(call, "fs_read", fs => fs.ReadAsync(path, offset, limit, ct)))),

        ["fs_info"] = new(b => b.DescribeInfo, served =>
            async (RequestContext<CallToolRequestParams> call, string path, CancellationToken ct = default) =>
                ToolResponse.Create(await served.Call(call, "fs_info", fs => fs.InfoAsync(path, ct)))),

        ["fs_glob"] = new(b => b.DescribeGlob, served =>
            async (RequestContext<CallToolRequestParams> call, string pattern, string basePath = "", CancellationToken ct = default) =>
                ToolResponse.Create(await served.Call(call, "fs_glob", fs => fs.GlobAsync(basePath, pattern, ct)))),

        ["fs_search"] = new(b => b.DescribeSearch, served =>
            async (RequestContext<CallToolRequestParams> call, string query, bool regex = false, string? path = null, string? directoryPath = null,
                    string? filePattern = null, int maxResults = 50, int contextLines = 1,
                    string outputMode = "content", CancellationToken ct = default) =>
                ToolResponse.Create(await served.Call(call, "fs_search", fs => SearchAsync(
                    fs, query, regex, path, directoryPath, filePattern, maxResults, contextLines,
                    outputMode, ct)))),

        ["fs_create"] = new(b => b.DescribeCreate, served =>
            async (RequestContext<CallToolRequestParams> call, string path, string content, bool overwrite = false, bool createDirectories = true, CancellationToken ct = default) =>
                ToolResponse.Create(await served.Call(call, "fs_create", fs => fs.CreateAsync(path, content, overwrite, createDirectories, ct)))),

        ["fs_edit"] = new(b => b.DescribeEdit, served =>
            async (RequestContext<CallToolRequestParams> call, string path, IReadOnlyList<TextEdit> edits, CancellationToken ct = default) =>
                ToolResponse.Create(await served.Call(call, "fs_edit", fs => fs.EditAsync(path, edits, ct)))),

        ["fs_move"] = new(b => b.DescribeMove, served =>
            async (RequestContext<CallToolRequestParams> call, string sourcePath, string destinationPath, CancellationToken ct = default) =>
                ToolResponse.Create(await served.Call(call, "fs_move", fs => fs.MoveAsync(sourcePath, destinationPath, ct)))),

        ["fs_delete"] = new(b => b.DescribeDelete, served =>
            async (RequestContext<CallToolRequestParams> call, string path, CancellationToken ct = default) =>
                ToolResponse.Create(await served.Call(call, "fs_delete", fs => fs.DeleteAsync(path, ct)))),

        ["fs_exec"] = new(b => b.DescribeExec, served =>
            async (RequestContext<CallToolRequestParams> call, string path, string command, int? timeoutSeconds = null, CancellationToken ct = default) =>
                ToolResponse.Create(await served.Call(call, "fs_exec", fs => fs.ExecAsync(path, command, timeoutSeconds, ct)))),

        ["fs_query"] = new(b => b.DescribeQuery, served =>
            // Held to its bounds here as well as by the agent's tool: the server answers whoever calls.
            async (RequestContext<CallToolRequestParams> call, string path, string sql, int maxRows = VfsQueryTool.DefaultMaxRows,
                    CancellationToken ct = default) =>
                ToolResponse.Create(await served.Call(call, "fs_query", fs => fs.QueryAsync(
                    path, sql, Math.Clamp(maxRows, 1, VfsQueryTool.MaxRowsCeiling), ct)))),

        ["fs_copy"] = new(b => b.DescribeCopy, served =>
            async (RequestContext<CallToolRequestParams> call, string sourcePath, string destinationPath, bool overwrite = false, bool createDirectories = true, CancellationToken ct = default) =>
                ToolResponse.Create(await served.Call(call, "fs_copy", fs => fs.CopyAsync(sourcePath, destinationPath, overwrite, createDirectories, ct)))),

        ["fs_blob_read"] = new(b => b.DescribeBlobRead, served =>
            async (RequestContext<CallToolRequestParams> call, string path, long offset = 0, int length = 262144, CancellationToken ct = default) =>
                ToolResponse.Create(await served.Call(call, "fs_blob_read", fs => fs.ReadBlobAsync(path, offset, length, ct)))),

        ["fs_blob_write"] = new(b => b.DescribeBlobWrite, served =>
            async (RequestContext<CallToolRequestParams> call, string path, string contentBase64, long offset = 0, bool overwrite = false, bool createDirectories = true, CancellationToken ct = default) =>
                ToolResponse.Create(await served.Call(call, "fs_blob_write", fs => fs.WriteBlobAsync(path, contentBase64, offset, overwrite, createDirectories, ct)))),

        [FileSystemOperations.MoveOutCheck] = new(b => b.DescribeMoveOutCheck, served =>
            async (RequestContext<CallToolRequestParams> call, string path, CancellationToken ct = default) =>
                ToolResponse.Create(await served.Call(call, FileSystemOperations.MoveOutCheck, fs => fs.MoveOutCheckAsync(path, ct))))
    };

    public static IMcpServerBuilder AddFileSystemTools<TBackend>(this IMcpServerBuilder builder)
        where TBackend : FileSystemBackendBase
    {
        var earlier = builder.Services
            .Select(d => d.ImplementationInstance)
            .OfType<ServedFileSystem>()
            .Select(f => f.BackendType)
            .ToList();
        if (earlier.Contains(typeof(TBackend)))
        {
            throw new InvalidOperationException(
                $"{typeof(TBackend).Name} already serves a filesystem on this server; register each backend type once.");
        }

        // A tool is per operation, not per filesystem: an operation an earlier filesystem already
        // brought has its tool, which dispatches to this one by name.
        var registered = earlier.SelectMany(SupportedToolNames).ToHashSet(StringComparer.Ordinal);
        builder.Services.AddSingleton(new ServedFileSystem(typeof(TBackend)));
        builder.Services.TryAddSingleton(ServedFileSystems.Resolve);

        foreach (var toolName in SupportedToolNames(typeof(TBackend)).Where(name => !registered.Contains(name)))
        {
            var wiring = _wiring[toolName];
            builder.Services.AddSingleton<McpServerTool>(sp =>
            {
                var served = sp.GetRequiredService<ServedFileSystems>();
                return McpServerTool.Create(wiring.Handler(served), new McpServerToolCreateOptions
                {
                    Name = toolName,
                    Description = wiring.Describe(served.FirstOffering(toolName))
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

    // One filesystem a server serves, in the order its AddFileSystemTools calls came: the order a
    // call naming none falls back by.
    private sealed record ServedFileSystem(Type BackendType);

    // Every filesystem the server serves, each with the type its capability is read off — the
    // registered one, as the tools were — so a call is dispatched by the name the agent mounted it
    // under and refused where that filesystem has no override.
    private sealed class ServedFileSystems(IReadOnlyList<(FileSystemBackendBase Backend, IReadOnlySet<string> Operations)> filesystems)
    {
        public static ServedFileSystems Resolve(IServiceProvider sp) => new([
            .. sp.GetServices<ServedFileSystem>().Select(f => (
                (FileSystemBackendBase)sp.GetRequiredService(f.BackendType),
                (IReadOnlySet<string>)SupportedToolNames(f.BackendType).ToHashSet(StringComparer.Ordinal)))
        ]);

        // A shared tool's prose is the first filesystem's that has the operation; the agent filters
        // the raw tools out, and a domain tool's per-mount prose comes from the mount's description.
        public FileSystemBackendBase FirstOffering(string toolName) =>
            filesystems.First(f => f.Operations.Contains(toolName)).Backend;

        public async Task<FsResult<T>> Call<T>(
            RequestContext<CallToolRequestParams> call, string toolName,
            Func<FileSystemBackendBase, Task<FsResult<T>>> operation) where T : class
        {
            var named = NamedFilesystem(call);
            var (backend, operations) = named is null
                ? filesystems[0]
                : filesystems.FirstOrDefault(f => f.Backend.FilesystemName == named);
            if (backend is null)
            {
                return FsError.Invalid<T>(
                    $"This server serves no filesystem named '{named}'; it serves "
                    + $"{string.Join(", ", filesystems.Select(f => f.Backend.FilesystemName))}.");
            }

            // A model-facing operation the filesystem never overrode is answered by the base's own
            // refusal, which names what the mount can do instead; the byte streams have no envelope
            // there and are refused here. The move-out check is the one whose default allows (ADR
            // 0015), so a filesystem with no rule answers it as allowed.
            return operations.Contains(toolName)
                   || toolName == FileSystemOperations.MoveOutCheck
                   || FileSystemOperations.All.Single(o => o.ToolName == toolName).Capability is not null
                ? await operation(As(backend, call))
                : FsError.Fail<T>(ToolError.Codes.UnsupportedOperation,
                    $"The {backend.FilesystemName} filesystem does not support '{toolName}'.");
        }

        // The filesystem the agent named, or null for a call that named none — an agent from before
        // several filesystems per server, or a hand-made call — which goes to the first. Read off
        // the arguments rather than declared on each tool, so every server's tool schema is as it
        // was.
        private static string? NamedFilesystem(RequestContext<CallToolRequestParams> call) =>
            call.Params?.Arguments?.TryGetValue("filesystem", out var named) == true
            && named.ValueKind == JsonValueKind.String
            && named.GetString() is { Length: > 0 } name
                ? name
                : null;

        // The backend as the call's caller sees it: the turn and the household through the one
        // accessor every server reads them by, and the exec bridge's grant off the `_meta` the
        // agent stamps on every hop. Read inside the handler, so a context that does not parse is
        // the caller's error result like any other failure rather than something the envelope
        // never sees.
        private static FileSystemBackendBase As(FileSystemBackendBase backend, RequestContext<CallToolRequestParams> call) =>
            backend.For(new FileSystemCaller(call.Turn, VfsBridgeGrant.Parse(call.Params?.Meta), call.Delegation, call.Household));
    }
}