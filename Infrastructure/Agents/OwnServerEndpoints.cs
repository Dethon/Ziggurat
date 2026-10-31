namespace Infrastructure.Agents;

// A household's own servers named in an agent's endpoint list, resolved to where they are reached
// at the moment a session is built — the moment outposts are asked for too, and for the same reason:
// an agent outlives many sessions, a tunnel opening is one generation, and nothing mutates a session
// already built (ADR 0012). Each keeps its place in the list, so mounts claim their names in the
// order the definition gave.
//
// A household server that cannot be reached is a configured endpoint that will not answer, and fails
// the session as one does today (ADR 0027).
internal static class OwnServerEndpoints
{
    public static IReadOnlyList<McpServerEndpoint> Resolve(
        IReadOnlyList<McpServerEndpoint> endpoints, string? tenantId, IOwnServers? servers) =>
    [
        .. endpoints.Select(endpoint => !endpoint.NamesOwnServer
            ? endpoint
            : servers?.Resolve(tenantId, endpoint.Address)
              ?? throw new HttpRequestException(tenantId is null
                  ? $"The household server '{endpoint.Address}' cannot be reached: this conversation names no household"
                  : $"The household server '{endpoint.Address}' is offline: its tunnel is not open"))
    ];
}