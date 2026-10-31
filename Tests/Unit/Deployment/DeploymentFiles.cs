using System.Text.RegularExpressions;
using Domain.DTOs.Tunnel;
using Tests.E2E.Fixtures;
using YamlDotNet.RepresentationModel;

namespace Tests.Unit.Deployment;

// The deployment's compose files and proxy configuration, read as the operator ships them, so a
// conformance check can say what a service is handed or published rather than grep for it.
public static class DeploymentFiles
{
    public static string Directory => Path.Combine(TestHelpers.FindSolutionRoot(), "DockerCompose");

    // The tenant kit: the compose file a household runs its own servers from, beside the deployment
    // or anywhere else, reaching it through their tunnels alone.
    public static string KitDirectory => Path.Combine(TestHelpers.FindSolutionRoot(), "TenantKit");

    public static ComposeFile Compose(string file = "docker-compose.yml") => ComposeIn(Directory, file);

    public static ComposeFile KitCompose(string file = "docker-compose.yml") => ComposeIn(KitDirectory, file);

    // The tracked secrets file: every placeholder the deployment reads, with whatever value ships.
    public static IReadOnlyDictionary<string, string> Placeholders() => PlaceholdersIn(Directory);

    // The kit's own: what a household fills in, none of it the deployment's.
    public static IReadOnlyDictionary<string, string> KitPlaceholders() => PlaceholdersIn(KitDirectory);

    // Every ${VAR} a compose file reads, defaulted or not: what it is handed from its .env.
    public static IReadOnlyList<string> Variables(string directory, string file = "docker-compose.yml") =>
    [
        .. System.Text.RegularExpressions.Regex
            .Matches(File.ReadAllText(Path.Combine(directory, file)), @"\$\{([A-Za-z_][A-Za-z0-9_]*)")
            .Select(m => m.Groups[1].Value)
            .Distinct()
    ];

    private static ComposeFile ComposeIn(string directory, string file)
    {
        var yaml = new YamlStream();
        using var reader = new StreamReader(Path.Combine(directory, file));
        yaml.Load(reader);

        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var services = root.Children.TryGetValue(new YamlScalarNode("services"), out var node)
            ? ((YamlMappingNode)node).Children.ToDictionary(
                c => ((YamlScalarNode)c.Key).Value!,
                c => ComposeService.From(((YamlScalarNode)c.Key).Value!, (YamlMappingNode)c.Value))
            : [];
        var networks = root.Children.TryGetValue(new YamlScalarNode("networks"), out var declared)
            && declared is YamlMappingNode map
                ? map.Children.ToDictionary(
                    c => ((YamlScalarNode)c.Key).Value!,
                    c => c.Value is YamlMappingNode settings
                         && settings.Children.TryGetValue(new YamlScalarNode("external"), out var external)
                         && external is YamlScalarNode { Value: "true" })
                : [];
        return new ComposeFile(services, networks);
    }

