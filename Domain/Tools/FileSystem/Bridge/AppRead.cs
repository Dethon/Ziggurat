using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Apps;
using Domain.DTOs;
using Domain.DTOs.FileSystem;

namespace Domain.Tools.FileSystem.Bridge;

// One read of a built app's, through the bridge's meaning layer: the third adapter over VfsCall, beside
// the file tools and the sandbox's FUSE daemon. Each operation is the file tool's own call, answered with
// the tool's result shape on virtual paths, and allowed only where the call would run unasked (VfsCall).
// An app reads only the mount points its manifest's `reads` names, and nothing reachable here changes
// anything: every other operation is refused with the permission envelope.
public sealed class AppRead(VfsCall call, IReadOnlyList<string> reads)
{
    public async Task<AppReadAnswer> RunAsync(string operation, JsonObject arguments, CancellationToken ct)
    {
        if (!AppReads.Operations.All.Contains(operation))
        {
            return AppReadAnswer.Refused(ToolError.PermissionDenied(
                $"{operation} is not a read, and a built app only reads.",
                $"Read with one of: {string.Join(", ", AppReads.Operations.All)}."));
        }

        try
        {
            if (Outside(PathOf(operation, arguments)) is { } refusal)
            {
                return AppReadAnswer.Refused(refusal);
            }

            return operation switch
            {
                AppReads.Operations.Read => await ReadAsync(arguments, ct),
                AppReads.Operations.Glob => await GlobAsync(arguments, ct),
                AppReads.Operations.Info => await InfoAsync(arguments, ct),
                AppReads.Operations.Search => await SearchAsync(arguments, ct),
                AppReads.Operations.Query => await QueryAsync(arguments, ct),
                AppReads.Operations.Blob => await BlobAsync(arguments, ct),
                _ => throw new NotSupportedException(operation)
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException or ArgumentException)
        {
            return AppReadAnswer.Refused(ToolError.Result(ToolError.Codes.InvalidArgument, ex.Message));
        }
    }

    // The path an operation reads, by the argument the tool takes it in.
    private static string PathOf(string operation, JsonObject arguments) => operation switch
    {
        AppReads.Operations.Read => Required(arguments, VfsFileReadTool.FilePathParameter),
        AppReads.Operations.Glob => Required(arguments, "basePath"),
        AppReads.Operations.Search => Text(arguments, "filePath") ?? Text(arguments, "directoryPath")
            ?? throw new InvalidOperationException("filePath or directoryPath is required."),
        _ => Required(arguments, "path")
    };

    // The mount a path resolves to, or for a path nothing is mounted at, the mount point it names.
    private ToolErrorResult? Outside(string path)
    {
        var mountPoint = call.MountPointOf(path) ?? "/" + path.Trim('/').Split('/')[0];
        return reads.Contains(mountPoint, StringComparer.Ordinal)
            ? null
            : ToolError.PermissionDenied(
                $"{path} is outside what this app reads: its manifest's reads are {(reads.Count == 0 ? "none" : string.Join(", ", reads))}.",
                "Name the mount point in the manifest's reads and publish the app again.");
    }

    private async Task<AppReadAnswer> ReadAsync(JsonObject arguments, CancellationToken ct)
    {
        var path = Required(arguments, VfsFileReadTool.FilePathParameter);
        var offset = Number(arguments, "offset");
        var limit = Number(arguments, "limit");
        return AppReadAnswer.Of(await call.ToolAsync(path, VfsFileReadTool.Name,
            (registry, caller) => new VfsFileReadTool(registry).RunAsync(path, offset, limit, caller, ct)));
    }

    private async Task<AppReadAnswer> GlobAsync(JsonObject arguments, CancellationToken ct)
    {
        var path = Required(arguments, "basePath");
        var pattern = Required(arguments, "pattern");
        return AppReadAnswer.Of(await call.ToolAsync(path, VfsGlobFilesTool.Name,
            (registry, caller) => new VfsGlobFilesTool(registry).RunAsync(path, pattern, caller, ct)));
    }

    private async Task<AppReadAnswer> InfoAsync(JsonObject arguments, CancellationToken ct)
    {
        var path = Required(arguments, "path");
        return AppReadAnswer.Of(await call.ToolAsync(path, VfsFileInfoTool.Name,
            (registry, caller) => new VfsFileInfoTool(registry).RunAsync(path, caller, ct)));
    }

    // One file where the read names one, as the tool prefers it, else a directory.
    private async Task<AppReadAnswer> SearchAsync(JsonObject arguments, CancellationToken ct)
    {
        var query = Required(arguments, "query");
        var filePath = Text(arguments, "filePath");
        var directoryPath = Text(arguments, "directoryPath");
        var path = filePath ?? directoryPath ?? throw new InvalidOperationException("filePath or directoryPath is required.");
        var outputMode = Text(arguments, "outputMode") is { } mode
            ? Enum.Parse<VfsTextSearchOutputMode>(mode, ignoreCase: true)
            : VfsTextSearchOutputMode.Content;
        return AppReadAnswer.Of(await call.ToolAsync(path, VfsTextSearchTool.Name,
            (registry, caller) => new VfsTextSearchTool(registry).RunAsync(
                query, Flag(arguments, "regex") ?? false, filePath, directoryPath, Text(arguments, "filePattern"),
                Number(arguments, "maxResults") ?? 50, Number(arguments, "contextLines") ?? 1, outputMode, caller, ct)));
    }

    // The bridge's own read: the bytes where the mount has them, its rendered text where it has none,
    // refused the moment it passes the call's ceiling.
    private async Task<AppReadAnswer> BlobAsync(JsonObject arguments, CancellationToken ct) =>
        await call.ReadAsync(Required(arguments, "path"), ct) switch
        {
            BridgeAnswer<byte[]>.Ok ok => new AppReadAnswer.Bytes(ok.Value),
            BridgeAnswer<byte[]>.Refused { Error: { } error } => AppReadAnswer.Refused(error),
            _ => throw new InvalidOperationException("A refused read carries its envelope.")
        };

    private async Task<AppReadAnswer> QueryAsync(JsonObject arguments, CancellationToken ct)
    {
        var path = Required(arguments, "path");
        var sql = Required(arguments, "sql");
        var maxRows = Number(arguments, "maxRows") ?? VfsQueryTool.DefaultMaxRows;
        return AppReadAnswer.Of(await call.ToolAsync(path, VfsQueryTool.Name,
            (registry, caller) => new VfsQueryTool(registry).RunAsync(path, sql, maxRows, caller, ct)));
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