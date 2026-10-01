namespace McpServerSandbox.Settings;

public record McpSettings
{
    public required string ContainerRoot { get; init; }

    // The persistent workspace. Nothing reads it today: the command runner stopped when exec
    // started meaning the container root by the mount point, as every other tool does. It stays
    // because ADR-0025 makes it the workspace the sandbox mount declares — which decides where an
    // attachment lands — so deleting it here would be one commit undoing another.
    public required string HomeDir { get; init; }
    public required int DefaultTimeoutSeconds { get; init; }
    public required int MaxTimeoutSeconds { get; init; }
    public required int OutputCapBytes { get; init; }

    // Where the root launcher listens, announced by the launcher itself to the server it starts
    // (LAUNCHERSOCKET). Absent on a host with no launcher, which runs commands in-process.
    public string? LauncherSocket { get; init; }
}