using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Apps;
using Domain.DTOs.FileSystem;

namespace Domain.Tools.FileSystem.Bridge;

// One read of a built app's, through the bridge's meaning layer: the third adapter over VfsCall, beside
// the file tools and the sandbox's FUSE daemon. Each operation is the file tool's own call, answered with
// the tool's result shape on virtual paths.
public sealed class AppRead(VfsCall call)
{
    public async Task<AppReadAnswer> RunAsync(string operation, JsonObject arguments, CancellationToken ct)
    {
        try
        {
            return operation switch
            {
                AppReads.Operations.Query => await QueryAsync(arguments, ct),
                _ => throw new NotSupportedException(operation)
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or JsonException)
        {
            return AppReadAnswer.Refused(ToolError.Result(ToolError.Codes.InvalidArgument, ex.Message));
        }
    }

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