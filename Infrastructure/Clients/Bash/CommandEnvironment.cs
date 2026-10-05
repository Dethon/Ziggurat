namespace Infrastructure.Clients.Bash;

// What a sandbox command is allowed to know about the process that started it. The server's
// environment holds the deployment's MCP secret (and once held its whole secrets file, which
// compose no longer hands this container), and a child inherits its parent's environment by
// default, so without this a command could print it. The list is an allowlist on purpose: a
// secret that reaches the server's environment later is excluded without anyone remembering to
// exclude it.
public static class CommandEnvironment
{
    public const string DefaultPath = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";

    // HOME is not inherited: it is the workspace the mount publishes, so a command's ~ is the
    // volume and not whatever home the server's own uid happens to have.
    private static readonly string[] _inherited = ["PATH", "TZ", "LANG", "LANGUAGE", "LC_ALL"];

    public static IReadOnlySet<string> Names { get; } =
        new HashSet<string>(["HOME", .. _inherited], StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, string> Minimal(string home, Func<string, string?> server)
    {
        var environment = _inherited
            .Select(name => (Name: name, Value: server(name)))
            .Where(v => !string.IsNullOrEmpty(v.Value))
            .ToDictionary(v => v.Name, v => v.Value!, StringComparer.Ordinal);
        environment["HOME"] = home;
        environment.TryAdd("PATH", DefaultPath);
        return environment;
    }
}