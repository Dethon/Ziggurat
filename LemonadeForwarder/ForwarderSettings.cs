using Domain.Security;

namespace LemonadeForwarder;

// What a household's Lemonade forwarder is told: its tunnel (MCP__TUNNEL__*, as every kit server),
// its household's box, and the loopback port its one listener takes streams on.
public sealed record ForwarderSettings
{
    public McpGateSettings Mcp { get; init; } = new();

    public required BoxSettings Box { get; init; }

    public ListenerSettings Listener { get; init; } = new();
}

public sealed record BoxSettings
{
    // The box's API base on the household's network, ending in its API path, as the Keys page's
    // address takes it: http://192.168.1.20:13305/api/v1.
    public required string Url { get; init; }

    // Only where the box checks one. The forwarder adds it, so it never leaves the house.
    public string ApiKey { get; init; } = "";
}

public sealed record ListenerSettings
{
    public int Port { get; init; } = 8080;
}