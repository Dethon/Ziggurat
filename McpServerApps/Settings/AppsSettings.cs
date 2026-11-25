using Domain.Security;
using Infrastructure.Clients.Accounts;
using Mcp.Hosting;

namespace McpServerApps.Settings;

public record AppsSettings : IMcpHostSettings
{
    // This server's own credential, which its /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    // Where the accounts service answers the calling household's agent-names view: only the
    // household's own agents build apps.
    public AccountsClientConfig Accounts { get; init; } = new();

    // The volume: a directory per household, and in it one directory per built app holding its git
    // repository, its working tree and its metadata.
    public string StoragePath { get; init; } = "/data";

    // The registrable domain built apps are served under (`<slug>-<id>.<AppsDomain>`): per
    // deployment, so compose sets it (APPSDOMAIN); a test stack names a local one.
    public required string AppsDomain { get; init; }

    public AppQuotas Quotas { get; init; } = new();
}

// What one household may spend of the deployment's shared storage. A refusal names the limit and
// its value, so the agent tells the person rather than retrying.
public record AppQuotas
{
    public int AppsPerHousehold { get; init; } = 20;

    // The whole repository: its working tree and its history.
    public long BytesPerRepository { get; init; } = 50L * 1024 * 1024;
}