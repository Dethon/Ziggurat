using System.Globalization;
using System.Text.RegularExpressions;
using Accounts.Internal;
using Accounts.Settings;
using Agent.App;
using Agent.Modules;
using Domain.DTOs.Admin;
using Domain.DTOs.Channel;
using Domain.DTOs.Tunnel;
using Domain.Security;
using Infrastructure.Tunnels;
using McpServerSugary.Channel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Tests.E2E.Fixtures;

namespace Tests.Unit.Deployment;

// What the deployment files promise about the network and its secrets, checked where the operator
// ships them: a service handed more than it reads, or published past the proxy, fails here before it
// reaches a host.
public class DeploymentConformanceTests
{
    private const string MainHost = "assistants.herfluffness.com";
    private const string SexyHost = "sexy.herfluffness.com";
    private const string SugaryHost = "sugary.herfluffness.com";
    private const string DashboardHost = "assistants-dashboard.herfluffness.com";

    // A local stack swaps the issuer by CADDY_TLS; the deployment never sets it, so every host must
    // fall back to the Cloudflare challenge rather than to the local CA a phone would not trust.
    [Fact]
    public void EveryHost_TakesTheDeploymentsCertificateUnlessToldOtherwise()
    {
        var hosts = DeploymentFiles.Caddyfile().Hosts.Where(h => !h.Name.StartsWith('(')).ToList();

        hosts.ShouldNotBeEmpty();
        hosts.ShouldAllBe(h => h.Body.Contains("import tls_{$CADDY_TLS:cloudflare}"));
        DeploymentFiles.Compose().Service("caddy").Environment.ShouldNotContainKey("CADDY_TLS");
    }

    // A deploy copies the Caddyfile in by writing a new file and renaming it over the old one (rsync, git),
    // and a bind mount of the file itself keeps the old one, so `caddy reload` re-read stale routes on
    // prod for eleven days. A mount of its folder sees the rename.
    [Fact]
    public void Caddy_MountsItsConfigurationFolder_NotTheFile()
    {
        var volumes = DeploymentFiles.Compose().Service("caddy").Volumes;

        volumes.ShouldContain("./caddy:/etc/caddy:ro");
        volumes.ShouldNotContain(v => v.Contains("/etc/caddy/Caddyfile", StringComparison.Ordinal));
    }

    // Every section's words are a markdown document embedded into Domain, and the build context
    // drops markdown: a context that lost them builds images whose agents have no prompt.
    [Fact]
    public void ThePromptDocuments_ReachEveryImagesBuildContext()
    {
        var root = TestHelpers.FindSolutionRoot();
        var ignore = DeploymentFiles.DockerIgnore();
        var documents = Directory
            .EnumerateFiles(Path.Combine(root, "Domain", "Prompts"), "*.md", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root, file).Replace('\\', '/'))
            .ToList();

