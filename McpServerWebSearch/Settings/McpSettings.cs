namespace McpServerWebSearch.Settings;

public record McpSettings
{
    public required BraveSearchConfiguration BraveSearch { get; init; }
    public CapSolverConfiguration? CapSolver { get; init; }
    public CamoufoxConfiguration? Camoufox { get; init; }
    public BrowsingConfiguration Browsing { get; init; } = new();

    // Metrics alone ride this connection: one event per overlay the browser met. Nothing else on
    // this server may use it — page images cross to Redis at the agent's bridge, never here.
    public string RedisConnectionString { get; init; } = "redis:6379";
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