namespace Infrastructure.Clients.Bash;

public record BashRunnerOptions
{
    public required string ContainerRoot { get; init; }
    public required int DefaultTimeoutSeconds { get; init; }
    public required int MaxTimeoutSeconds { get; init; }
    public required int OutputCapBytes { get; init; }
}