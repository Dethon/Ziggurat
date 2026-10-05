using Domain.Security;
using Mcp.Hosting;

namespace McpServerSandbox.Settings;

public record McpSettings : IMcpHostSettings
{
    // The deployment secret this server's /mcp asks for (MCP__SHAREDSECRET).
    public McpGateSettings Mcp { get; init; } = new();

    public required string ContainerRoot { get; init; }

    // The persistent workspace: the HOME a command gets (CommandEnvironment.Minimal), and — ADR-0025
    // — the workspace the sandbox mount declares, which decides where an attachment lands. Not a
    // working directory: exec means the container root by the mount point, as every other tool does.
    public required string HomeDir { get; init; }
    public required int DefaultTimeoutSeconds { get; init; }
    public required int MaxTimeoutSeconds { get; init; }
    public required int OutputCapBytes { get; init; }

    // Where the root launcher listens, announced by the launcher itself to the server it starts
    // (LAUNCHERSOCKET). Absent on a host with no launcher, which runs commands in-process.
    public string? LauncherSocket { get; init; }

    // Where the agent's exec bridge answers, as this container reaches it — topology, so compose
    // sets it. Absent, a command sees only the sandbox's own disk.
    public string? VfsBridgeUrl { get; init; }

    // A command sees the session's other mounts only with both: a launcher to mount them in the
    // command's namespace, and a bridge for its daemon to ask. What the prompt and the skill say
    // follows this, so they never promise mounts a command will not find.
    public bool ServesMounts => !string.IsNullOrEmpty(LauncherSocket) && !string.IsNullOrEmpty(VfsBridgeUrl);
}