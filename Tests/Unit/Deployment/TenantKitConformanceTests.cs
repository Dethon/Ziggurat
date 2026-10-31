using System.Text.RegularExpressions;
using Domain.DTOs.Tunnel;
using Shouldly;

namespace Tests.Unit.Deployment;

// What the tenant kit promises, checked where it ships: a household's servers reach the deployment
// through their tunnels and nothing else, each holds the credential minted for it and no secret of
// the deployment's, and the kit's own state stays in the kit's own store.
public class TenantKitConformanceTests
{
    // Where every tunnel opens: the deployment's main host, from the kit's .env, on the proxy's route.
    private const string Hub = "wss://${DEPLOYMENT_HOST:?set DEPLOYMENT_HOST in .env}" + TunnelProtocol.Path;

    // What a server may name besides the kit's own services: its own loopback (its back-calls), the
    // host's gateway (Music Assistant, on the host's network) and the deployment's main host itself,
    // which only the hub and a handoff link's address name.
    private static readonly string[] _reachableBeyondTheKit = ["127.0.0.1", "localhost", "host.docker.internal", "${DEPLOYMENT_HOST"];

    private static ComposeFile Kit => DeploymentFiles.KitCompose();

    private static IReadOnlyList<ComposeService> TunnelledServers =>
        [.. Kit.Services.Values.Where(s => s.Environment.ContainsKey("MCP__TUNNEL__SERVERNAME"))];

    [Fact]
    public void TheKit_ReadsNothingButItsOwnPlaceholders()
    {
        var placeholders = DeploymentFiles.KitPlaceholders();

        DeploymentFiles.Variables(DeploymentFiles.KitDirectory)
            .Where(variable => !placeholders.ContainsKey(variable))
            .ShouldBeEmpty("a variable the kit's .env does not declare is read from whatever shell runs it");
    }

    [Fact]
    public void EveryKitPlaceholder_IsUsedByTheKit()
    {
        var read = DeploymentFiles.Variables(DeploymentFiles.KitDirectory);

        DeploymentFiles.KitPlaceholders().Keys
            .Where(key => !read.Contains(key))
            .ShouldBeEmpty("a placeholder nothing reads is a secret nobody needs");
    }

    [Fact]
    public void NoKitPlaceholder_ShipsAValue()
    {
        DeploymentFiles.KitPlaceholders()
            .Where(p => !p.Key.EndsWith("_PATH", StringComparison.Ordinal) && p.Value.Length > 0)
            .Select(p => p.Key)
            .ShouldBeEmpty("a value in git is a value everyone can read");
    }

    [Fact]
    public void NoKitService_TakesTheWholeSecretsFile()
    {
        Kit.Services.Values.Where(s => s.TakesWholeSecretsFile).Select(s => s.Name).ShouldBeEmpty();
    }

    // A tunnelled server's /mcp asks for its tunnel credential and checks call tokens against the keys
    // its tunnel delivers, so it is told none of a dialled server's gate: no shared secret, no audience,
    // no address of the agent's keys — and nothing at all of the accounts service's callers.
    [Fact]
    public void NoKitService_IsToldADeploymentGatesSettings()
    {
        string[] gates = ["MCP__SHAREDSECRET", "MCP__SECRETS__", "MCP__AUDIENCE", "MCP__AGENTKEYSURL", "CALLERS__", "AGENTAPI__"];

        Kit.Services.Values
            .SelectMany(s => s.Environment.Keys.Select(key => (s.Name, Key: key)))
            .Where(e => gates.Any(gate => e.Key.StartsWith(gate, StringComparison.OrdinalIgnoreCase)))
            .Select(e => $"{e.Name} {e.Key}")
            .ShouldBeEmpty();
    }

    [Fact]
    public void EveryKitServer_OpensItsTunnelToTheDeploymentsHub()
    {
        TunnelledServers.ShouldNotBeEmpty();
        TunnelledServers.ShouldAllBe(s => s.Environment.GetValueOrDefault("MCP__TUNNEL__HUB") == Hub);
    }

