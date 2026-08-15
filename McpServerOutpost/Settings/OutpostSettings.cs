namespace McpServerOutpost.Settings;

public record OutpostSettings
{
    // The one documented default, so a firewall rule is writable once. An operator running a
    // jailed outpost and an unjailed one on the same machine overrides it on the second.
    public const int DefaultPort = 8099;

    // The name the mount is addressed by, so a person can tell their laptop from their desktop
    // when they talk to the agent.
    public required string Name { get; init; }

    // Where files land and commands run. It is the mount's declared workspace, and — when the
    // outpost is jailed — the only part of the machine any operation will touch.
    public required string WorkingDirectory { get; init; }

    public bool Jailed { get; init; }

    // Off unless asked for: exposing somebody's files must not imply exposing a shell on their
    // computer, and the safe thing has to be what happens when the binary is run without reading
    // anything.
    public bool Exec { get; init; }

    public int Port { get; init; } = DefaultPort;

    // The command runner's bounds. Not flags: nobody starting a binary on their laptop has an
    // opinion about an output cap, and these are the sandbox's own numbers so a command behaves
    // the same wherever the agent runs it. They bind from the environment like anything else if a
    // deployment ever needs to differ.
    public int DefaultTimeoutSeconds { get; init; } = 60;
    public int MaxTimeoutSeconds { get; init; } = 1800;
    public int OutputCapBytes { get; init; } = 65536;

    // Comma-separated, replacing the shared list wholesale for this machine — a computer full of
    // unusual source files is still readable. Absent leaves the shared list alone.
    public string? Ext { get; init; }
}