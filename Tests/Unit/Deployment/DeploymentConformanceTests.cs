using System.Globalization;
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
        compose.Networks.Keys.ShouldBe(["jackbot"]);
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
    // the service reads who it is off the live tunnel credential the relay shows (accounts.md).
    [Theory]
    [InlineData("mcp-channel-signalr")]
    [InlineData("mcp-channel-telegram")]
    [InlineData("mcp-idealista")]
    [InlineData("mcp-scheduling")]
    [InlineData("mcp-sugary")]
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
                    InternalCallers.Idealista, InternalCallers.Scheduling, InternalCallers.Sugary
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

    // The timers server rings the household's voice server through the relay's endpoint alone, and
    // presents there the credential the agent dials it with under the name the agent dials it at; it
    // holds no address of the voice server, nor the deployment's announce secret, which the voice
    // server reached through its tunnel never asks it for.
    [Fact]
    public void TheTimersServer_ReachesTheHouseholdsVoiceThroughTheRelayAlone()
    {
        var compose = DeploymentFiles.Compose();
        var timers = compose.Service("mcp-timers");

        timers.Environment.GetValueOrDefault("VOICEHUB__BASEURL")
            .ShouldBe($"http://agent:8080{RelayedCallProtocol.For(HouseholdServerNames.Voice)}/");
        compose.Service("agent").Environment.GetValueOrDefault($"MCP__SECRETS__{timers.Environment["MCP__AUDIENCE"]}")
            .ShouldBe(timers.Environment["MCP__SHAREDSECRET"]);
        timers.Environment.Keys.ShouldNotContain(key => key.StartsWith("Announce__", StringComparison.OrdinalIgnoreCase));
        timers.Environment.Values.ShouldNotContain(value => value.Contains("mcp-channel-voice", StringComparison.Ordinal));
        timers.DependsOn.ShouldNotContain("mcp-channel-voice");
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
        string Source(ComposeService service, string target) =>
            service.Volumes.Single(v => v.EndsWith($":{target}", StringComparison.Ordinal)).Split(':')[0];

        Source(migration, "/sugary").ShouldBe(Source(compose.Service("mcp-sugary"), "/data"));
        Source(migration, "/voices").ShouldBe(Source(DeploymentFiles.KitCompose().Service("mcp-channel-voice"), "/voices"));
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