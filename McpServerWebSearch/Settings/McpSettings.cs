using Domain.Security;
using Domain.Tools.Web;
using Infrastructure.Judgments;
using Mcp.Hosting;

namespace McpServerWebSearch.Settings;

public record McpSettings : IMcpHostSettings
{
    // This server's own credential, which its /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    public required BraveSearchConfiguration BraveSearch { get; init; }
    public CapSolverConfiguration? CapSolver { get; init; }
    public CamoufoxConfiguration? Camoufox { get; init; }
    public BrowsingConfiguration Browsing { get; init; } = new();

    // The login handoff's link and lifetimes.
    public HandoffConfiguration Handoff { get; init; } = new();

    // Remembered logins: the key their records are sealed with, and how soon after use a person's
    // context is saved again.
    public LoginsConfiguration Logins { get; init; } = new();

    // Metrics ride this connection — one event per overlay the browser met — and so do the login
    // handoff's one-time links and each person's remembered logins, sealed. Page images still cross
    // to Redis at the agent's bridge, never here.
    public string RedisConnectionString { get; init; } = "redis:6379";

    // Jev, the hosted judge this server asks which of a wall's buttons closes it. It is asked
    // through OpenRouter, so the key is OPENROUTER__APIKEY, the same line the agent reads; empty
    // means the judgment is off, never a startup failure.
    public OpenRouterKey OpenRouter { get; init; } = new();
    public TypeSafeOptions TypeSafe { get; init; } = new();

    // The modal judgment's tunables (Domain's ModalJudgmentSettings), beside the judge they tune.
    public ModalJudgmentSettings Judgment { get; init; } = new();
}

// Generic tunables of the browse session pool — settings in this server's own appsettings.json,
// never a compose entry.
public record BrowsingConfiguration
{
    // How many tabs one browse session keeps alive; the least-recently-touched dies at the cap.
    public int TabCap { get; init; } = 3;

    // The session's one idle clock, refreshed by every routed call.
    public int SessionIdleTimeoutMinutes { get; init; } = 30;
}

public record HandoffConfiguration
{
    // Where people reach this server's /browse pages: the assistants host, behind Caddy. Per
    // deployment, a compose entry; empty is the handoff off.
    public string PublicBaseUrl { get; init; } = "";

    // How long a handoff link can be opened, and how long the view lives once it is.
    public int TicketMinutes { get; init; } = 10;
    public int ViewMinutes { get; init; } = 30;
}

public record LoginsConfiguration
{
    // The base64 of 32 random bytes, from secrets (LOGINS__STATEKEY); empty is remembering off.
    public string StateKey { get; init; } = "";

    public int SaveDebounceSeconds { get; init; } = 5;

    // The most a person's storage state may take in their record. Past it every origin's
    // IndexedDB is left out, then localStorage; the cookies are always saved.
    public long MaxRecordBytes { get; init; } = 8 * 1024 * 1024;
}

public record BraveSearchConfiguration
{
    public required string ApiKey { get; init; }
    public string ApiUrl { get; init; } = "https://api.search.brave.com/res/v1/";
}

public record CapSolverConfiguration
{
    public required string ApiKey { get; init; }
}

public record CamoufoxConfiguration
{
    public required string WsEndpoint { get; init; }
}