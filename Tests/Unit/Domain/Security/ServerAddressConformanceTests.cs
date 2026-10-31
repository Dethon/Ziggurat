using System.Text.RegularExpressions;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit.Domain.Security;

// Which server a call is for is read in one place, ServerAddress: the call token's audience, the
// credential the agent presents, the tool qualifier, the prompt cache, the deployment's endpoint list
// and which outposts a session dialled all key on it. A server reached through a tunnel has no host of its own,
// so anything that took a dialled URL's host itself would key that server on nothing — and the only
// thing that can tell such a read from one through the address is the source.
//
// A web page's host is a site, not a server: the browser, the logins it remembers, search results
// and push services read theirs, and are not scanned.
public class ServerAddressConformanceTests
{
    // ShellReach.Host is the sandbox's reach, an enum member, not a URL's host.
    private static readonly Regex _hostRead = new(@"(?<!ShellReach)\.(?:Host|IdnHost|DnsSafeHost)\b");

    private static readonly string _address = Path.Combine("Domain", "Security", "ServerAddress.cs");

    private static readonly string[] _sites =
    [
        Path.Combine("Infrastructure", "Clients", "Browser") + Path.DirectorySeparatorChar,
        Path.Combine("Infrastructure", "Clients", "Push") + Path.DirectorySeparatorChar,
        Path.Combine("Infrastructure", "Clients", "BraveSearchClient.cs"),
        Path.Combine("Domain", "Tools", "Web") + Path.DirectorySeparatorChar
    ];

    [Fact]
    public void NothingButTheAddress_ReadsAServersHost() =>
        Sources()
            .Where(file => file.Key != _address && _hostRead.IsMatch(file.Value))
            .Select(file => file.Key)
            .ShouldBeEmpty(
                "a server is keyed by its ServerAddress (Name for the audience and credential, Qualifier "
                + "for tools and the prompt cache), never by a dialled URL's host; these files read one");

    // Guards the scan above: a pattern or a root that matched nothing would keep it green forever.
    [Fact]
    public void TheAddress_IsWhereTheHostIsRead() =>
        _hostRead.IsMatch(Sources()[_address]).ShouldBeTrue(
            $"{_address} should be the one place that reads a dialled URL's host");

    private static IReadOnlyDictionary<string, string> Sources()
    {
        var root = TestHelpers.FindSolutionRoot();
        return new DirectoryInfo(root)
            .EnumerateDirectories()
            .Where(d => d.Name is "Agent" or "Infrastructure" or "Domain" or "Mcp.Hosting")
            .SelectMany(d => d.EnumerateFiles("*.cs", SearchOption.AllDirectories))
            .Select(f => Path.GetRelativePath(root, f.FullName))
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Where(path => !_sites.Any(site => path.StartsWith(site, StringComparison.Ordinal)))
            .ToDictionary(path => path, path => File.ReadAllText(Path.Combine(root, path)));
    }
}