    private static IReadOnlyDictionary<string, string> PlaceholdersIn(string directory) =>
        File.ReadAllLines(Path.Combine(directory, ".env"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#') && l.Contains('='))
            .Select(l => l.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1]);

    public static CaddyConfig Caddyfile() =>
        CaddyConfig.Parse(File.ReadAllText(Path.Combine(Directory, "caddy", "Caddyfile")));

    // The repository's .dockerignore, which every image's build context is filtered through.
    public static DockerIgnore DockerIgnore() =>
        Deployment.DockerIgnore.Parse(
            File.ReadAllText(Path.Combine(TestHelpers.FindSolutionRoot(), ".dockerignore")));

    // Every file a container is built from or configured by: the compose directory whole but for its
    // volumes, every Dockerfile, and each project's settings and the policy files beside its project
    // file. What a test fixture holds must never be in one of them. ./volumes is what running stacks
    // wrote (gitignored, never shipped): the containers' own users own it, so a stack running in this
    // checkout — the smoke stack — would make it unreadable here.
    public static IReadOnlyList<string> Shipped()
    {
        var root = TestHelpers.FindSolutionRoot();
        var projects = System.IO.Directory.EnumerateDirectories(root)
            .Where(d => Path.GetFileName(d) != "Tests" && System.IO.Directory.EnumerateFiles(d, "*.csproj").Any());

        return
        [
            .. SourceFilesUnder(Directory),
            .. SourceFilesUnder(KitDirectory),
            .. System.IO.Directory.EnumerateFiles(root, "Dockerfile*"),
            .. projects.SelectMany(project => SourceFilesUnder(project).Where(file =>
            {
                var name = Path.GetFileName(file);
                return name.StartsWith("Dockerfile", StringComparison.Ordinal)
                       || (name.StartsWith("appsettings", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
                       || (Path.GetDirectoryName(file) == project && name.EndsWith(".json", StringComparison.Ordinal));
            }))
        ];
    }

    private static IEnumerable<string> SourceFilesUnder(string directory) =>
        System.IO.Directory.EnumerateFiles(directory)
            .Concat(System.IO.Directory.EnumerateDirectories(directory)
                .Where(d => Path.GetFileName(d) is not ("bin" or "obj" or "node_modules")
                            && d != Path.Combine(Directory, "volumes") && d != Path.Combine(KitDirectory, "volumes"))
                .SelectMany(SourceFilesUnder));
}

// The browse server's two listeners as it ships them: its appsettings.json, under whatever the tenant
// kit's entry for it sets over it.
public static class BrowseServerListeners
{
    public const string Service = "mcp-websearch";

    public static int Mcp => Port("Mcp");

    public static int Pages => Port("Pages");

    private static int Port(string name)
    {
        var environment = DeploymentFiles.KitCompose().Service(Service).Environment;
        if (environment.TryGetValue($"LISTENERS__{name.ToUpperInvariant()}", out var overridden))
        {
            return int.Parse(overridden);
        }

        var path = Path.Combine(TestHelpers.FindSolutionRoot(), "McpServerWebSearch", "appsettings.json");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path),
            new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true
            });
        return document.RootElement.GetProperty("Listeners").GetProperty(name).GetInt32();
    }
}

// The servers the shipped agent reaches: its agents', its workers' and its channels' endpoints — the
// deployment's own it dials at a URL, a household's it names by the server's name.
public static class ShippedAgentSettings
{
    public static IReadOnlyList<string> DialledHosts() =>
    [
        .. Endpoints()
            .Where(endpoint => !TunnelCredentialProtocol.IsServerName(endpoint))
            .Select(address => new Uri(address).Host)
            .Distinct()
    ];

    public static IReadOnlyList<string> ServerNames() =>
        [.. Endpoints().Where(TunnelCredentialProtocol.IsServerName).Distinct()];

    // A tool server's names: what the agents and workers name for their tools, not the channels.
    public static IReadOnlyList<string> ToolServerNames() =>
        [.. Endpoints(channels: false).Where(TunnelCredentialProtocol.IsServerName).Distinct()];

    public static IReadOnlyList<string> ChannelServerNames() =>
        [.. Endpoints(tools: false).Where(TunnelCredentialProtocol.IsServerName).Distinct()];

    private static IReadOnlyList<string> Endpoints(bool tools = true, bool channels = true)
    {
        var path = Path.Combine(TestHelpers.FindSolutionRoot(), "Agent", "appsettings.json");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path),
            new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true
            });
        var root = document.RootElement;

        IEnumerable<string> Listed(string section, string property) =>
            root.TryGetProperty(section, out var list)
                ? list.EnumerateArray().SelectMany(item => item.TryGetProperty(property, out var value)
                    ? value.ValueKind == System.Text.Json.JsonValueKind.Array
                        ? value.EnumerateArray().Select(v => v.GetString()!)
                        : [value.GetString()!]
                    : [])
                : [];

        IEnumerable<string> tool = tools ? [.. Listed("agents", "mcpServerEndpoints"), .. Listed("subAgents", "mcpServerEndpoints")] : [];
        IEnumerable<string> channel = channels ? Listed("channelEndpoints", "endpoint") : [];
        return [.. tool, .. channel];
    }
}

