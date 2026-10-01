namespace Infrastructure.Clients.Bash;

public record BashRunnerOptions
{
    public required string ContainerRoot { get; init; }
    public required int DefaultTimeoutSeconds { get; init; }
    public required int MaxTimeoutSeconds { get; init; }
    public required int OutputCapBytes { get; init; }

    // Exactly what a command's environment holds, or null to inherit the server's. The sandbox
    // sets it (CommandEnvironment.Minimal); an outpost leaves it null, because there the command
    // runs on its operator's own machine as its operator, in the environment they started it with.
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}