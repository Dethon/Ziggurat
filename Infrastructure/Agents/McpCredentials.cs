using Domain.Security;

namespace Infrastructure.Agents;

// The credential the agent presents to each deployment server, found by the name the server answers
// to (ServerAddress.Name). A server with no entry is presented nothing, and its gate refuses;
// `MissingFor` lets startup say which configured server that would be instead.
public sealed class McpCredentials(IReadOnlyDictionary<string, string> byName)
{
    private readonly Dictionary<string, string> _byName = byName
        .Where(e => e.Value.Length > 0)
        .ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);

    public static McpCredentials From(McpGateSettings? gate) => new(gate?.Secrets ?? []);

    public string? For(ServerAddress server) => _byName.GetValueOrDefault(server.Name);

    public IReadOnlyList<string> MissingFor(IEnumerable<string> addresses) =>
    [
        .. addresses
            .Select(ServerAddress.FromUrl)
            .OfType<ServerAddress>()
            .Select(server => server.Name)
            .Where(name => !_byName.ContainsKey(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
    ];
}