// The services, and the networks the file declares with whether each is another project's
// (`external: true`) rather than its own.
public sealed record ComposeFile(
    IReadOnlyDictionary<string, ComposeService> Services, IReadOnlyDictionary<string, bool> Networks)
{
    public ComposeService Service(string name) =>
        Services.TryGetValue(name, out var service)
            ? service
            : throw new KeyNotFoundException($"no service '{name}' in the compose file");
}

// A published port: "6379:6379" has no bind address, "127.0.0.1:6017:8080" and
// "${LAN_BIND_ADDRESS:-127.0.0.1}:8123:8123" do.
public sealed record PublishedPort(string? BindAddress, string Host, string Container, string Raw)
{
    public static PublishedPort Parse(string raw)
    {
        var spec = raw.Split('/')[0];
        string? bind = null;
        if (spec.StartsWith("${", StringComparison.Ordinal))
        {
            var end = spec.IndexOf('}');
            bind = spec[..(end + 1)];
            spec = spec[(end + 2)..];
        }

        var parts = spec.Split(':');
        return parts.Length switch
        {
            1 => new PublishedPort(bind, parts[0], parts[0], raw),
            2 => new PublishedPort(bind, parts[0], parts[1], raw),
            _ => new PublishedPort(parts[0], parts[1], parts[2], raw)
        };
    }
}

public sealed record ComposeService(
    string Name,
    IReadOnlyList<PublishedPort> Ports,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<string> EnvFiles,
    IReadOnlyList<string> Volumes,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> Healthcheck,
    string? User,
    IReadOnlyList<string> Profiles,
    IReadOnlyList<string> Networks)
{
    public bool TakesWholeSecretsFile => EnvFiles.Any(f => Path.GetFileName(f) == ".env");

    // `network_mode` (the host's network), or null for a service on compose networks.
    public string? NetworkMode { get; init; }

    // What it is built from, as written: the context and the Dockerfile within it. A build given as a
    // bare path is that context with its own Dockerfile.
    public string? BuildContext { get; init; }

    public string? Dockerfile { get; init; }

    public string? Image { get; init; }

    // The host devices it is handed, each `host:container` as written.
    public IReadOnlyList<string> Devices { get; init; } = [];

    public static ComposeService From(string name, YamlMappingNode node) => new(
        name,
        [.. Sequence(node, "ports").Select(PublishedPort.Parse)],
        EnvironmentOf(node),
        Sequence(node, "env_file"),
        [.. Child(node, "volumes") is YamlSequenceNode volumes ? volumes.Children.Select(VolumeOf) : []],
        Child(node, "depends_on") switch
        {
            YamlMappingNode map => [.. map.Children.Keys.Select(k => ((YamlScalarNode)k).Value!)],
            YamlSequenceNode seq => [.. seq.Children.Select(c => ((YamlScalarNode)c).Value!)],
            _ => []
        },
        Child(node, "healthcheck") is YamlMappingNode health ? Sequence(health, "test") : [],
        Child(node, "user") is YamlScalarNode user ? user.Value : null,
        Sequence(node, "profiles"),
        NetworksOf(node))
    {
        NetworkMode = Child(node, "network_mode") is YamlScalarNode mode ? mode.Value : null,
        BuildContext = Child(node, "build") switch
        {
            YamlScalarNode context => context.Value,
            YamlMappingNode build => Child(build, "context") is YamlScalarNode context ? context.Value : null,
            _ => null
        },
        Dockerfile = Child(node, "build") is YamlMappingNode built && Child(built, "dockerfile") is YamlScalarNode file
            ? file.Value
            : Child(node, "build") is not null ? "Dockerfile" : null,
        Image = Child(node, "image") is YamlScalarNode image ? image.Value : null,
        Devices = Sequence(node, "devices")
    };

    // A list of names or a map of them to their settings; none at all is compose's default network,
    // and a service on another network mode (the host's) is on no compose network.
    private static IReadOnlyList<string> NetworksOf(YamlMappingNode node) =>
        Child(node, "network_mode") is not null
            ? []
            : Child(node, "networks") switch
            {
                YamlMappingNode map => [.. map.Children.Keys.Select(k => ((YamlScalarNode)k).Value!)],
                YamlSequenceNode seq => [.. seq.Children.Select(c => ((YamlScalarNode)c).Value!)],
                _ => ["default"]
            };

    private static YamlNode? Child(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var child) ? child : null;

    private static IReadOnlyList<string> Sequence(YamlMappingNode node, string key) => Child(node, key) switch
    {
        YamlSequenceNode seq => [.. seq.Children.Select(c => ((YamlScalarNode)c).Value!)],
        YamlScalarNode scalar => [scalar.Value!],
        _ => []
    };

    private static string VolumeOf(YamlNode node) => node switch
    {
        YamlScalarNode scalar => scalar.Value!,
        YamlMappingNode map => $"{Value(map, "source")}:{Value(map, "target")}",
        _ => ""
    };

    private static string Value(YamlMappingNode node, string key) =>
        Child(node, key) is YamlScalarNode scalar ? scalar.Value ?? "" : "";

    // A map, possibly merging an anchor with `<<`, or a list of KEY=value.
    private static IReadOnlyDictionary<string, string> EnvironmentOf(YamlMappingNode node) =>
        Child(node, "environment") switch
        {
            YamlMappingNode map => Flatten(map).ToDictionary(),
            YamlSequenceNode seq => seq.Children
                .Select(c => ((YamlScalarNode)c).Value!.Split('=', 2))
                .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : ""),
            _ => new Dictionary<string, string>()
        };

    private static IEnumerable<KeyValuePair<string, string>> Flatten(YamlMappingNode map) =>
        map.Children.SelectMany(c => ((YamlScalarNode)c.Key).Value == "<<"
            ? c.Value switch
            {
                YamlMappingNode merged => Flatten(merged),
                YamlSequenceNode many => many.Children.OfType<YamlMappingNode>().SelectMany(Flatten),
                _ => []
            }
            : [KeyValuePair.Create(((YamlScalarNode)c.Key).Value!, (c.Value as YamlScalarNode)?.Value ?? "")]);
}

