using System.Text.Json.Nodes;

namespace Domain.DTOs.FileSystem;

// The call token, as it rides an exec call's `_meta` beside the conversation context: the per-call
// data channel servers already parse, so neither the backend's exec signature nor the reflected
// fs_exec schema changes. The sandbox hands it to its launcher and nowhere else — never into the
// command's environment or a file the command can read.
public sealed record VfsBridgeGrant(string Token)
{
    public const string MetaKey = "com.herfluffness/vfsBridge";

    public JsonObject ToMeta() => new() { ["token"] = Token };

    public static VfsBridgeGrant? Parse(JsonObject? meta) =>
        meta?[MetaKey]?["token"]?.GetValue<string>() is { Length: > 0 } token ? new VfsBridgeGrant(token) : null;
}