    // Holding one server's credential must open no other's tunnel, and each is minted for the one name
    // its server claims.
    [Fact]
    public void EveryKitServer_HoldsACredentialOfItsOwn_ForANameOfItsOwn()
    {
        var servers = TunnelledServers;
        var credentials = servers.Select(s => s.Environment.GetValueOrDefault("MCP__TUNNEL__CREDENTIAL")).ToList();
        var names = servers.Select(s => s.Environment["MCP__TUNNEL__SERVERNAME"]).ToList();

        credentials.ShouldAllBe(c => c != null && Regex.IsMatch(c, @"^\$\{TUNNEL_CREDENTIAL_[A-Z]+\}$"));
        credentials.Distinct().Count().ShouldBe(servers.Count, "holding one server's credential must open no other");
        names.ShouldAllBe(n => TunnelCredentialProtocol.IsServerName(n));
        names.Distinct().Count().ShouldBe(servers.Count);
    }

    // The deployment looks two of a household's servers up by name — the browser a browse page is
    // forwarded to, the voice server a timer rings — so those two run under exactly those names.
    [Theory]
    [InlineData(BrowseServerListeners.Service, HouseholdServerNames.WebSearch)]
    [InlineData("mcp-channel-voice", HouseholdServerNames.Voice)]
    public void TheServersTheDeploymentFindsByName_RunUnderTheNamesItLooksFor(string service, string name)
    {
        Kit.Service(service).Environment["MCP__TUNNEL__SERVERNAME"].ShouldBe(name);
    }

    // The kit is on networks of its own, and every address a service is told is one of the kit's own
    // services, its own loopback or the deployment's main host: the deployment's agent, accounts
    // service, Lemonade and Redis are reached through a tunnel's back-calls, never by name.
    [Fact]
    public void NoKitService_ReachesTheDeploymentButThroughItsTunnel()
    {
        var kit = Kit;

        kit.Networks.Where(n => n.Value).Select(n => n.Key).ShouldBeEmpty("an external network is another project's");
        kit.Services.Values
            .SelectMany(s => s.Networks.Where(n => n != "default" && !kit.Networks.ContainsKey(n)).Select(n => $"{s.Name} {n}"))
            .ShouldBeEmpty();
        kit.Services.Values
            .SelectMany(s => s.Environment.Values.SelectMany(HostsIn).Select(host => (s.Name, Host: host)))
            .Where(r => !kit.Services.ContainsKey(r.Host)
                        && !_reachableBeyondTheKit.Any(beyond => r.Host.StartsWith(beyond, StringComparison.Ordinal)))
            .Select(r => $"{r.Name} names {r.Host}")
            .ShouldBeEmpty();
    }

    // A back-call is a loopback port of the server's own that its tunnel carries to one deployment
    // service, so the client that needs that service is pointed at that port and nowhere else.
    [Theory]
    [InlineData("mcp-channel-voice", "ACCOUNTS__BASEURL", BackCallProtocol.Accounts, "")]
    [InlineData("mcp-channel-voice", "AGENT__BASEURL", BackCallProtocol.Topics, "")]
    [InlineData("mcp-channel-voice", "STT__OPENAI__BASEURL", BackCallProtocol.Lemonade, "/v1")]
    [InlineData("mcp-channel-voice", "TTS__OPENAI__BASEURL", BackCallProtocol.Lemonade, "/v1")]
    [InlineData("mcp-channel-voice", "METRICS__URL", BackCallProtocol.Metrics, BackCallProtocol.MetricsPath)]
    [InlineData(BrowseServerListeners.Service, "METRICS__URL", BackCallProtocol.Metrics, BackCallProtocol.MetricsPath)]
    [InlineData("mcp-sandbox", "VFSBRIDGEURL", BackCallProtocol.VfsBridge, "/api/vfs-bridge")]
    public void AClientOfADeploymentService_DialsItsServersBackCallPort(string service, string client, string tag, string path)
    {
        var environment = Setting.Of(Kit.Service(service));

        environment.ShouldContainKey($"MCP__TUNNEL__BACKCALLS__{tag}".ToUpperInvariant());
        environment.GetValueOrDefault(client)
            .ShouldBe($"http://127.0.0.1:{environment[$"MCP__TUNNEL__BACKCALLS__{tag}".ToUpperInvariant()]}{path}");
    }

