using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Apps;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Microsoft.Extensions.AI;

namespace Domain.Tools.FileSystem.Bridge;

// One read of a built app's, through the bridge's meaning layer: the third adapter over VfsCall, beside
// the file tools and the sandbox's FUSE daemon. Each operation is the file tool's own call, answered with
// the tool's result shape on virtual paths, and allowed only where the call would run unasked (VfsCall).
// An app reads only the mount points its manifest's `reads` names, and nothing reachable here changes
// anything: every other operation is refused with the permission envelope.
// `offline`: the household's own servers the agent names that this build did not reach.
public sealed class AppRead(VfsCall call, IReadOnlyList<string> reads, IReadOnlyList<string> offline)
{
    public async Task<AppReadAnswer> RunAsync(string operation, JsonObject arguments, CancellationToken ct)
    {
        if (!AppReads.Operations.All.Contains(operation))
        {
            return AppReadAnswer.Refused(ToolError.PermissionDenied(
                $"{operation} is not a read, and a built app only reads.",
                $"Read with one of: {string.Join(", ", AppReads.Operations.All)}."));
        }

        // Only reading the arguments is the caller's to get wrong; a fault past them is the fault it is.
        Planned read;
        try
        {
            read = Plan(operation, arguments, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException or ArgumentException)
        {
            return AppReadAnswer.Refused(ToolError.Result(ToolError.Codes.InvalidArgument, ex.Message));
        }

        return Outside(read.Path) is { } refusal ? AppReadAnswer.Refused(refusal) : await read.Run();
    }

    // The path an operation reads, by the argument the tool takes it in, and the tool's own call on it.
    private Planned Plan(string operation, JsonObject arguments, CancellationToken ct)
    {
        switch (operation)
        {
            case AppReads.Operations.Read:
                {
                    var path = Required(arguments, VfsFileReadTool.FilePathParameter);
                    var (offset, limit) = (Number(arguments, "offset"), Number(arguments, "limit"));
                    return Tool(path, VfsFileReadTool.Name,
                        (registry, caller) => new VfsFileReadTool(registry).RunAsync(path, offset, limit, caller, ct));
                }
            case AppReads.Operations.Glob:
                {
                    var path = Required(arguments, "basePath");
                    var pattern = Required(arguments, "pattern");
                    return Tool(path, VfsGlobFilesTool.Name,
                        (registry, caller) => new VfsGlobFilesTool(registry).RunAsync(path, pattern, caller, ct));
                }
            case AppReads.Operations.Info:
                {
                    var path = Required(arguments, "path");
                    return Tool(path, VfsFileInfoTool.Name, (registry, caller) => new VfsFileInfoTool(registry).RunAsync(path, caller, ct));
                }
            case AppReads.Operations.Search:
                {
                    // One file where the read names one, as the tool prefers it, else a directory.
                    var query = Required(arguments, "query");
                    var filePath = Text(arguments, "filePath");
                    var directoryPath = Text(arguments, "directoryPath");
                    var path = filePath ?? directoryPath ?? throw new InvalidOperationException("filePath or directoryPath is required.");
                    var regex = Flag(arguments, "regex") ?? false;
                    var filePattern = Text(arguments, "filePattern");
                    var maxResults = Number(arguments, "maxResults") ?? 50;
                    var contextLines = Number(arguments, "contextLines") ?? 1;
                    var outputMode = Text(arguments, "outputMode") is { } mode
                        ? Enum.Parse<VfsTextSearchOutputMode>(mode, ignoreCase: true)
                        : VfsTextSearchOutputMode.Content;
                    return Tool(path, VfsTextSearchTool.Name, (registry, caller) => new VfsTextSearchTool(registry).RunAsync(
                        query, regex, filePath, directoryPath, filePattern, maxResults, contextLines, outputMode, caller, ct));
                }
            case AppReads.Operations.Query:
                {
                    var path = Required(arguments, "path");
                    var sql = Required(arguments, "sql");
                    var maxRows = Number(arguments, "maxRows") ?? VfsQueryTool.DefaultMaxRows;
                    return Tool(path, VfsQueryTool.Name,
                        (registry, caller) => new VfsQueryTool(registry).RunAsync(path, sql, maxRows, caller, ct));
                }
            default:
                {
                    // The bridge's own read: the bytes where the mount has them, its rendered text where it has
                    // none, refused the moment it passes the call's ceiling.
                    var path = Required(arguments, "path");
                    return new Planned(path, async () => await call.ReadAsync(path, ct) switch
                    {
                        BridgeAnswer<byte[]>.Ok ok => new AppReadAnswer.Bytes(ok.Value),
                        BridgeAnswer<byte[]>.Refused { Error: { } error } => AppReadAnswer.Refused(error),
                        _ => throw new InvalidOperationException("A refused read carries its envelope.")
                    });
                }
        }
    }

    private Planned Tool(string path, string toolName, Func<IVirtualFileSystemRegistry, AIFunctionArguments, Task<JsonNode>> run) =>
        new(path, async () => AppReadAnswer.Of(await call.ToolAsync(path, toolName, run)));

    private sealed record Planned(string Path, Func<Task<AppReadAnswer>> Run);

    // The mount a path resolves to, or for a path nothing is mounted at, the mount point it names. A
    // mount the app may read that is not there while a household server is offline is that server's,
    // as likely as not: which mount point a server publishes is known only once it is reached, so the
    // read is unavailable rather than missing, and worth asking again.
    private ToolErrorResult? Outside(string path)
    {
        var mounted = call.MountPointOf(path);
        var mountPoint = mounted ?? "/" + path.Trim('/').Split('/')[0];
        if (!reads.Contains(mountPoint, StringComparer.Ordinal))
        {
            return ToolError.PermissionDenied(
                $"{path} is outside what this app reads: its manifest's reads are {(reads.Count == 0 ? "none" : string.Join(", ", reads))}.",
                "Name the mount point in the manifest's reads and publish the app again.");
        }

        return mounted is null && offline.Count > 0
            ? CapabilityError.For(CapabilityState.Unavailable,
                $"{mountPoint} is not mounted while the household's {string.Join(", ", offline)} {(offline.Count == 1 ? "is" : "are")} offline.",
                "Read again once it is back; nothing was read.")
            : null;
    }

    // The tool's own arguments by its own names; a wrong type is the caller's invalid argument.
    private static string Required(JsonObject arguments, string name) =>
        Text(arguments, name) ?? throw new InvalidOperationException($"{name} is required.");

    private static string? Text(JsonObject arguments, string name) => arguments[name]?.GetValue<string>();

    private static int? Number(JsonObject arguments, string name) => arguments[name]?.GetValue<int>();

    private static bool? Flag(JsonObject arguments, string name) => arguments[name]?.GetValue<bool>();
}

// What a read answers: the tool's result (a value or its envelope), or a file's bytes.
public abstract record AppReadAnswer
{
    private AppReadAnswer() { }

    public sealed record Json(JsonNode Node) : AppReadAnswer
    {
        public bool IsRefusal => Node is JsonObject { } result && result["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var value) && !value;
    }

    public sealed record Bytes(byte[] Content) : AppReadAnswer;

    public static AppReadAnswer Of(JsonNode node) => new Json(node);

    public static AppReadAnswer Refused(ToolErrorResult error) => new Json(error.ToNode());
}