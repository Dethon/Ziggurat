namespace Domain.Security;

// Which MCP server something is for. Everything that keys on a server reads this and nothing else:
// the call token's audience and the credential the agent presents (Name), the tool qualifier and the
// prompt cache (Qualifier), the deployment's endpoint list and the outpost verdicts (the address
// itself). A server dialled at a URL takes all of it from that URL; one that has no URL of its own
// takes it from a name it was given. Nothing else may read a URL's host to say which server it is,
// so a server addressed some other way needs one more factory here and no change anywhere that keys
// on it (ServerAddressConformanceTests).
//
// Equal as servers, not as strings: a URL's scheme, host, port, path and user info, so two servers
// on one host are two addresses, and one that carries credentials of its own is never the same
// server as the one it reads like.
public sealed record ServerAddress
{
    private ServerAddress(string name, string qualifier, string locator, bool carriesCredentials)
    {
        Name = name;
        Qualifier = qualifier;
        Locator = locator;
        CarriesCredentials = carriesCredentials;
    }

    // The name the server answers to: the audience its call tokens are signed for, which it binds as
    // its own (MCP__AUDIENCE), and the key the agent finds its credential by (MCP__SECRETS__<name>).
    // A URL's host and not its port, so a test server on localhost is "localhost" whatever port it
    // took.
    public string Name { get; }

    // What the server's tools are qualified with (mcp__<qualifier>__<tool>) and its prompts and
    // skills are cached under. The port is in it where the name leaves it out, so two servers on one
    // host keep their tools apart.
    public string Qualifier { get; }

    // An address that carries credentials of its own (`http://mcp-vault:8080@elsewhere/mcp`) reads
    // like a server it may not be, so nothing that hands out a credential matches it.
    public bool CarriesCredentials { get; }

    private string Locator { get; }

    // A server dialled at a URL. Null for an address that is not an absolute URL, which no server
    // answers to.
    public static ServerAddress? FromUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? new ServerAddress(
                uri.Host,
                uri.IsDefaultPort ? uri.Host : $"{uri.Host}-{uri.Port}",
                $"{uri.Scheme}://{uri.UserInfo}@{uri.Host.ToLowerInvariant()}:{uri.Port}{uri.AbsolutePath}",
                uri.UserInfo.Length > 0)
            : null;

    // A server's own address as it was told it: the name it binds as its audience, never a URL.
    public static ServerAddress Named(string name) => new(name, name, $"name:{name}", false);
}