    // Anything running beside a server can dial its back-call ports, a sandbox command included, so a
    // server names only the back-calls it makes, each a tag the relay knows, and every one is used.
    [Fact]
    public void EveryBackCall_IsOneTheRelayKnows_AndOneItsServerMakes()
    {
        string[] tags = [BackCallProtocol.Accounts, BackCallProtocol.Topics, BackCallProtocol.Lemonade,
            BackCallProtocol.Metrics, BackCallProtocol.VfsBridge];

        foreach (var server in TunnelledServers)
        {
            var backCalls = server.Environment
                .Where(e => e.Key.StartsWith("MCP__TUNNEL__BACKCALLS__", StringComparison.Ordinal))
                .ToDictionary(e => e.Key["MCP__TUNNEL__BACKCALLS__".Length..], e => e.Value);
            var dialled = server.Environment.Values
                .Select(v => Regex.Match(v, @"^http://127\.0\.0\.1:(\d+)"))
                .Where(m => m.Success)
                .Select(m => m.Groups[1].Value)
                .ToHashSet();

            backCalls.Keys.ShouldAllBe(tag => tags.Contains(tag), server.Name);
            backCalls.Values.ShouldAllBe(port => dialled.Contains(port), $"{server.Name} names a back-call nothing dials");
            dialled.ShouldAllBe(port => backCalls.ContainsValue(port), $"{server.Name} dials a loopback port that is no back-call");
        }
    }

    // The sandbox runs whatever the model writes: it shares a network with no other service of the
    // kit, so a command reaches neither Home Assistant nor the kit's Redis nor another server.
    [Fact]
    public void TheSandbox_SharesANetworkWithNoOtherKitService()
    {
        var kit = Kit;
        var sandbox = kit.Service("mcp-sandbox").Networks;

        sandbox.ShouldNotBeEmpty();
        kit.Services.Values
            .Where(s => s.Name != "mcp-sandbox" && s.Networks.Intersect(sandbox).Any())
            .Select(s => s.Name)
            .ShouldBeEmpty();
    }

    // It is handed its own tunnel credential and what it runs as, and no other secret of the kit's.
    [Fact]
    public void TheSandbox_IsHandedNoSecretButItsTunnelCredential()
    {
        var read = Kit.Service("mcp-sandbox").Environment.Values
            .SelectMany(v => Regex.Matches(v, @"\$\{([A-Za-z_][A-Za-z0-9_]*)").Select(m => m.Groups[1].Value))
            .ToHashSet();

        read.ShouldBe(["DEPLOYMENT_HOST", "TUNNEL_CREDENTIAL_SANDBOX", "PUID", "PGID"], ignoreOrder: true);
    }

    [Fact]
    public void TheKitsRedis_RequiresItsPassword_AndEveryServiceOnItIsHandedIt()
    {
        var kit = Kit;
        var redis = kit.Service("redis");

        redis.Environment["REDIS_ARGS"].ShouldContain("--requirepass ${KIT_REDIS_PASSWORD:?");
        redis.Environment["REDISCLI_AUTH"].ShouldBe("${KIT_REDIS_PASSWORD}");
        kit.Services.Values
            .Where(s => s.DependsOn.Contains("redis"))
            .Where(s => !s.Environment.Values.Any(v => v.Contains("password=${KIT_REDIS_PASSWORD}", StringComparison.Ordinal)))
            .Select(s => s.Name)
            .ShouldBeEmpty();
    }

