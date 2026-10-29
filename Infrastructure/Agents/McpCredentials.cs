using Domain.Security;

namespace Infrastructure.Agents;

// The credential the agent presents to each deployment server, found by the host it dials. A host
// with no entry is presented nothing, and its gate refuses; `MissingFor` lets startup say which
// configured server that would be instead.
public sealed class McpCredentials(IReadOnlyDictionary<string, string> byHost)
{
    private readonly Dictionary<string, string> _byHost = byHost
        .Where(e => e.Value.Length > 0)
        .ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);

    public static McpCredentials From(McpGateSettings? gate) => new(gate?.Secrets ?? []);

    public string? For(string address) =>
        HostOf(address) is { } host && _byHost.TryGetValue(host, out var secret) ? secret : null;

    public IReadOnlyList<string> MissingFor(IEnumerable<string> addresses) =>
    [
        .. addresses
            .Select(HostOf)
            .OfType<string>()
            .Where(host => !_byHost.ContainsKey(host))
            .Distinct(StringComparer.OrdinalIgnoreCase)
    ];

    private static string? HostOf(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : null;
}