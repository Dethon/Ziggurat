using System.Text.Json.Nodes;

namespace Domain.DTOs.FileSystem;

// The call token, as it rides an exec call's `_meta` beside the conversation context: the per-call
// data channel servers already parse, so neither the backend's exec signature nor the reflected
// fs_exec schema changes. The sandbox hands it to its launcher and nowhere else — never into the
// command's environment or a file the command can read.
//
// `Served` names the mounts the call serves at /vfs, so the call's daemon builds its root from them
// instead of asking the bridge for a listing before the command may start. Null where the agent
// named none: the daemon then lists the root as it always did.
public sealed record VfsBridgeGrant(string Token, IReadOnlyList<string>? Served = null)
{
    public const string MetaKey = "com.herfluffness/vfsBridge";

    public JsonObject ToMeta()
    {
        var meta = new JsonObject { ["token"] = Token };
        if (Served is not null)
        {
            meta["served"] = new JsonArray([.. Served.Select(name => (JsonNode?)name)]);
        }

        return meta;
    }

    public static VfsBridgeGrant? Parse(JsonObject? meta) =>
        meta?[MetaKey]?["token"]?.GetValue<string>() is { Length: > 0 } token
            ? new VfsBridgeGrant(token, meta[MetaKey]?["served"] is JsonArray served
                ? [.. served.Select(n => n?.GetValue<string>()).OfType<string>()]
                : null)
            : null;
}