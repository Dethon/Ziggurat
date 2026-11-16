using Domain.DTOs.Tunnel;

namespace Domain.Security;

// Which MCP server something is for. Everything that keys on a server reads this and nothing else:
// the call token's audience and the credential the agent presents (Name), the tool qualifier
// (Qualifier), the prompt cache (CacheKey), the deployment's endpoint list and which outposts a session
// dialled (the address itself). A server dialled at a URL takes all of it from that URL; one that has no URL
// of its own — a server's own name as it was told it, a household's server reached through its
// tunnel — takes it from a factory here. Nothing else may read a URL's host to say which server it
// is (ServerAddressConformanceTests).
//
// Equal as servers, not as strings: a URL's scheme, host, port, path and user info, so two servers
// on one host are two addresses, and one that carries credentials of its own is never the same
// server as the one it reads like.
public sealed record ServerAddress
{
    private ServerAddress(string name, string qualifier, string cacheKey, string locator, bool carriesCredentials)
    {
        Name = name;
        Qualifier = qualifier;
        CacheKey = cacheKey;
        Locator = locator;
        CarriesCredentials = carriesCredentials;
    }

    // The name the server answers to: the audience its call tokens are signed for, which it binds as
    // its own (MCP__AUDIENCE, or the one its tunnel is admitted with), and the key the agent finds a
    // deployment server's credential by (MCP__SECRETS__<name>). A URL's host and not its port, so a
    // test server on localhost is "localhost" whatever port it took.
    public string Name { get; }

    // What the server's tools are qualified with (mcp__<qualifier>__<tool>). The port is in it where
    // the name leaves it out, so two servers on one host keep their tools apart.
    public string Qualifier { get; }

    // What its prompts and skills are cached under, in the one cache every agent the process builds
    // shares: the qualifier for a URL's server, and for a household's own server its full name, which
    // carries the household — two households' servers of one name are two servers, and one
    // household's server prompts are never served to another.
    public string CacheKey { get; }

    // An address that carries credentials of its own (`http://mcp-vault:8080@elsewhere/mcp`) reads
    // like a server it may not be, so nothing that hands out a credential matches it.
    public bool CarriesCredentials { get; }

    private string Locator { get; }

    // A server dialled at a URL. Null for an address that is not an absolute http(s) URL with a host,
    // which no server answers to — and which a household's server, named rather than addressed, must
    // never be mistaken for.
    public static ServerAddress? FromUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.Host.Length == 0)
        {
            return null;
        }

        var qualifier = uri.IsDefaultPort ? uri.Host : $"{uri.Host}-{uri.Port}";
        return new ServerAddress(
            uri.Host,
            qualifier,
            qualifier,
            $"{uri.Scheme}://{uri.UserInfo}@{uri.Host.ToLowerInvariant()}:{uri.Port}{uri.AbsolutePath}",
            uri.UserInfo.Length > 0);
    }

    // A server's own address as it was told it: the name it binds as its audience, never a URL.
    public static ServerAddress Named(string name) => new(name, name, name, $"name:{name}", false);

    // A household's own server, reached through its tunnel and never dialled: its name is the
    // server's within the household (a DNS label, TunnelCredentialProtocol.IsServerName) under the
    // household's id, a hostname the agent's client can put in a request without it resolving
    // anywhere. Its tools are qualified with the server's name alone: a turn is one household's, so
    // the household never tells two of one turn's tools apart, and a definition's whitelist
    // (mcp__websearch*) reads the same for every household.
    public static ServerAddress Tunnelled(string household, string serverName)
    {
        if (!TunnelCredentialProtocol.IsServerName(serverName))
        {
            throw new ArgumentException($"'{serverName}' is not a server's name", nameof(serverName));
        }

        if (!TunnelCredentialProtocol.IsServerName(household))
        {
            throw new ArgumentException($"'{household}' is not a household's id", nameof(household));
        }

        var name = $"{serverName}.{household}.tunnel";
        return new ServerAddress(name, serverName, name, $"tunnel:{name}", false);
    }
}