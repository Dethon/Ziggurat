namespace Dashboard.Client.State.Health;

// A tile is a service of one household, or of the deployment when Household is null: two
// households' voice servers are two tiles.
public record ServiceHealth(string Service, bool IsHealthy, string? LastSeen, string? Household = null);

public record HealthState
{
    public IReadOnlyList<ServiceHealth> Services { get; init; } = [];
}