        documents.ShouldNotBeEmpty();
        documents.ShouldAllBe(document => !ignore.Excludes(document));
    }

    // A household's server opens its tunnel through the proxy on the main host, to the relay in the
    // agent, presenting a credential of its own: the one new door, and only that path.
    [Fact]
    public void TheTunnel_ReachesTheRelayOnTheMainHostAlone()
    {
        var hosts = DeploymentFiles.Caddyfile().Hosts.Where(h => !h.Name.StartsWith('(')).ToList();

        var route = hosts.Single(h => h.Name == MainHost).Routes.Single(r => r.Matcher == TunnelProtocol.Path);
        route.Upstream.ShouldBe("agent:8080");
        route.ForwardAuth.ShouldBeNull();
        foreach (var host in hosts.Where(h => h.Name != MainHost))
        {
            RouteFor(host, TunnelProtocol.Path)?.Upstream.ShouldNotBe("agent:8080", host.Name);
        }
    }

    // The route names the tunnel's path exactly, so nothing under it reaches the agent by it.
    [Fact]
    public void NothingUnderTheTunnelsPath_ReachesTheAgent()
    {
        var host = DeploymentFiles.Caddyfile().Host(MainHost);

        RouteFor(host, $"{TunnelProtocol.Path}/anything")?.Upstream.ShouldNotBe("agent:8080");
        RouteFor(host, $"{TunnelProtocol.Path}s")?.Upstream.ShouldNotBe("agent:8080");
    }

    // An outpost opens its tunnel like any household server; the registration and keepalive routes
    // it used to announce itself on are gone, and nothing else may answer for them at the agent.
    [Fact]
    public void NoOutpostRegistrationRoute_ReachesTheAgent()
    {
        var host = DeploymentFiles.Caddyfile().Host(MainHost);

        RouteFor(host, "/api/outposts")?.Upstream.ShouldNotBe("agent:8080");
        RouteFor(host, "/api/outposts/laptop")?.Upstream.ShouldNotBe("agent:8080");
    }

    // One leaked value no longer registers outposts for every household: an outpost holds a tunnel
    // credential of its own, so the deployment holds no outpost secret at all.
    [Fact]
    public void TheOutpostsSharedSecret_IsGoneFromTheDeployment()
    {
        DeploymentFiles.Placeholders().Keys.ShouldNotContain("OUTPOSTS__SHAREDSECRET");
        DeploymentFiles.Compose().Services.Values
            .Where(s => s.Environment.Keys.Any(k => k.StartsWith("OUTPOSTS__", StringComparison.OrdinalIgnoreCase))
                        || s.Environment.Values.Any(v => v.Contains("OUTPOSTS__", StringComparison.Ordinal)))
            .Select(s => s.Name)
            .ShouldBeEmpty();
    }

    [Fact]
    public void EveryPublishedPort_NamesItsBindAddress_ButTheProxys()
    {
        var unbound = DeploymentFiles.Compose().Services.Values
            .SelectMany(s => s.Ports.Select(p => (s.Name, p)))
            .Where(x => x.p.BindAddress is null && !(x.Name == "caddy" && x.p.Container == "443"))
            .Select(x => $"{x.Name} {x.p.Raw}")
            .ToList();

        unbound.ShouldBeEmpty("only the proxy listens on every interface");
    }

    [Fact]
    public void Redis_IsPublishedOnLoopbackOnly()
    {
        DeploymentFiles.Compose().Service("redis").Ports
            .ShouldAllBe(p => p.BindAddress == "127.0.0.1");
    }

    // A household's own LAN tools — qBittorrent, Jackett, Home Assistant, Plex, filebrowser — moved
    // into the tenant kit with its servers, and are held to the same rule there
    // (TenantKitConformanceTests). Lemonade is the shared box's, which the speech typist posts at.
    [Fact]
    public void Lemonade_ListensOnTheNamedLanAddress()
    {
        var ports = DeploymentFiles.Compose().Service("lemonade").Ports;

        ports.ShouldNotBeEmpty();
        ports.ShouldAllBe(p => p.BindAddress == "${LAN_BIND_ADDRESS:-127.0.0.1}");
    }

    [Fact]
    public void OnlyTheProxyRedisAndLemonade_ArePublishedAtAll()
    {
        string[] published = ["caddy", "redis", "lemonade"];

        DeploymentFiles.Compose().Services.Values
            .Where(s => s.Ports.Count > 0)
            .Select(s => s.Name)
            .ShouldBe(published, ignoreOrder: true);
    }

    [Fact]
    public void Redis_RequiresThePassword_AndItsHealthCheckAuthenticates()
    {
        var redis = DeploymentFiles.Compose().Service("redis");

        redis.Environment["REDIS_ARGS"].ShouldContain("--requirepass ${REDIS_PASSWORD:?");
        redis.Environment.ShouldContainKey("REDISCLI_AUTH", "redis-cli in the health check reads it");
        redis.Environment["REDISCLI_AUTH"].ShouldStartWith("${REDIS_PASSWORD");
    }

    [Fact]
    public void EveryServiceOnRedis_IsHandedAConnectionStringWithThePassword()
    {
        var missing = DeploymentFiles.Compose().Services.Values
            .Where(s => s.DependsOn.Contains("redis"))
            .Where(s => !s.Environment.Any(e =>
                e.Key.Replace("__", "").Equals("REDISCONNECTIONSTRING", StringComparison.OrdinalIgnoreCase)
                && e.Value.Contains("password=${REDIS_PASSWORD", StringComparison.Ordinal)))
            .Select(s => s.Name)
            .ToList();

        missing.ShouldBeEmpty();
    }

    [Fact]
    public void NoService_TakesTheWholeSecretsFile()
    {
        DeploymentFiles.Compose().Services.Values
            .Where(s => s.TakesWholeSecretsFile)
            .Select(s => s.Name)
            .ShouldBeEmpty("each service names the variables it reads");
    }

    [Theory]
    [InlineData("OPENROUTER__APIKEY", new[] { "agent" })]
    [InlineData("LOGINS__STATEKEY", new[] { "household-migration" })]
    [InlineData("HOUSEHOLD_MIGRATION_SECRET", new[] { "accounts", "household-migration" })]
    [InlineData("TELEGRAM_SHARED_BOT_TOKEN", new[] { "mcp-channel-telegram" })]
    [InlineData("WEBPUSH__PRIVATEKEY", new[] { "mcp-channel-signalr", "mcp-sugary" })]
    [InlineData("SUGARY_SEALING_KEY", new[] { "mcp-sugary" })]
    [InlineData("AGENTAPI__SHAREDSECRET", new[] { "agent", "accounts" })]
    [InlineData("HOUSEHOLD_CONFIG_MASTER_KEY", new[] { "accounts" })]
    public void ASecret_ReachesOnlyTheServicesThatReadIt(string variable, string[] readers)
    {
        DeploymentFiles.Compose().Services.Values
            .Where(s => s.TakesWholeSecretsFile
                        || s.Environment.Values.Any(v => v.Contains($"${{{variable}}}", StringComparison.Ordinal)))
            .Select(s => s.Name)
            .ShouldBe(readers, ignoreOrder: true);
    }

    [Fact]
    public void EveryPlaceholder_IsUsedByTheDeployment()
    {
        var compose = File.ReadAllText(Path.Combine(DeploymentFiles.Directory, "docker-compose.yml"));

        DeploymentFiles.Placeholders().Keys
            .Where(key => !compose.Contains($"${{{key}", StringComparison.Ordinal))
            .ShouldBeEmpty("a placeholder nothing reads is a secret nobody needs");
    }

    [Fact]
    public void NoSecretPlaceholder_ShipsAValue()
    {
        DeploymentFiles.Placeholders()
            .Where(p => !p.Key.EndsWith("_PATH", StringComparison.Ordinal) && p.Value.Length > 0)
            .Select(p => p.Key)
            .ShouldBeEmpty("a value in git is a value everyone can read");
    }

    [Theory]
    [InlineData("docker-compose.override.linux.yml")]
    [InlineData("docker-compose.override.windows.yml")]
    public void TheOsOverrides_MountEachProjectsOwnUserSecrets(string overrideFile)
    {
        var mounts = DeploymentFiles.Compose(overrideFile).Services.Values
            .SelectMany(s => s.Volumes.Where(v => v.Contains("usersecrets", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        mounts.ShouldNotBeEmpty();
        mounts.ShouldAllBe(v => System.Text.RegularExpressions.Regex.IsMatch(
            v, @"[Uu]ser[Ss]ecrets/[0-9a-f-]{36}:/home/app/\.microsoft/usersecrets/[0-9a-f-]{36}:ro$"));
    }

    [Fact]
    public void EveryMcpServer_IsFedACredentialOfItsOwn()
    {
        var credentials = DeploymentFiles.Compose().Services.Values
            .Where(s => s.Environment.ContainsKey("MCP__SHAREDSECRET"))
            .Select(s => (s.Name, Variable: s.Environment["MCP__SHAREDSECRET"]))
            .ToList();

        credentials.Count.ShouldBeGreaterThan(4);
        credentials.GroupBy(c => c.Variable)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(c => c.Name))}")
            .ShouldBeEmpty("holding one server's credential must open no other");
    }

    // The sandbox runs whatever the model writes. The servers behind the proxy believe the identity
    // headers it copies on because only the proxy reaches them, so the sandbox must not reach them:
    // it is a household's server now, in the tenant kit on a network nobody else is on
    // (TenantKitConformanceTests), and reaches this deployment through its tunnel alone. So nothing
    // here runs the model's commands, the agent is on no network but the deployment's own, and no
    // service holds the sandbox's old credential.
    [Fact]
    public void NothingInTheDeployment_RunsTheModelsCommands()
    {
        var compose = DeploymentFiles.Compose();

        compose.Services.Values
            .Where(s => s.Image == "mcp-sandbox:latest" || s.Dockerfile == "McpServerSandbox/Dockerfile")
            .Select(s => s.Name)
            .ShouldBeEmpty();
        compose.Networks.Keys.ShouldBe(["jackbot", "apps-browser"]);
        compose.Service("agent").Networks.ShouldBe(["jackbot"]);
        DeploymentFiles.Placeholders().Keys.ShouldNotContain("MCP_SECRET_SANDBOX");
    }

    // A household's servers are reached through their tunnels: the deployment runs none of them, nor
    // what stood beside them, and holds none of their credentials — the agent dials none by host,
    // and the accounts service admits none by a deployment secret.
    [Theory]
    [InlineData("mcp-websearch", new[] { "camoufox" })]
    [InlineData("mcp-vault", new string[0])]
    [InlineData("mcp-library", new[] { "qbittorrent", "jackett", "plex", "filebrowser" })]
    [InlineData("mcp-sandbox", new string[0])]
    [InlineData("mcp-printer", new string[0])]
    [InlineData("mcp-homeassistant", new[] { "homeassistant", "music-assistant" })]
    [InlineData("mcp-channel-voice", new[] { "tse-extractor" })]
    public void AHouseholdsOwnServer_IsNoServiceOfTheDeployment(string server, string[] beside)
    {
        var compose = DeploymentFiles.Compose();
        var agent = compose.Service("agent").Environment;

        compose.Services.Keys.ShouldNotContain(server);
        compose.Services.Keys.Intersect(beside).ShouldBeEmpty();
        agent.Keys.ShouldNotContain($"MCP__SECRETS__{server}");
        compose.Service("accounts").Environment.Keys.ShouldNotContain($"CALLERS__{server}");
        compose.Services.Values.SelectMany(s => s.DependsOn).ShouldNotContain(server);
    }

    // What the moved servers read leaves with them: their credentials, the third-party keys only they
    // used, the announce secret Home Assistant and voice shared, and the paths of their mounts.
    [Theory]
    [InlineData("MCP_SECRET_LIBRARY")]
    [InlineData("MCP_SECRET_VAULT")]
    [InlineData("MCP_SECRET_WEBSEARCH")]
    [InlineData("MCP_SECRET_HOMEASSISTANT")]
    [InlineData("MCP_SECRET_PRINTER")]
    [InlineData("MCP_SECRET_CHANNEL_VOICE")]
    [InlineData("HOMEASSISTANT__TOKEN")]
    [InlineData("MUSICASSISTANT__TOKEN")]
    [InlineData("JACKETT__APIKEY")]
    [InlineData("QBITTORRENT__PASSWORD")]
    [InlineData("BRAVESEARCH__APIKEY")]
    [InlineData("CAPSOLVER__APIKEY")]
    [InlineData("Announce__Token")]
    [InlineData("VAULT_PATH")]
    [InlineData("SANDBOX_HOME_PATH")]
    [InlineData("DATA_PATH")]
    public void AMovedServersSetting_IsNoLongerTheDeployments(string variable)
    {
        DeploymentFiles.Placeholders().Keys.ShouldNotContain(variable);
        DeploymentFiles.Variables(DeploymentFiles.Directory).ShouldNotContain(variable);
    }

    // The home's media and the host's devices are the household's: no deployment service mounts the
    // media, and only the shared Lemonade is handed a device — its render node, for whisper on the GPU.
    [Fact]
    public void TheDeployment_MountsNoMedia_AndHandsADeviceToLemonadeAlone()
    {
        var services = DeploymentFiles.Compose().Services.Values;

        services.Where(s => s.Volumes.Any(v => v.Contains("${DATA_PATH", StringComparison.Ordinal)))
            .Select(s => s.Name)
            .ShouldBeEmpty();
        services.Where(s => s.Devices.Count > 0).Select(s => s.Name).ShouldBe(["lemonade"]);
        DeploymentFiles.Compose("docker-compose.override.no-dri.yml").Services.Keys.ShouldBe(["lemonade"]);
    }

    // The agent presents each server the credential the server reads, so the two must name the
    // same variable — and every server the shipped agent dials must have one.
    [Fact]
    public void TheAgent_HoldsEachDialledServersOwnCredential()
    {
        var compose = DeploymentFiles.Compose();
        var agent = compose.Service("agent").Environment;

        var mismatched = ShippedAgentSettings.DialledHosts()
            .Where(host => !agent.TryGetValue($"MCP__SECRETS__{host}", out var presented)
                           || !compose.Services.TryGetValue(host, out var server)
                           || server.Environment.GetValueOrDefault("MCP__SHAREDSECRET") != presented)
            .ToList();

        mismatched.ShouldBeEmpty();
    }

    // The agent refuses to start without a credential for a server it dials. With the home's servers
    // gone from this file, the shipped definitions name them by their tunnels' names, which need
    // none: the agent as compose configures it — its shipped settings under the environment compose
    // hands it — starts with the shared servers' credentials alone.
    [Fact]
    public void TheShippedAgent_StartsWithTheCredentialsTheDeploymentHandsIt()
    {
        var environment = DeploymentFiles.Compose().Service("agent").Environment
            .ToDictionary(
                e => e.Key.Replace("__", ":", StringComparison.Ordinal),
                e => (string?)System.Text.RegularExpressions.Regex.Replace(e.Value, @"\$\{[^}]*\}", "local"));
        var settings = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(Tests.E2E.Fixtures.TestHelpers.FindSolutionRoot(), "Agent", "appsettings.json"))
            .AddInMemoryCollection(environment)
            .Build()
            .Get<global::Agent.Settings.AgentSettings>()!;

        Should.NotThrow(() => new ServiceCollection().AddLogging().AddAgent(settings));
    }

    // Sign-in, enrollment and the session check live under /accounts on the main host; Caddy's
    // handle passes the path as it is, which is the path the service maps its routes under.
    // SexyTime's and Sugary's hosts route them too, so an app there asks /accounts/me, and sends a
    // signed-out person to sign in, same-origin.
    [Theory]
    [InlineData(MainHost)]
    [InlineData(SexyHost)]
    [InlineData(SugaryHost)]
    public void TheAccountsPages_ReachTheAccountsService(string hostName)
    {
        var host = DeploymentFiles.Caddyfile().Host(hostName);

        host.Routes.ShouldContain(r => r.Matcher == "/accounts/*" && r.Upstream == "accounts:8080");
    }

    // Who holds which identity is asked server to server and never by a browser: every host that
    // reaches the accounts service answers its internal API 404 first, before the accounts route
    // could hand the path on.
    [Fact]
    public void TheAccountsInternalApi_IsAnswered404OnEveryHostThatReachesTheService()
    {
        var hosts = DeploymentFiles.Caddyfile().Hosts
            .Where(h => h.Routes.Any(r => r.Upstream == "accounts:8080"))
            .ToList();

        hosts.ShouldNotBeEmpty();
        hosts.ShouldAllBe(h => h.Routes.Any(r => r.Matcher == "/accounts/internal/*"), "a host routes the internal API");
        foreach (var host in hosts)
        {
            var routes = host.Routes.ToList();
            var refusal = routes.FindIndex(r => r.Matcher == "/accounts/internal/*");
            routes[refusal].Upstream.ShouldBeNull();
            routes[refusal].Body.ShouldContain("respond 404");
            refusal.ShouldBeLessThan(routes.FindIndex(r => r.Upstream == "accounts:8080"), host.Name);
        }
    }

    // The session check answers whoever holds the cookie with the four headers, a signed assertion among
    // them, for any channel it is asked about — the admin API's included. forward_auth asks it on the
    // compose network, never through a host, so every host that routes the accounts service's paths
    // refuses it before that route could hand it on, and no host's route for it is the service: a
    // same-origin script cannot read an assertion out of its answer.
    [Fact]
    public void TheSessionCheck_IsAnswered404OnEveryHostThatRoutesTheAccountsService()
    {
        var hosts = DeploymentFiles.Caddyfile().Hosts
            .Where(h => h.Routes.Any(r => r.Matcher == "/accounts/*" && r.Upstream == "accounts:8080"))
            .ToList();

        hosts.ShouldNotBeEmpty();
        foreach (var host in hosts)
        {
            var routes = host.Routes.ToList();
            var refusal = routes.FindIndex(r => r.Matcher == "/accounts/verify*");
            refusal.ShouldBeGreaterThanOrEqualTo(0, $"{host.Name} refuses the session check");
            routes[refusal].Upstream.ShouldBeNull();
            routes[refusal].Body.ShouldContain("respond 404");
            refusal.ShouldBeLessThan(routes.FindIndex(r => r.Upstream == "accounts:8080"), host.Name);
            foreach (var path in new[] { "/accounts/verify", $"/accounts/verify?channel={AppChannels.Admin}" })
            {
                RouteFor(host, path)!.Matcher.ShouldBe("/accounts/verify*", $"{host.Name} {path}");
            }
        }

        DeploymentFiles.Caddyfile().Hosts.Where(h => !h.Name.StartsWith('('))
            .ShouldAllBe(h => RouteFor(h, "/accounts/verify")!.Upstream != "accounts:8080");
    }

    // The keys assertions verify with are asked for without a credential, so it is the proxy alone
    // that keeps them inside: on every host the route the path lands on is a refusal or an upstream
    // that is not the accounts service, never the accounts route.
    [Fact]
    public void TheAccountsJwks_IsRefusedThroughTheProxyOnEveryHost() =>
        ShouldBeRefusedThroughTheProxyOnEveryHost(InternalApi.JwksPath);

    // A tunnel's credential is verified by the relay alone; from outside, the check would be an
    // oracle for whether a credential guessed or stolen still works.
    [Fact]
    public void TheTunnelCredentialCheck_IsRefusedThroughTheProxyOnEveryHost() =>
        ShouldBeRefusedThroughTheProxyOnEveryHost(TunnelCredentialProtocol.VerifyPath);

    private static void ShouldBeRefusedThroughTheProxyOnEveryHost(string path)
    {
        var hosts = DeploymentFiles.Caddyfile().Hosts.Where(h => !h.Name.StartsWith('(')).ToList();

        hosts.ShouldNotBeEmpty();
        foreach (var host in hosts)
        {
            var route = RouteFor(host, path);
            route.ShouldNotBeNull(host.Name);
            route.Upstream.ShouldNotBe("accounts:8080", host.Name);
            if (host.Routes.Any(r => r.Upstream == "accounts:8080"))
            {
                route.Matcher.ShouldBe("/accounts/internal/*", host.Name);
                route.Body.ShouldContain("respond 404", customMessage: host.Name);
            }
        }
    }

    // Every app behind the session check believes the person the signed assertion names, so each is
    // told where the accounts service publishes the keys it verifies with — on the compose network,
    // where the proxy's 404 above does not reach — and the default those apps would otherwise fall
    // back on is the same address.
    [Theory]
    [InlineData("mcp-channel-signalr")]
    [InlineData("mcp-sugary")]
    [InlineData("observability")]
    [InlineData("agent")]
    [InlineData("mcp-apps")]
    public void TheAppsThatBelieveTheAssertion_FetchTheAccountsServicesKeys(string service)
    {
        var expected = $"http://accounts:8080{InternalApi.JwksPath}";

        DeploymentFiles.Compose().Service(service).Environment.GetValueOrDefault("ACCOUNTS__JWKSURL")
            .ShouldBe(expected, service);
        AssertionVerifier.DefaultJwksUrl.ShouldBe(expected);
    }

    // The handle a request path lands on, as Caddy sorts handle blocks: the most specific path
    // matcher that matches it, else the catch-all.
    private static CaddyRoute? RouteFor(CaddyHost host, string path) =>
        host.Routes
            .Where(r => r.Matcher is { } m && (m.EndsWith('*') ? path.StartsWith(m.TrimEnd('*'), StringComparison.Ordinal) : path == m))
            .OrderByDescending(r => r.Matcher!.Length)
            .FirstOrDefault()
        ?? host.Routes.SingleOrDefault(r => r.Matcher is null);

    // A server asks the accounts service with the credential it already holds for its own /mcp, so
    // each caller the service admits is fed exactly the variable that server's own gate reads.
    // A household's voice server is no such caller: it asks on its tunnel's accounts back-call, and
    // the service reads who it is off the live tunnel credential the relay shows (channel-identity.md).
    [Theory]
    [InlineData("mcp-channel-signalr")]
    [InlineData("mcp-channel-telegram")]
    [InlineData("mcp-idealista")]
    [InlineData("mcp-scheduling")]
    [InlineData("mcp-sugary")]
    [InlineData("mcp-apps")]
    public void TheAccountsServicesCallers_PresentTheirOwnMcpCredential(string caller)
    {
        var compose = DeploymentFiles.Compose();

        compose.Service("accounts").Environment.GetValueOrDefault($"CALLERS__{caller}")
            .ShouldBe(compose.Service(caller).Environment["MCP__SHAREDSECRET"]);
    }

    [Fact]
    public void TheAccountsService_AdmitsNoCallerThatIsNotADeploymentServer()
    {
        var compose = DeploymentFiles.Compose();

        compose.Service("accounts").Environment.Keys
            .Where(k => k.StartsWith("CALLERS__", StringComparison.Ordinal))
            .Select(k => k["CALLERS__".Length..])
            .ShouldAllBe(caller => compose.Services.ContainsKey(caller));
    }

    // The service confines each caller to its own channel — the agent to tunnel credentials, a shared
    // server to its own view of a household's configuration — by the name compose gives it
    // (InternalCallers), so a caller renamed on one side and not the other would be refused
    // everything it asks.
    [Fact]
    public void EveryChannelCallerOfTheAccountsService_IsOneItKnowsTheChannelOf()
    {
        var compose = DeploymentFiles.Compose();

        compose.Service("accounts").Environment.Keys
            .Where(k => k.StartsWith("CALLERS__", StringComparison.Ordinal))
            .Select(k => k["CALLERS__".Length..])
            .Where(caller => caller != HouseholdImportApi.Caller)
            .ShouldBe(
                [
                    InternalCallers.SignalR, InternalCallers.Telegram, InternalCallers.Agent,
                    InternalCallers.Idealista, InternalCallers.Scheduling, InternalCallers.Sugary, InternalCallers.Timers,
                    InternalCallers.Apps
                ],
                ignoreOrder: true);
    }

    // The agent has no /mcp; its relay asks with the credential the agent already holds for its own
    // API, so no secret exists for this alone.
    [Fact]
    public void TheRelay_AsksTheAccountsServiceWithTheAgentApisCredential()
    {
        var compose = DeploymentFiles.Compose();

        compose.Service("accounts").Environment.GetValueOrDefault($"CALLERS__{InternalCallers.Agent}")
            .ShouldBe(compose.Service("agent").Environment["AGENTAPI__SHAREDSECRET"]);
    }

    // A revoked tunnel credential is announced on Redis for the relay to close its tunnel, so the
    // accounts service announces it on the Redis the agent listens on.
    [Fact]
    public void TheAccountsService_AnnouncesRevocationsOnTheAgentsRedis()
    {
        var compose = DeploymentFiles.Compose();
        var accounts = compose.Service("accounts");

        accounts.Environment.GetValueOrDefault("REDIS__CONNECTIONSTRING")
            .ShouldBe(compose.Service("agent").Environment["REDIS__CONNECTIONSTRING"]);
        accounts.DependsOn.ShouldContain("redis");
    }

    // Memory text and usage per person are the operator's alone: every request to the dashboard is
    // checked for the operator's session first, and the identity the check answers is what the
    // dashboard is handed.
    [Fact]
    public void TheDashboard_AdmitsOnlyTheOperatorsSession()
    {
        var host = DeploymentFiles.Caddyfile().Host(DashboardHost);

        host.ForwardAuth.ShouldNotBeNull();
        host.ForwardAuth.Upstream.ShouldBe("accounts:8080");
        host.ForwardAuth.Uri.ShouldBe($"/accounts/verify?role=operator&channel={AppChannels.Dashboard}");
        host.ForwardAuth.CopiedHeaders.ShouldBe(ForwardedAccount.Headers, ignoreOrder: true);
        // In front of everything but the client's own files asked for by something that is not a page
        // being opened, which an installed dashboard updates itself with signed out (the dashboard's
        // gate serves those alone, and only those that are files of its web root).
        host.ForwardAuth.Matcher.ShouldBe("@checked");
        var checkedBody = Regex.Match(host.Body, @"@checked \{\s*not \{(?<conditions>[^}]*)\}\s*\}").Groups["conditions"].Value
            .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        checkedBody.ShouldBe(
            ["not header Sec-Fetch-Mode navigate", "not header Accept *text/html*", "not path /api/* /hubs/* /accounts/*"],
            ignoreOrder: true);
        host.Routes.ShouldNotBeEmpty();
        host.Routes.Where(r => r.Matcher is not ($"{AdminProtocol.Operator}/*" or "/accounts/internal/*"))
            .ShouldAllBe(r => r.Upstream == "observability:8080");
        var admin = host.Routes.Single(r => r.Matcher == $"{AdminProtocol.Operator}/*");
        admin.Upstream.ShouldBe("accounts:8080");
        admin.ForwardAuth.ShouldNotBeNull().Uri.ShouldBe($"/accounts/verify?channel={AppChannels.Admin}");
    }

    // The chat client, its hub and the hub's upload and dictation endpoints believe the person the
    // proxy hands them, so each asks the session check first and is given every header it answers.
    // SexyTime is behind the same sign-in: its server forwards the person it was handed to the
    // agent registration API, which is how a custom agent comes to be somebody's. So is Sugary, the
    // app and its API alike: each person's log is found by the account the headers name. Each check
    // names the channel the route's turns come on, which the signed assertion then names; the agent
    // refuses a turn whose assertion names another.
    [Theory]
    [InlineData(MainHost, null, ChannelIds.SignalR)]
    [InlineData(MainHost, "/hubs/*", ChannelIds.SignalR)]
    [InlineData(MainHost, "/api/attachments*", ChannelIds.SignalR)]
    [InlineData(MainHost, "/api/dictation*", ChannelIds.SignalR)]
    [InlineData(SexyHost, null, AppChannels.SexyTime)]
    [InlineData(SexyHost, "/hubs/*", ChannelIds.SignalR)]
    [InlineData(SexyHost, "/api/attachments*", ChannelIds.SignalR)]
    [InlineData(SexyHost, "/api/dictation*", ChannelIds.SignalR)]
    [InlineData(SugaryHost, null, SugaryChannel.ChannelId)]
    [InlineData(MainHost, "/accounts/admin/*", AppChannels.Admin)]
    public void TheChatClientsRoutes_GoThroughTheSessionCheckAndCopyItsHeaders(string hostName, string? matcher, string channel)
    {
        var route = DeploymentFiles.Caddyfile().Host(hostName).Routes.Single(r => r.Matcher == matcher);

        var check = route.ForwardAuth.ShouldNotBeNull($"{hostName} {matcher ?? "(catch-all)"} is not checked");
        check.Upstream.ShouldBe("accounts:8080");
        check.Uri.ShouldBe($"/accounts/verify?channel={channel}");
        check.CopiedHeaders.ShouldBe(ForwardedAccount.Headers, ignoreOrder: true);
    }

    // An installed app replaces its service worker by fetching the worker's script, and installs the
    // new one by fetching the files its shell caches — signed out too, which is how a phone holding a
    // worker from before sign-in existed gets one that lets the sign-in page through. So the app's own
    // files are served to any request that is not a page being opened: every navigation still goes
    // through the session check (and to sign-in), and so does the app's API.
    [Theory]
    [InlineData(MainHost, "webui:8080")]
    [InlineData(SugaryHost, "mcp-sugary:8080")]
    [InlineData(SexyHost, "sexytime:8080")]
    public void TheAppsOwnFiles_AreServedSignedOut_ButNoNavigationAndNoApi(string hostName, string app)
    {
        var host = DeploymentFiles.Caddyfile().Host(hostName);

        var shell = host.Routes.Single(r => r.Matcher == "@shell");
        shell.Upstream.ShouldBe(app);
        shell.ForwardAuth.ShouldBeNull();
        var matcher = Regex.Match(host.Body, @"@shell \{(?<conditions>[^}]*)\}").Groups["conditions"].Value
            .Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        matcher.ShouldBe(["not header Sec-Fetch-Mode navigate", "not header Accept *text/html*", "not path /api/*"], ignoreOrder: true);
        host.Routes.Single(r => r.Matcher == null).ForwardAuth.ShouldNotBeNull();
    }

    // Whatever a forward_auth guards, it hands the app every header the check answers, the signed
    // assertion among them, and names what it guards: the check refuses one that names nothing.
    [Fact]
    public void EverySessionCheck_NamesWhatItGuardsAndCopiesEveryHeader()
    {
        var checks = DeploymentFiles.Caddyfile().Hosts
            .SelectMany(h => h.Routes.Select(r => r.ForwardAuth).Append(h.ForwardAuth).OfType<CaddyForwardAuth>()
                .Select(check => (h.Name, Check: check)))
            .ToList();

        checks.ShouldNotBeEmpty();
        checks.ShouldAllBe(c => c.Check.CopiedHeaders.Order().SequenceEqual(ForwardedAccount.Headers.Order()));
        checks.ShouldAllBe(c => c.Check.Uri != null && c.Check.Uri.Contains("channel="));
    }

    // A home screen widget can send no cookie, so its address carries its token and skips the session
    // check, which the server stands in for (WidgetAccess). That route alone, and since the address is
    // the credential, the host writes no access log it could end up in.
    [Fact]
    public void SugarysWidget_IsRoutedWithoutTheSessionCheck_OnAHostThatLogsNoAddresses()
    {
        var host = DeploymentFiles.Caddyfile().Host(SugaryHost);

        var widget = host.Routes.Single(r => r.Matcher == "/widget/*");
        widget.Upstream.ShouldBe("mcp-sugary:8080");
        widget.ForwardAuth.ShouldBeNull();
        host.Body.Split('\n').Select(l => l.Trim())
            .ShouldNotContain(l => l == "log" || l.StartsWith("log ", StringComparison.Ordinal));
    }

    // The admin API believes only the assertion the session check signs for the admin channel, so it
    // is reached on the main host — and the operator's routes on the dashboard's, for the household
    // list — through that check alone, and on no host past it: a host that does not serve the admin
    // area refuses the path rather than hand it to the accounts service unchecked.
    [Fact]
    public void TheAdminApi_IsReachedThroughItsSessionCheckAlone()
    {
        var hosts = DeploymentFiles.Caddyfile().Hosts.Where(h => !h.Name.StartsWith('(')).ToList();

        foreach (var (hostName, path) in new[] { (MainHost, AdminProtocol.Settings), (DashboardHost, AdminProtocol.Households) })
        {
            var reached = RouteFor(DeploymentFiles.Caddyfile().Host(hostName), path).ShouldNotBeNull(hostName);
            reached.Upstream.ShouldBe("accounts:8080", hostName);
            reached.ForwardAuth.ShouldNotBeNull(hostName).Uri.ShouldBe($"/accounts/verify?channel={AppChannels.Admin}");
        }

        RouteFor(DeploymentFiles.Caddyfile().Host(DashboardHost), AdminProtocol.Settings)!.Upstream
            .ShouldNotBe("accounts:8080", "the owners' routes are the chat client's");
        foreach (var host in hosts)
        {
            foreach (var path in new[] { AdminProtocol.Settings, AdminProtocol.AllChanges, AdminProtocol.Households })
            {
                var route = RouteFor(host, path).ShouldNotBeNull($"{host.Name} {path}");
                if (route.Upstream == "accounts:8080")
                {
                    route.ForwardAuth.ShouldNotBeNull($"{host.Name} {path}").Uri.ShouldBe(
                        $"/accounts/verify?channel={AppChannels.Admin}", $"{host.Name} {path}");
                }
            }
        }
    }

    // Sign-in and enrollment are how a signed-out person gets a session at all; a login handoff's
    // live view and the logins page keep their own link-claimed cookies so a Telegram or voice
    // person can open them; SexyTime's server presents a secret of its own.
    [Theory]
    [InlineData(MainHost, "/accounts/*")]
    [InlineData(MainHost, "/browse/*")]
    [InlineData(MainHost, "/api/agents*")]
    [InlineData(SexyHost, "/accounts/*")]
    [InlineData(SexyHost, "/api/agents*")]
    [InlineData(SugaryHost, "/accounts/*")]
    public void TheRoutesThatCarryTheirOwnCredential_DoNotAskTheSessionCheck(string hostName, string matcher)
    {
        var route = DeploymentFiles.Caddyfile().Host(hostName).Routes.Single(r => r.Matcher == matcher);

        route.ForwardAuth.ShouldBeNull();
    }

    // A login handoff's live view and the logins page are served by the household's web search at its
    // own site: the proxy sends /browse/* to the relay in the agent, which forwards each exchange down
    // that server's tunnel to its pages listener, and to nothing else of the agent's.
    [Fact]
    public void TheBrowsePages_ReachTheRelayOnTheMainHostAlone()
    {
        var hosts = DeploymentFiles.Caddyfile().Hosts.Where(h => !h.Name.StartsWith('(')).ToList();

        var route = hosts.Single(h => h.Name == MainHost).Routes.Single(r => r.Matcher == $"{BrowsePages.Path}/*");
        route.Upstream.ShouldBe("agent:8080");
        route.ForwardAuth.ShouldBeNull();
        foreach (var host in hosts.Where(h => h.Name != MainHost))
        {
            RouteFor(host, $"{BrowsePages.Path}/handoff/x")?.Upstream.ShouldNotBe("agent:8080", host.Name);
        }
    }

    // The browse server is a household's, reached through its tunnel: no host, no route, nested or
    // not, names it, so neither of its listeners is reachable through the proxy by any other way.
    [Fact]
    public void NothingTheProxyRoutes_ReachesTheBrowseServer()
    {
        var upstreams = DeploymentFiles.Caddyfile().Hosts
            .SelectMany(h => h.Body.Split('\n'))
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("reverse_proxy ", StringComparison.Ordinal))
            .SelectMany(l => l["reverse_proxy ".Length..].TrimEnd('{').Split(' ', StringSplitOptions.RemoveEmptyEntries))
            // http://mcp-websearch:8081 names the same server as mcp-websearch:8081.
            .Select(u => u.Contains("://", StringComparison.Ordinal) ? u[(u.IndexOf("://", StringComparison.Ordinal) + 3)..] : u)
            .ToList();

        upstreams.ShouldNotBeEmpty();
        upstreams.ShouldNotContain(u => u.Split(':')[0] == BrowseServerListeners.Service);
    }

    // Sugary's /mcp is the deployment's, gated by the server's own credential, and a person's phone
    // never needs it: the proxy answers it 404 before the checked catch-all could pass it on.
    [Fact]
    public void SugarysMcp_IsNotReachableThroughTheProxy()
    {
        var routes = DeploymentFiles.Caddyfile().Host(SugaryHost).Routes.ToList();

        var refusal = routes.FindIndex(r => r.Matcher == "/mcp*");
        refusal.ShouldBeGreaterThanOrEqualTo(0);
        routes[refusal].Upstream.ShouldBeNull();
        routes[refusal].Body.ShouldContain("respond 404");
        refusal.ShouldBeLessThan(routes.FindIndex(r => r.Matcher is null));
    }

    // The app has no sign-in of its own any more: nothing configures an access code for it.
    [Fact]
    public void Sugary_IsGivenNoAccessCode()
    {
        DeploymentFiles.Compose().Service("mcp-sugary").Environment.Keys
            .ShouldNotContain(key => key.StartsWith("SUGARYAPP__", StringComparison.Ordinal));
        DeploymentFiles.Placeholders().Keys
            .ShouldNotContain(key => key.StartsWith("SUGARYAPP__", StringComparison.Ordinal));
    }

    [Fact]
    public void TheAccountsService_KeepsItsStoreInAVolumeOfItsOwnThatItsUserOwns()
    {
        var compose = DeploymentFiles.Compose();
        var accounts = compose.Service("accounts");

        accounts.Volumes.ShouldBe(["./volumes/accounts:/data"]);
        accounts.User.ShouldBe("${PUID:-1654}:${PGID:-1654}");
        compose.Services.Values.Where(s => s.Name != "accounts" && s.Name != "accounts-volume")
            .ShouldNotContain(s => s.Volumes.Any(v => v.StartsWith("./volumes/accounts:", StringComparison.Ordinal)));
        // Docker creates a missing bind-mount folder as root, so the one-shot step hands it to the
        // service's user before the service starts.
        var volume = compose.Service("accounts-volume");
        volume.Volumes.ShouldBe(["./volumes/accounts:/data"]);
        accounts.DependsOn.ShouldContain("accounts-volume");
    }

    // Every household's built apps are git repositories in a volume of the apps server's own; nothing
    // else mounts it, the one-shot step hands it to the server's user, and the server is told the
    // apps domain it is required to know.
    [Fact]
    public void TheAppsServer_KeepsItsRepositoriesInAVolumeOfItsOwnThatItsUserOwns()
    {
        var compose = DeploymentFiles.Compose();
        var apps = compose.Service("mcp-apps");
        var storage = new global::McpServerApps.Settings.AppsSettings { AppsDomain = "" }.StoragePath;
        var volume = $"./volumes/apps:{storage}";

        apps.Volumes.ShouldBe([volume]);
        apps.User.ShouldBe("${PUID:-1654}:${PGID:-1654}");
        apps.Environment.GetValueOrDefault("APPSDOMAIN").ShouldNotBeNullOrWhiteSpace();
        compose.Service("mcp-apps-volume").Volumes.ShouldBe([volume]);
        compose.Service("mcp-apps-volume").Dockerfile.ShouldBe("McpServerApps/Dockerfile");
        apps.DependsOn.ShouldContain("mcp-apps-volume");
        compose.Services.Values.Where(s => s.Name is not ("mcp-apps" or "mcp-apps-volume"))
            .ShouldNotContain(s => s.Volumes.Any(v => v.StartsWith("./volumes/apps:", StringComparison.Ordinal)));
        DeploymentFiles.Placeholders().Keys.ShouldContain("MCP_SECRET_APPS");
    }

    // Built apps are served on a domain of their own, one wildcard site for every app origin: all of it
    // goes to the apps server's pages listener, which decides who opens what by its own app session, so
    // no session check stands in front of it and nothing else of the deployment is served there.
    [Fact]
    public void TheAppsDomain_IsOneWildcardSite_SendingEverythingToTheAppsServersPagesListener()
    {
        var hosts = DeploymentFiles.Caddyfile().Hosts.Where(h => !h.Name.StartsWith('(')).ToList();
        var listener = new global::McpServerApps.Settings.AppListeners().Pages;
        var pages = $"mcp-apps:{listener}";

        var apps = hosts.Single(h => h.Name.Contains("{$APPSDOMAIN}", StringComparison.Ordinal));
        apps.Name.ShouldBe("*.{$APPSDOMAIN}");
        apps.Body.ShouldContain("import tls_{$CADDY_TLS:cloudflare}");
        apps.ForwardAuth.ShouldBeNull();
        apps.Body.ShouldNotContain("forward_auth");
        apps.Routes.ShouldHaveSingleItem().Matcher.ShouldBeNull();
        apps.Routes[0].Upstream.ShouldBe(pages);
        hosts.Where(h => h != apps).SelectMany(h => h.Routes)
            .ShouldNotContain(r => r.Upstream != null && r.Upstream.StartsWith("mcp-apps", StringComparison.Ordinal));
    }

    // The proxy, the apps server and the accounts service's app sign-in name one apps domain, set once
    // per deployment; it is no secret.
    // It is never under the deployment's own domain: the accounts session cookie is sent to every host
    // under that, and app code must never be sent it.
    [Fact]
    public void TheAppsDomain_IsOneValueTheProxyTheAppsServerAndTheAccountsServiceShare_OutsideTheSessionCookiesDomain()
    {
        var compose = DeploymentFiles.Compose();
        var domain = compose.Service("mcp-apps").Environment["APPSDOMAIN"];
        var cookieDomain = compose.Service("accounts").Environment["SESSIONCOOKIE__DOMAIN"];

        domain.ShouldNotBeNullOrWhiteSpace();
        compose.Service("caddy").Environment.GetValueOrDefault("APPSDOMAIN").ShouldBe(domain);
        compose.Service("accounts").Environment.GetValueOrDefault("APPSDOMAIN").ShouldBe(domain,
            "the app sign-in returns a code only to an origin under it");
        DeploymentFiles.Placeholders().Keys.ShouldNotContain("APPSDOMAIN");
        ("." + domain).EndsWith(cookieDomain, StringComparison.OrdinalIgnoreCase).ShouldBeFalse();
        DeploymentFiles.Caddyfile().Hosts.Where(h => !h.Name.StartsWith('(') && !h.Name.StartsWith('*'))
            .ShouldNotContain(h => h.Name.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }

    // A stack on somebody's own machine serves apps under .localhost, which resolves to loopback and is a
    // secure context in Chromium even on plain http (the `__Host-` cookie and the service worker need one).
    [Fact]
    public void TheSmokeStack_ServesAppsUnderALocalWildcardName()
    {
        var smoke = DeploymentFiles.Compose("docker-compose.override.smoke.yml");
        var domain = smoke.Service("mcp-apps").Environment["APPSDOMAIN"];

        domain.ShouldEndWith(".localhost");
        smoke.Service("caddy").Environment.GetValueOrDefault("APPSDOMAIN").ShouldBe(domain);
        smoke.Service("accounts").Environment.GetValueOrDefault("APPSDOMAIN").ShouldBe(domain);
    }

    // A page opened on an app origin with no app session is sent to the app sign-in on the main host,
    // which the browser reaches as any accounts page: the accounts route, no session check in front.
    [Fact]
    public void TheAppSignIn_IsReachedOnTheMainHost_PastNoSessionCheck()
    {
        var compose = DeploymentFiles.Compose();
        var publicBase = compose.Service("accounts").Environment["PUBLICBASEURL"];
        var host = DeploymentFiles.Caddyfile().Host(MainHost);

        compose.Service("mcp-apps").Environment.GetValueOrDefault("PUBLICBASEURL").ShouldBe(publicBase);
        var route = RouteFor(host, global::Domain.Apps.AppSignIn.Path).ShouldNotBeNull();
        route.Upstream.ShouldBe("accounts:8080");
        route.ForwardAuth.ShouldBeNull();
        host.ForwardAuth.ShouldBeNull();
    }

    // Every way a person's sessions end is announced on the deployment's Redis, which the apps server
    // hears to end their app sessions at once.
    [Fact]
    public void TheAppsServer_HearsSessionsEnd_OnTheAccountsServicesRedis()
    {
        var compose = DeploymentFiles.Compose();
        var apps = compose.Service("mcp-apps");

        apps.Environment.GetValueOrDefault("REDISCONNECTIONSTRING")
            .ShouldBe(compose.Service("accounts").Environment["REDIS__CONNECTIONSTRING"]);
        apps.DependsOn.ShouldContain("redis");
    }

    // An app session hands its assertion to the agent with each read (ticket 06), and the agent refuses
    // one that has run out, so it is renewed while most of its life is left: held at most half of it.
    [Fact]
    public void TheAppsServersKeptAssertion_LeavesItHalfItsLifetime()
    {
        var kept = TimeSpan.Parse(
            ShippedSetting("McpServerApps", "Sessions", "AssertionKept").GetString()!, CultureInfo.InvariantCulture);
        var lifetime = TimeSpan.Parse(
            ShippedSetting("Accounts", "AssertionLifetime").GetString()!, CultureInfo.InvariantCulture);

        kept.ShouldBeLessThanOrEqualTo(lifetime / 2);
        new global::McpServerApps.Settings.AppSessionSettings().AssertionKept.ShouldBe(kept);
    }

    // A check's page reads on the token of the call that ran it, which the agent re-mints within its
    // refresh margin of expiry, so it arrives with at least that margin left: a check stopped within it
    // never reads on a token that has run out.
    [Fact]
    public void TheAppsServersCheckDeadline_EndsBeforeTheCallsTokenDoes()
    {
        var deadline = TimeSpan.Parse(
            ShippedSetting("McpServerApps", "Checks", "Deadline").GetString()!, CultureInfo.InvariantCulture);
        var margin = TimeSpan.Parse(
            ShippedSetting("Agent", "callTokens", "refreshMargin").GetString()!, CultureInfo.InvariantCulture);

        deadline.ShouldBeLessThanOrEqualTo(margin);
        new global::McpServerApps.Settings.AppCheckSettings().Deadline.ShouldBe(deadline);
    }

    // A check runs the agent's code in a browser, so the browser runs where nothing is to be had: a
    // container of its own holding no secret, setting, volume, port or capability, unprivileged, on a
    // network it shares with the apps server alone, under Chromium's own sandbox (its syscall filter lets
    // through the user namespaces that needs, never `unconfined`).
    [Fact]
    public void TheChecksBrowser_HoldsNothing_AndSharesANetworkWithTheAppsServerAlone()
    {
        var compose = DeploymentFiles.Compose();
        var browser = compose.Service("apps-browser");

        browser.Environment.ShouldBeEmpty();
        browser.EnvFiles.ShouldBeEmpty();
        browser.Volumes.ShouldBeEmpty();
        browser.Ports.ShouldBeEmpty();
        browser.Devices.ShouldBeEmpty();
        browser.CapAdd.ShouldBeEmpty();
        browser.CapDrop.ShouldBe(["ALL"]);
        browser.Privileged.ShouldBeFalse();
        browser.SecurityOpt.ShouldBe(["no-new-privileges:true", "seccomp=${REPOSITORY_PATH}/DockerCompose/seccomp/ziggurat-apps-browser.json"]);
        File.Exists(Path.Combine(DeploymentFiles.Directory, "seccomp", "ziggurat-apps-browser.json")).ShouldBeTrue();
        browser.Networks.ShouldBe(["apps-browser"]);
        compose.Services.Values.Where(s => s.Networks.Contains("apps-browser")).Select(s => s.Name)
            .ShouldBe(["mcp-apps", "apps-browser"], ignoreOrder: true);
    }

    // The apps server runs every check on that browser's server, at the port its image listens on (one
    // Chromium refuses to dial, so not even a renderer reaches it), and the browser reaches the app at the
    // apps server's pages listener by its name on their network, never through public DNS.
    [Fact]
    public void TheAppsServer_RunsItsChecksOnTheChecksBrowser_ReachingItsPagesListenerByName()
    {
        var apps = DeploymentFiles.Compose().Service("mcp-apps").Environment;
        var port = Regex.Match(BrowserDockerfile(), @"""--port"",\s*""(\d+)""").Groups[1].Value;
        var pages = new global::McpServerApps.Settings.AppListeners().Pages;

        port.ShouldBe("10080");
        BrowserDockerfile().ShouldContain($"EXPOSE {port}");
        apps.GetValueOrDefault("CHECKS__BROWSER__ENDPOINT").ShouldBe($"ws://apps-browser:{port}/");
        apps.GetValueOrDefault("CHECKS__BROWSER__PAGES")
            .ShouldBe($"mcp-apps:{pages}");
    }

    // The apps-browser image is the one place the deployment installs Chromium; the apps server's image,
    // which holds the credential, Redis and every household's repositories, carries no browser.
    [Fact]
    public void Chromium_IsInstalledInTheChecksBrowsersImageAlone()
    {
        var root = TestHelpers.FindSolutionRoot();
        var installing = DeploymentFiles.Compose().Services.Values
            .Where(s => s.Dockerfile is not null)
            .Where(s =>
            {
                var context = s.BuildContext == "${REPOSITORY_PATH}" ? root : Path.Combine(DeploymentFiles.Directory, s.BuildContext!);
                return File.ReadLines(Path.Combine(context, s.Dockerfile!))
                    .Where(line => !line.TrimStart().StartsWith('#'))
                    .Any(line => Regex.IsMatch(line, @"\bchromium\b|mcr\.microsoft\.com/playwright", RegexOptions.IgnoreCase));
            })
            .Select(s => s.Name)
            .ToList();

        installing.ShouldBe(["apps-browser"]);
    }

    // A Playwright client and the server it connects to refuse each other unless their versions match, so
    // every image running a Playwright server pins the version the .NET projects reference.
    [Theory]
    [InlineData("apps-browser")]
    [InlineData("camoufox")]
    public void APlaywrightServersImage_PinsTheClientsPlaywright(string image)
    {
        var root = TestHelpers.FindSolutionRoot();
        var client = Regex.Match(File.ReadAllText(Path.Combine(root, "Infrastructure", "Infrastructure.csproj")),
            @"Include=""Microsoft\.Playwright"" Version=""([^""]+)""").Groups[1].Value;
        var dockerfile = File.ReadAllText(Path.Combine(DeploymentFiles.Directory, image, "Dockerfile"));

        client.ShouldNotBeNullOrEmpty();
        var pins = Regex.Matches(dockerfile, @"playwright(?:-core)?[@:]v?(\d+\.\d+\.\d+)").Select(m => m.Groups[1].Value).ToList();
        pins.ShouldNotBeEmpty();
        pins.ShouldAllBe(pin => pin == client);
    }

    private static string BrowserDockerfile() =>
        File.ReadAllText(Path.Combine(DeploymentFiles.Directory, "apps-browser", "Dockerfile"));

    // A built app's reads go from the apps server to the agent on the compose network, believed on the
    // apps server's own credential: the one the agent already holds for the apps server it dials, so no
    // secret exists for this alone and holding the agent API's opens nothing here. The route is never
    // reached through the proxy.
    [Fact]
    public void TheAppsServer_ReadsThroughTheAgent_WithTheCredentialTheAgentHoldsForIt()
    {
        var compose = DeploymentFiles.Compose();
        var apps = compose.Service("mcp-apps").Environment;
        var dialled = ServerAddress.FromUrl(ShippedSetting("Agent", "sharedServers", "apps").GetString()!).ShouldNotBeNull();

        dialled.Name.ShouldBe("mcp-apps");
        apps.GetValueOrDefault("AGENTURL").ShouldBe("http://agent:8080");
        apps["MCP__SHAREDSECRET"].ShouldBe(compose.Service("agent").Environment[$"MCP__SECRETS__{dialled.Name}"]);
        foreach (var host in DeploymentFiles.Caddyfile().Hosts.Where(h => !h.Name.StartsWith('(')))
        {
            RouteFor(host, global::Domain.Apps.AppReads.AgentRoute("read"))?.Upstream.ShouldNotBe("agent:8080", host.Name);
        }
    }

    // The agent signs every call with a key it generates on first start and keeps across rebuilds, in
    // a volume of its own; nothing else mounts it, and the one-shot step hands it to the agent's user.
    [Fact]
    public void TheAgent_KeepsItsSigningKeyInAVolumeOfItsOwnThatItsUserOwns()
    {
        var compose = DeploymentFiles.Compose();
        var agent = compose.Service("agent");

        agent.Volumes.ShouldContain("./volumes/agent-keys:/keys");
        agent.Environment["SIGNING__KEYDIRECTORY"].ShouldBe("/keys");
        agent.Environment.ShouldNotContainKey("SIGNING__PRIVATEKEY");
        agent.User.ShouldBe("${PUID:-1654}:${PGID:-1654}");
        compose.Services.Values.Where(s => s.Name != "agent" && s.Name != "agent-volume")
            .ShouldNotContain(s => s.Volumes.Any(v => v.StartsWith("./volumes/agent-keys:", StringComparison.Ordinal)));
        compose.Service("agent-volume").Volumes.ShouldBe(["./volumes/agent-keys:/keys"]);
        agent.DependsOn.ShouldContain("agent-volume");
    }

    // Every server the agent dials checks each tool call's token: for itself, by the name the agent
    // dials it at, against the keys the agent publishes on the compose network. A server told another
    // name refuses every call as aimed elsewhere; one told no keys refuses every call as unverifiable.
    [Fact]
    public void EveryServer_ChecksCallTokensForItsOwnNameAgainstTheAgentsKeys()
    {
        var compose = DeploymentFiles.Compose();
        var dialled = compose.Service("agent").Environment.Keys
            .Where(key => key.StartsWith("MCP__SECRETS__", StringComparison.Ordinal))
            .Select(key => key["MCP__SECRETS__".Length..])
            .ToList();

        dialled.ShouldNotBeEmpty();
        foreach (var server in dialled)
        {
            var environment = compose.Service(server).Environment;
            environment.GetValueOrDefault("MCP__AUDIENCE").ShouldBe(server, server);
            environment.GetValueOrDefault("MCP__AGENTKEYSURL").ShouldBe($"http://agent:8080{AgentKeysApi.Path}", server);
        }
    }

    // The agent's keys are fetched without a credential, so the proxy alone keeps them inside: on every
    // host the path lands on a route that is not the agent's.
    [Fact]
    public void TheAgentsKeys_AreNeverRoutedThroughTheProxy()
    {
        var hosts = DeploymentFiles.Caddyfile().Hosts.Where(h => !h.Name.StartsWith('(')).ToList();

        hosts.ShouldNotBeEmpty();
        hosts.ShouldContain(h => h.Routes.Any(r => r.Upstream == "agent:8080"), "a host routes the agent's API");
        foreach (var host in hosts)
        {
            RouteFor(host, AgentKeysApi.Path)?.Upstream.ShouldNotBe("agent:8080", host.Name);
        }
    }

    // A voice topic is minted for whoever the accounts service's assertion names, with no credential
    // beside it, so only the deployment's own servers may ask: on every host the path lands on a
    // route that is not the agent's.
    [Fact]
    public void TheAgentsTopicMinting_IsNeverRoutedThroughTheProxy()
    {
        var hosts = DeploymentFiles.Caddyfile().Hosts.Where(h => !h.Name.StartsWith('(')).ToList();

        hosts.ShouldNotBeEmpty();
        hosts.ShouldContain(h => h.Routes.Any(r => r.Upstream == "agent:8080"), "a host routes the agent's API");
        foreach (var host in hosts)
        {
            RouteFor(host, TopicMinting.Path)?.Upstream.ShouldNotBe("agent:8080", host.Name);
        }
    }

    // A shared server reaches a household's server through the relay's endpoint on the agent, which
    // the deployment's own network alone may call: on every host the path lands on a route that is
    // not the agent's.
    [Fact]
    public void TheRelaysEndpointForSharedServers_IsNeverRoutedThroughTheProxy()
    {
        var hosts = DeploymentFiles.Caddyfile().Hosts.Where(h => !h.Name.StartsWith('(')).ToList();

        hosts.ShouldContain(h => h.Routes.Any(r => r.Upstream == "agent:8080"), "a host routes the agent's API");
        foreach (var host in hosts)
        {
            RouteFor(host, $"{RelayedCallProtocol.For(HouseholdServerNames.Voice)}/api/voice/announce")?.Upstream
                .ShouldNotBe("agent:8080", host.Name);
        }
    }

    // The timers server (a timer) and Sugary (a glucose alarm) ring the household's voice server
    // through the relay's endpoint alone, and present there the credential the agent dials them with
    // under the name the agent dials them at; neither holds an address of the voice server, nor the
    // deployment's announce secret, which the voice server reached through its tunnel never asks for.
    [Theory]
    [InlineData("mcp-timers")]
    [InlineData("mcp-sugary")]
    public void ARelayedCaller_ReachesTheHouseholdsVoiceThroughTheRelayAlone(string caller)
    {
        var compose = DeploymentFiles.Compose();
        var server = compose.Service(caller);

        server.Environment.GetValueOrDefault("VOICEHUB__BASEURL")
            .ShouldBe($"http://agent:8080{RelayedCallProtocol.For(HouseholdServerNames.Voice)}/");
        server.Environment.GetValueOrDefault("MCP__AUDIENCE").ShouldBe(caller);
        compose.Service("agent").Environment.GetValueOrDefault($"MCP__SECRETS__{caller}")
            .ShouldBe(server.Environment["MCP__SHAREDSECRET"]);
        server.Environment.Keys.ShouldNotContain(key => key.StartsWith("Announce__", StringComparison.OrdinalIgnoreCase));
        server.Environment.Values.ShouldNotContain(value => value.Contains("mcp-channel-voice", StringComparison.Ordinal));
        server.DependsOn.ShouldNotContain("mcp-channel-voice");
    }

    // A passkey is bound to the relying party, the cookie to its domain and a sign-in's return to the
    // allowed origins, so the three must describe the hosts the proxy actually serves.
    [Fact]
    public void TheAccountsService_IsToldTheDeploymentsOwnHosts()
    {
        var caddy = DeploymentFiles.Caddyfile();
        var environment = DeploymentFiles.Compose().Service("accounts").Environment;
        var relyingParty = environment["WEBAUTHN__RELYINGPARTYID"];
        var origins = environment.Where(e => e.Key.StartsWith("ALLOWEDORIGINS__", StringComparison.Ordinal))
            .Select(e => new Uri(e.Value))
            .ToList();
        var publicBase = new Uri(environment["PUBLICBASEURL"]);

        origins.ShouldNotBeEmpty();
        origins.ShouldAllBe(o => o.Scheme == "https" && caddy.Hosts.Any(h => h.Name == o.Host));
        origins.ShouldAllBe(o => o.Host.EndsWith("." + relyingParty, StringComparison.Ordinal));
        environment["SESSIONCOOKIE__DOMAIN"].ShouldBe("." + relyingParty);
        publicBase.Host.ShouldBe(MainHost);
        origins.ShouldContain(o => o.Host == publicBase.Host, "sign-in is served there, so its passkey ceremonies run there");
    }

    // The migration into the home household is run once, by hand, on deploy: under a profile, so no
    // `up` starts it, and published nowhere.
    [Fact]
    public void TheHouseholdMigration_RunsOnlyWhenAskedFor()
    {
        var migration = DeploymentFiles.Compose().Service("household-migration");

        migration.Profiles.ShouldBe(["migrate"]);
        migration.Ports.ShouldBeEmpty();
        migration.DependsOn.ShouldBe(["base-sdk", "redis", "accounts"], ignoreOrder: true);
    }

    // The accounts service spells Sugary's channel again (it references no server), and the assertion
    // Sugary forwards as its proof of a household must name the channel Sugary is.
    [Fact]
    public void TheSugaryChannelTheAccountsServiceHoldsAssertionsTo_IsSugarysOwn() =>
        InternalCallers.SugaryChannel.ShouldBe(SugaryChannel.ChannelId);

    // The one secret households' configuration adds: the master key their data keys are wrapped with,
    // a placeholder shipped empty and handed to the accounts service as the setting it reads.
    [Fact]
    public void TheHouseholdConfigMasterKey_IsAnEmptyPlaceholderTheAccountsServiceReads()
    {
        DeploymentFiles.Placeholders()["HOUSEHOLD_CONFIG_MASTER_KEY"].ShouldBeEmpty();
        DeploymentFiles.Compose().Service("accounts").Environment.GetValueOrDefault("HOUSEHOLDCONFIG__MASTERKEY")
            .ShouldBe("${HOUSEHOLD_CONFIG_MASTER_KEY}");
    }

    // A shared server proves which household it asks about with the agent's call token it was handed,
    // and the agent proves itself with one of its own, so the accounts service checks them against the
    // keys the agent publishes on the compose network — the address its default names too.
    [Fact]
    public void TheAccountsService_ChecksCallTokensAgainstTheAgentsKeys()
    {
        var expected = $"http://agent:8080{AgentKeysApi.Path}";

        DeploymentFiles.Compose().Service("accounts").Environment.GetValueOrDefault("AGENTKEYSURL").ShouldBe(expected);
        AccountsSettings.DefaultAgentKeysUrl.ShouldBe(expected);
    }

    // The operator's household list shows each household's connected servers, which only the agent's
    // relay knows: the accounts service asks the agent's listing on the compose network with the one
    // credential the agent already presents to it, which is the agent API's own secret, so no secret
    // exists for this alone.
    [Fact]
    public void TheAccountsService_AsksTheAgentsRelayWithTheAgentApisSecret()
    {
        var compose = DeploymentFiles.Compose();
        var accounts = compose.Service("accounts").Environment;

        accounts.GetValueOrDefault("AGENTURL").ShouldBe(AccountsSettings.DefaultAgentUrl);
        AccountsSettings.DefaultAgentUrl.ShouldBe("http://agent:8080");
        accounts.GetValueOrDefault("CALLERS__agent").ShouldBe(compose.Service("agent").Environment["AGENTAPI__SHAREDSECRET"]);
    }

    // The spaces page lists the household's published apps, which only the apps server knows: the accounts
    // service asks its listing on the apps server's MCP listener, on the compose network both sit on, with
    // an assertion it signs itself, so no secret exists for this. The proxy sends nothing to that listener.
    [Fact]
    public void TheAccountsService_AsksTheAppsServersListingOnItsMcpListener()
    {
        var compose = DeploymentFiles.Compose();
        var accounts = compose.Service("accounts");

        accounts.Environment.GetValueOrDefault("APPSURL").ShouldBe(AccountsSettings.DefaultAppsUrl);
        var mcpListener = new global::McpServerApps.Settings.AppListeners().Mcp;
        AccountsSettings.DefaultAppsUrl.ShouldBe($"http://mcp-apps:{mcpListener}");
        compose.Service("mcp-apps").Networks.ShouldContain(accounts.Networks.ShouldHaveSingleItem());
        foreach (var host in DeploymentFiles.Caddyfile().Hosts.Where(h => !h.Name.StartsWith('(')))
        {
            RouteFor(host, global::Domain.Apps.PublishedApps.Path)?.Upstream.ShouldNotBe("mcp-apps:8080", host.Name);
        }
    }

    // WebChat's space switcher lists a person's apps, which only the apps server knows: the chat hub asks
    // its listing on the apps server's MCP listener, on the compose network both sit on, with the accounts
    // service's word for the connection's session on persons-apps — which only the hub may ask for — so no
    // secret exists for this. The proxy sends nothing to that listener.
    [Fact]
    public void TheChatHub_AsksTheAppsServersListingOfAPersonsAppsOnItsMcpListener()
    {
        var compose = DeploymentFiles.Compose();
        var hub = compose.Service("mcp-channel-signalr");
        var mcpListener = new global::McpServerApps.Settings.AppListeners().Mcp;

        hub.Environment.GetValueOrDefault("APPSURL").ShouldBe($"http://mcp-apps:{mcpListener}");
        compose.Service("mcp-apps").Networks.ShouldContain(hub.Networks.ShouldHaveSingleItem());
        foreach (var host in DeploymentFiles.Caddyfile().Hosts.Where(h => !h.Name.StartsWith('(')))
        {
            RouteFor(host, global::Domain.Apps.PersonsApps.Path)?.Upstream.ShouldNotBe("mcp-apps:8080", host.Name);
        }
    }

    // The household import can make the operator and mint their link, so the accounts service admits
    // it by a credential nothing else holds — one the deployment leaves empty except for the run.
    [Fact]
    public void TheHouseholdImport_IsAdmittedByACredentialOnlyTheMigrationHolds()
    {
        var compose = DeploymentFiles.Compose();
        const string credential = "${HOUSEHOLD_MIGRATION_SECRET}";

        compose.Service("accounts").Environment["CALLERS__household-migration"].ShouldBe(credential);
        compose.Service("household-migration").Environment["ACCOUNTS__CREDENTIAL"].ShouldBe(credential);
        DeploymentFiles.Placeholders()["HOUSEHOLD_MIGRATION_SECRET"].ShouldBeEmpty();
    }

    // The migration moves data in the Sugary server's and the voice channel's own volumes, as their
    // user, and finds them where its settings look. It runs before the home moves into the tenant kit,
    // so the voice profiles are still where the deployment kept them — the folder the kit's voice reads
    // at the same place under the kit, once the move has carried it over (TenantKit/README.md).
    [Fact]
    public void TheHouseholdMigration_MountsTheVolumesItMovesDataInWhereItLooksForThem()
    {
        var compose = DeploymentFiles.Compose();
        var migration = compose.Service("household-migration");
        var defaults = new global::HouseholdMigration.MigrationSettings();
        string source(ComposeService service, string target) =>
            service.Volumes.Single(v => v.EndsWith($":{target}", StringComparison.Ordinal)).Split(':')[0];

        source(migration, "/sugary").ShouldBe(source(compose.Service("mcp-sugary"), "/data"));
        source(migration, "/voices").ShouldBe(source(DeploymentFiles.KitCompose().Service("mcp-channel-voice"), "/voices"));
        migration.Volumes.ShouldContain("./volumes/household-migration:/input:ro");
        Path.GetDirectoryName(defaults.SugaryDatabasePath).ShouldBe("/sugary");
        Path.GetFileName(defaults.SugaryDatabasePath)
            .ShouldBe(Path.GetFileName(new global::McpServerSugary.Settings.SugarySettings().DatabasePath));
        defaults.VoicesPath.ShouldBe("/voices");
        migration.User.ShouldBe(compose.Service("mcp-sugary").User);
        migration.User.ShouldBe(DeploymentFiles.KitCompose().Service("mcp-channel-voice").User);
    }

    [Fact]
    public void TheDebugOverride_PublishesOnLoopbackOnly()
    {
        var ports = DeploymentFiles.Compose("docker-compose.override.ports.yml").Services.Values
            .SelectMany(s => s.Ports)
            .ToList();

        ports.ShouldNotBeEmpty();
        ports.ShouldAllBe(p => p.BindAddress == "127.0.0.1");
    }

    // The Telegram bot carries the assertion of the answer it admitted a sender on, which it keeps for
    // LinkCache:Linked. An assertion lives AssertionLifetime from when the accounts service
    // signed it and the agent refuses a turn whose assertion has run out, so a kept answer must leave
    // its assertion most of its life for the hop to the agent: at most half of it spent in the cache.
    [Fact]
    public void TheTelegramBotsKeptAnswer_LeavesItsAssertionHalfItsLifetime()
    {
        var linked = TimeSpan.Parse(
            ShippedSetting("McpChannelTelegram", "LinkCache", "Linked").GetString()!, CultureInfo.InvariantCulture);
        var lifetime = TimeSpan.Parse(
            ShippedSetting("Accounts", "AssertionLifetime").GetString()!, CultureInfo.InvariantCulture);

        linked.ShouldBeLessThanOrEqualTo(lifetime / 2);
    }

    private static System.Text.Json.JsonElement ShippedSetting(string project, params string[] path)
    {
        var file = Path.Combine(Tests.E2E.Fixtures.TestHelpers.FindSolutionRoot(), project, "appsettings.json");
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file),
            new System.Text.Json.JsonDocumentOptions
            {
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true
            });
        return path.Aggregate(document.RootElement, (element, name) => element.GetProperty(name)).Clone();
    }

    // The fixed keys the test and eval stacks sign with are private keys anyone with the repository
    // can read, so a deployment handed one would believe tokens anyone could mint. A deployment's
    // issuers generate their own, in their own volumes. Looked for by the key and by the id it is
    // published under, through line breaks, so a key pasted as wrapped PEM is found as well.
    [Fact]
    public void TheTestSigningKeys_AreNeverShipped()
    {
        var needles = TestSigningKeys.All
            .SelectMany(key => new[] { key, TestSigningKeys.Store(key).CurrentKeyId })
            .ToList();

        DeploymentFiles.Shipped()
            .Where(file =>
            {
                var text = string.Concat(File.ReadAllText(file).Replace("\\n", "").Where(c => !char.IsWhiteSpace(c)));
                return needles.Any(needle => text.Contains(needle, StringComparison.Ordinal));
            })
            .ShouldBeEmpty("a test signing key in a shipped file is a key every reader of the repository holds");
    }
}