// A handle block of one proxied host: its path matcher (null for the catch-all), where it proxies
// to, the session check it asks first if any, and its whole body, so a check can ask what else
// stands in front of the upstream.
public sealed record CaddyRoute(string? Matcher, string? Upstream, string Body)
{
    public CaddyForwardAuth? ForwardAuth { get; } = CaddyForwardAuth.In(Body);
}

// A forward_auth block: whom it asks, at which path, and the headers of the answer it copies onto
// the request it passes on.
public sealed record CaddyForwardAuth(string Upstream, string? Uri, IReadOnlyList<string> CopiedHeaders)
{
    private const string Directive = "forward_auth ";

    public static CaddyForwardAuth? In(string body)
    {
        var lines = body.Split('\n').Select(l => l.Trim()).ToList();
        var start = lines.FindIndex(l => l.StartsWith(Directive, StringComparison.Ordinal));
        if (start < 0)
        {
            return null;
        }

        var block = lines.Skip(start + 1).TakeWhile(l => l != "}").ToList();
        string? Setting(string name) => block
            .FirstOrDefault(l => l.StartsWith(name + " ", StringComparison.Ordinal))?[(name.Length + 1)..].Trim();

        return new CaddyForwardAuth(
            lines[start][Directive.Length..].TrimEnd('{').Trim(),
            Setting("uri"),
            Setting("copy_headers")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? []);
    }