    [Theory]
    [InlineData("LOGINS__STATEKEY", new[] { "mcp-websearch" })]
    [InlineData("KIT_REDIS_PASSWORD", new[] { "redis", "mcp-websearch", "mcp-library", "state-move" })]
    [InlineData("Announce__Token", new[] { "mcp-homeassistant", "mcp-channel-voice" })]
    [InlineData("HOMEASSISTANT__TOKEN", new[] { "mcp-homeassistant" })]
    [InlineData("OPENROUTER__APIKEY", new[] { "mcp-websearch", "mcp-channel-voice" })]
    [InlineData("TUNNEL_CREDENTIAL_SANDBOX", new[] { "mcp-sandbox" })]
    [InlineData("TUNNEL_CREDENTIAL_VOICE", new[] { "mcp-channel-voice" })]
    public void AKitSecret_ReachesOnlyTheServicesThatReadIt(string variable, string[] readers)
    {
        Kit.Services.Values
            .Where(s => s.Environment.Values.Any(v => v.Contains($"${{{variable}", StringComparison.Ordinal)))
            .Select(s => s.Name)
            .ShouldBe(readers, ignoreOrder: true);
    }

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("homeassistant")]
    [InlineData("jackett")]
    public void TheHouseholdsLanTools_ListenOnTheNamedLanAddress(string service)
    {
        var ports = Kit.Service(service).Ports;

        ports.ShouldNotBeEmpty();
        ports.ShouldAllBe(p => p.BindAddress == "${LAN_BIND_ADDRESS:-127.0.0.1}");
    }

    // A household's servers need no open port: nothing is published but its own LAN tools and its
    // Redis on loopback, which the state move and the operator reach.
    [Fact]
    public void OnlyTheLanToolsAndTheKitsRedis_ArePublished()
    {
        var kit = Kit;

        kit.Services.Values.Where(s => s.Ports.Count > 0).Select(s => s.Name)
            .ShouldBe(["redis", "qbittorrent", "homeassistant", "jackett"], ignoreOrder: true);
        kit.Service("redis").Ports.ShouldAllBe(p => p.BindAddress == "127.0.0.1");
    }

    [Fact]
    public void TheKitsPortsOverride_PublishesOnLoopbackOnly()
    {
        var ports = DeploymentFiles.KitCompose("docker-compose.override.ports.yml").Services.Values
            .SelectMany(s => s.Ports)
            .ToList();

        ports.ShouldNotBeEmpty();
        ports.ShouldAllBe(p => p.BindAddress == "127.0.0.1");
    }

    // The home's state is carried over once, by hand: under a profile, so no `up` starts it, and told
    // the kit's Redis alone — the deployment's is given on the run's own command line.
    [Fact]
    public void TheStateMove_RunsOnlyWhenAskedFor_AndHoldsNoDeploymentAddress()
    {
        var move = Kit.Service("state-move");

        move.Profiles.ShouldBe(["move"]);
        move.Ports.ShouldBeEmpty();
        move.Environment.Keys.ShouldBe(["HOMESTATE__TO"]);
        move.Environment["HOMESTATE__TO"].ShouldStartWith("127.0.0.1:");
    }

    // The same images the deployment builds, from the repository's own Dockerfiles.
    [Fact]
    public void EveryImageTheKitBuilds_IsBuiltFromAFileInTheRepository()
    {
        var repository = DeploymentFiles.KitPlaceholders()["REPOSITORY_PATH"];
        var built = Kit.Services.Values.Where(s => s.BuildContext is not null).ToList();

        built.ShouldNotBeEmpty();
        built.Select(s => Path.Combine(DeploymentFiles.KitDirectory, s.BuildContext!.Replace("${REPOSITORY_PATH}", repository), s.Dockerfile!))
            .Where(file => !File.Exists(file))
            .ShouldBeEmpty();
    }

    // What hosts a setting's value names: a URL's, or a `host:port` connection string's.
    private static IEnumerable<string> HostsIn(string value) =>
        Regex.Matches(value, @"[a-z]+://([^/:,]+)").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(value, @"^([A-Za-z0-9.-]+):\d+").Select(m => m.Groups[1].Value));

    // A service's environment by the configuration key it binds, which ignores case.
    private static class Setting
    {
        public static IReadOnlyDictionary<string, string> Of(ComposeService service) =>
            service.Environment.ToDictionary(e => e.Key.ToUpperInvariant(), e => e.Value);
    }
}