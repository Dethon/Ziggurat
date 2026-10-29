using Tests.E2E.Fixtures;
using YamlDotNet.RepresentationModel;

namespace Tests.Unit.Deployment;

// The deployment's compose files and proxy configuration, read as the operator ships them, so a
// conformance check can say what a service is handed or published rather than grep for it.
public static class DeploymentFiles
{
    public static string Directory => Path.Combine(TestHelpers.FindSolutionRoot(), "DockerCompose");

    public static ComposeFile Compose(string file = "docker-compose.yml")
    {
        var yaml = new YamlStream();
        using var reader = new StreamReader(Path.Combine(Directory, file));
        yaml.Load(reader);

        var root = (YamlMappingNode)yaml.Documents[0].RootNode;
        var services = root.Children.TryGetValue(new YamlScalarNode("services"), out var node)
            ? ((YamlMappingNode)node).Children.ToDictionary(
                c => ((YamlScalarNode)c.Key).Value!,
                c => ComposeService.From(((YamlScalarNode)c.Key).Value!, (YamlMappingNode)c.Value))
            : [];
        return new ComposeFile(services);
    }

    public static CaddyConfig Caddyfile() =>
        CaddyConfig.Parse(File.ReadAllText(Path.Combine(Directory, "caddy", "Caddyfile")));
}

public sealed record ComposeFile(IReadOnlyDictionary<string, ComposeService> Services)
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
    IReadOnlyList<string> Healthcheck)
{
    public bool TakesWholeSecretsFile => EnvFiles.Any(f => Path.GetFileName(f) == ".env");

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
        Child(node, "healthcheck") is YamlMappingNode health ? Sequence(health, "test") : []);

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
// to, and its whole body, so a check can ask what else stands in front of the upstream.
public sealed record CaddyRoute(string? Matcher, string? Upstream, string Body);

public sealed record CaddyHost(string Name, IReadOnlyList<CaddyRoute> Routes, string Body);

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
            .Select(b => new CaddyHost(
                b.Header,
                [.. Blocks(lines, b.Start + 1, b.End)
                    .Where(r => r.Header.StartsWith("handle", StringComparison.Ordinal))
                    .Select(r => RouteOf(r.Header, Text(lines, r.Start + 1, r.End)))],
                Text(lines, b.Start + 1, b.End)))]);
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