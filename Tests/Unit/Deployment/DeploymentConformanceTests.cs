using Domain.Security;
using Shouldly;

namespace Tests.Unit.Deployment;

// What the deployment files promise about the network and its secrets, checked where the operator
// ships them: a service handed more than it reads, or published past the proxy, fails here before it
// reaches a host.
public class DeploymentConformanceTests
{
    private const string MainHost = "assistants.herfluffness.com";
    private const string SexyHost = "sexy.herfluffness.com";

    [Fact]
    public void OutpostRegistration_ReachesTheAgentThroughTheProxy()
    {
        var host = DeploymentFiles.Caddyfile().Host(MainHost);

        host.Routes.ShouldContain(r => r.Matcher == "/api/outposts*" && r.Upstream == "agent:8080");
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

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("homeassistant")]
    [InlineData("filebrowser")]
    [InlineData("jackett")]
    [InlineData("lemonade")]
    public void TheOperatorsLanTools_ListenOnTheNamedLanAddress(string service)
    {
        var ports = DeploymentFiles.Compose().Service(service).Ports;

        ports.ShouldNotBeEmpty();
        ports.ShouldAllBe(p => p.BindAddress == "${LAN_BIND_ADDRESS:-127.0.0.1}");
    }

    [Fact]
    public void OnlyTheProxyAndTheLanTools_ArePublishedAtAll()
    {
        string[] published = ["caddy", "redis", "qbittorrent", "homeassistant", "filebrowser", "jackett", "lemonade"];

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
    [InlineData("OPENROUTER__APIKEY", new[] { "agent", "mcp-websearch", "mcp-channel-voice" })]
    [InlineData("HOMEASSISTANT__TOKEN", new[] { "mcp-homeassistant" })]
    [InlineData("LOGINS__STATEKEY", new[] { "mcp-websearch" })]
    [InlineData("BOTS__0__BOTTOKEN", new[] { "mcp-channel-telegram" })]
    [InlineData("BOTS__1__BOTTOKEN", new[] { "mcp-channel-telegram" })]
    [InlineData("WEBPUSH__PRIVATEKEY", new[] { "mcp-channel-signalr", "mcp-sugary" })]
    [InlineData("AGENTAPI__SHAREDSECRET", new[] { "agent" })]
    [InlineData("OUTPOSTS__SHAREDSECRET", new[] { "agent" })]
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

        credentials.Count.ShouldBeGreaterThan(10);
        credentials.GroupBy(c => c.Variable)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(c => c.Name))}")
            .ShouldBeEmpty("holding one server's credential must open no other");
    }

    [Fact]
    public void TheSandbox_HoldsNoCredentialButItsOwn()
    {
        var compose = DeploymentFiles.Compose();
        var own = compose.Service("mcp-sandbox").Environment["MCP__SHAREDSECRET"];
        var others = compose.Services.Values
            .Where(s => s.Name != "mcp-sandbox" && s.Environment.ContainsKey("MCP__SHAREDSECRET"))
            .Select(s => s.Environment["MCP__SHAREDSECRET"])
            .ToHashSet();

        compose.Service("mcp-sandbox").Environment.Values.ShouldNotContain(v => others.Contains(v));
        own.ShouldNotBeNullOrEmpty();
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

    // Sign-in, enrollment and the session check live under /accounts on the main host; Caddy's
    // handle passes the path as it is, which is the path the service maps its routes under.
    [Fact]
    public void TheAccountsPages_ReachTheAccountsServiceOnTheMainHost()
    {
        var host = DeploymentFiles.Caddyfile().Host(MainHost);

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

    // A server asks the accounts service with the credential it already holds for its own /mcp, so
    // each caller the service admits is fed exactly the variable that server's own gate reads.
    [Theory]
    [InlineData("mcp-channel-signalr")]
    [InlineData("mcp-channel-telegram")]
    [InlineData("mcp-channel-voice")]
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

    // Memory text and usage per person are the operator's alone: every request to the dashboard is
    // checked for the operator's session first, and the identity the check answers is what the
    // dashboard is handed.
    [Fact]
    public void TheDashboard_AdmitsOnlyTheOperatorsSession()
    {
        var host = DeploymentFiles.Caddyfile().Host("assistants-dashboard.herfluffness.com");

        host.ForwardAuth.ShouldNotBeNull();
        host.ForwardAuth.Upstream.ShouldBe("accounts:8080");
        host.ForwardAuth.Uri.ShouldBe("/accounts/verify?role=operator");
        host.ForwardAuth.CopiedHeaders.ShouldBe(["X-Ziggurat-User", "X-Ziggurat-Tenant", "X-Ziggurat-Roles"]);
        host.Routes.ShouldNotBeEmpty();
        host.Routes.ShouldAllBe(r => r.Upstream == "observability:8080");
    }

    // The chat client, its hub and the hub's upload and dictation endpoints believe the person the
    // proxy hands them, so each asks the session check first and is given every header it answers.
    [Theory]
    [InlineData(MainHost, null)]
    [InlineData(MainHost, "/hubs/*")]
    [InlineData(MainHost, "/api/attachments*")]
    [InlineData(MainHost, "/api/dictation*")]
    [InlineData(SexyHost, "/hubs/*")]
    [InlineData(SexyHost, "/api/attachments*")]
    [InlineData(SexyHost, "/api/dictation*")]
    public void TheChatClientsRoutes_GoThroughTheSessionCheckAndCopyItsHeaders(string hostName, string? matcher)
    {
        var route = DeploymentFiles.Caddyfile().Host(hostName).Routes.Single(r => r.Matcher == matcher);

        var check = route.ForwardAuth.ShouldNotBeNull($"{hostName} {matcher ?? "(catch-all)"} is not checked");
        check.Upstream.ShouldBe("accounts:8080");
        check.Uri.ShouldBe("/accounts/verify");
        check.CopiedHeaders.ShouldBe(ForwardedAccount.Headers, ignoreOrder: true);
    }

    // Sign-in and enrollment are how a signed-out person gets a session at all; a login handoff's
    // live view and the logins page keep their own link-claimed cookies so a Telegram or voice
    // person can open them; an outpost and SexyTime's server present secrets of their own.
    [Theory]
    [InlineData(MainHost, "/accounts/*")]
    [InlineData(MainHost, "/browse/*")]
    [InlineData(MainHost, "/api/outposts*")]
    [InlineData(MainHost, "/api/agents*")]
    [InlineData(SexyHost, "/api/agents*")]
    public void TheRoutesThatCarryTheirOwnCredential_DoNotAskTheSessionCheck(string hostName, string matcher)
    {
        var route = DeploymentFiles.Caddyfile().Host(hostName).Routes.Single(r => r.Matcher == matcher);

        route.ForwardAuth.ShouldBeNull();
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

    [Fact]
    public void TheDebugOverride_PublishesOnLoopbackOnly()
    {
        var ports = DeploymentFiles.Compose("docker-compose.override.ports.yml").Services.Values
            .SelectMany(s => s.Ports)
            .ToList();

        ports.ShouldNotBeEmpty();
        ports.ShouldAllBe(p => p.BindAddress == "127.0.0.1");
    }
}