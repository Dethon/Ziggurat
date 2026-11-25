using System.Text.Json.Nodes;

namespace Domain.Apps;

// The app read API's wire, spelled once for its three ends: the client library a built app's page
// calls (OriginPath on the app's own origin), the apps server that answers it there and forwards it,
// and the agent route that reads (AgentPath, on the deployment's network alone: the proxy routes
// nothing under /internal). An operation is the last path segment; its arguments are the file tool's
// own, by the names the tool takes them.
//
// What the apps server sends the agent: the app (its id, building agent and `reads`), believed on the
// apps server's own credential, and the viewer's current `apps` assertion, which alone says who reads.
public static class AppReads
{
    public const string AgentPath = "/internal/app-reads";
    public const string OriginPath = "/_vfs";

    // A check's reads carry, in place of an `apps` assertion, the token the agent signed for the tool call
    // that ran `./check` (to the apps server, naming that call's person), handed back as it came.
    public const string CheckCallTokenHeader = "X-Ziggurat-Check-Call-Token";

    public static string AgentRoute(string operation) => $"{AgentPath}/{operation}";

    // Every operation is a read; nothing reachable here changes anything.
    public static class Operations
    {
        public const string Read = "read";
        public const string Glob = "glob";
        public const string Info = "info";
        public const string Search = "search";
        public const string Query = "query";

        // The file's bytes, whole, up to the bridge's ceiling: an image, a table's Parquet file.
        public const string Blob = "blob";

        public static readonly IReadOnlyList<string> All = [Read, Glob, Info, Search, Query, Blob];
    }
}

// One read as the apps server forwards it.
public sealed record AppReadRequest
{
    public required AppReadScope App { get; init; }
    public JsonObject Arguments { get; init; } = [];
}

// The app a read is for, as the apps server keeps it: never the page's word.
public sealed record AppReadScope
{
    public required string Id { get; init; }

    // The household agent whose turn created the app, whose mounts and whitelist it reads through.
    public required string Agent { get; init; }

    // The manifest's mount points (`/sugary`), of the origin the page is on.
    public IReadOnlyList<string> Reads { get; init; } = [];
}