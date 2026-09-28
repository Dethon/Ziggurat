namespace Domain.DTOs;

public record FileSystemMount(string Name, string MountPoint, string Description)
{
    // Domain-tool leaf names (file_read, glob, exec, …) the backing MCP server actually exposes,
    // derived at discovery from its advertised fs_* tool set. Empty when unknown.
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    // The writable, persistent directory under this mount, as the backend spells it, or null where
    // the mount declares none — which is most of them. An attachment lands here, and a mount that
    // declares nothing lands nothing rather than falling back to the mount root (ADR 0025).
    public string? Workspace { get; init; }

    // Whether attachments may be put into this mount. A separate claim from being able to run
    // commands: an outpost is a filesystem on somebody's real machine and may well execute, and a
    // person's files still belong in the sandbox (ADR 0025, refined).
    public bool IsLandingTarget { get; init; }

    // How far a command run through this mount's exec reaches, or null where it has no shell. The
    // exec screen asks this rather than the mount's name or its capabilities: a sandbox command
    // runs in the deployment's own container, an outpost's on a person's own computer, and /ha's
    // exec is a service call with no shell behind it.
    public ShellReach? ShellReach { get; init; }
}