    // A host-level forward_auth block, standing in front of every route of the host.
    public static CaddyForwardAuth Of(string header, string body)
    {
        var settings = body.Split('\n')
            .Select(l => l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToList();
        return new CaddyForwardAuth(
            header["forward_auth".Length..].Trim(),
            settings.FirstOrDefault(s => s is ["uri", _])?[1],
            [.. settings.Where(s => s.FirstOrDefault() == "copy_headers").SelectMany(s => s.Skip(1))]);
    }
}

public sealed record CaddyHost(string Name, IReadOnlyList<CaddyRoute> Routes, string Body, CaddyForwardAuth? ForwardAuth);

public sealed record CaddyConfig(IReadOnlyList<CaddyHost> Hosts)
{
    public CaddyHost Host(string name) =>
        Hosts.FirstOrDefault(h => h.Name == name)
        ?? throw new KeyNotFoundException($"no host '{name}' in the Caddyfile");

    public static CaddyConfig Parse(string text)
    {
        var lines = text.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => !l.TrimStart().StartsWith('#'))
            .ToList();
        return new CaddyConfig([.. Blocks(lines, 0, lines.Count)
            .Select(b =>
            {
                var children = Blocks(lines, b.Start + 1, b.End).ToList();
                return new CaddyHost(
                    b.Header,
                    [.. children
                        .Where(r => r.Header.StartsWith("handle", StringComparison.Ordinal))
                        .Select(r => RouteOf(r.Header, Text(lines, r.Start + 1, r.End)))],
                    Text(lines, b.Start + 1, b.End),
                    children
                        .Where(r => r.Header.StartsWith("forward_auth ", StringComparison.Ordinal))
                        .Select(r => CaddyForwardAuth.Of(r.Header, Text(lines, r.Start + 1, r.End)))
                        .SingleOrDefault());
            })]);
    }

    private static CaddyRoute RouteOf(string header, string body)
    {
        var matcher = header["handle".Length..].Trim();
        var upstream = body.Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("reverse_proxy ", StringComparison.Ordinal))?
            ["reverse_proxy ".Length..].Trim();
        return new CaddyRoute(matcher.Length == 0 ? null : matcher, upstream, body);
    }

    private static string Text(List<string> lines, int from, int to) =>
        string.Join('\n', lines.Skip(from).Take(to - from));

    // The blocks opened at the shallowest depth between two lines: a header ending in `{` and the
    // line holding its matching `}`.
    private static IEnumerable<(string Header, int Start, int End)> Blocks(List<string> lines, int from, int to)
    {
        var depth = 0;
        var start = -1;
        var header = "";
        foreach (var i in Enumerable.Range(from, to - from))
        {
            var line = lines[i].Trim();
            var opens = line.Count(c => c == '{');
            var closes = line.Count(c => c == '}');
            if (depth == 0 && opens > closes)
            {
                start = i;
                header = line.TrimEnd('{').Trim();
            }

            depth += opens - closes;
            if (depth == 0 && start >= 0 && closes > 0)
            {
                yield return (header, start, i);
                start = -1;
            }
        }
    }
}
// A .dockerignore read the way the builder reads it: patterns in order, the last one matching a path
// or any directory above it decides, and a `!` pattern puts back what an earlier one dropped.
public sealed record DockerIgnore(IReadOnlyList<(bool Keeps, Regex Pattern)> Rules)
{
    public static DockerIgnore Parse(string text) => new([
        .. text.Replace("\r\n", "\n").Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Select(line => line.StartsWith('!') ? (Keeps: true, Glob: line[1..]) : (Keeps: false, Glob: line))
            .Select(rule => (rule.Keeps, Compile(rule.Glob.Trim('/'))))
    ]);

    // Whether a path, relative to the repository root, is left out of the build context.
    public bool Excludes(string path)
    {
        var segments = path.Split('/');
        var candidates = Enumerable.Range(1, segments.Length).Select(n => string.Join('/', segments.Take(n))).ToList();

        return Rules
            .Where(rule => candidates.Any(candidate => rule.Pattern.IsMatch(candidate)))
            .Select(rule => !rule.Keeps)
            .LastOrDefault();
    }

    private static Regex Compile(string glob) =>
        new("^" + Regex.Escape(glob)
            .Replace(@"\*\*/", "(.*/)?")
            .Replace(@"/\*\*", "(/.*)?")
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            .Replace(@"\?", "[^/]") + "$");
}