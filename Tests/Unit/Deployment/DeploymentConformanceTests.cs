using System.Globalization;
using Accounts.Internal;
using Agent.App;
using Domain.DTOs.Channel;
using Domain.Security;
using McpServerSugary.Channel;
using Shouldly;

namespace Tests.Unit.Deployment;

// What the deployment files promise about the network and its secrets, checked where the operator
// ships them: a service handed more than it reads, or published past the proxy, fails here before it
// reaches a host.
public class DeploymentConformanceTests
{
    private const string MainHost = "assistants.herfluffness.com";
    private const string SexyHost = "sexy.herfluffness.com";
    private const string SugaryHost = "sugary.herfluffness.com";

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
    [InlineData("LOGINS__STATEKEY", new[] { "mcp-websearch", "household-migration" })]
    [InlineData("HOUSEHOLD_MIGRATION_SECRET", new[] { "accounts", "household-migration" })]
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

    // The sandbox runs whatever the model writes. The servers behind the proxy believe the identity
    // headers it copies on because only the proxy reaches them, so the sandbox must not reach them
    // either: it shares a network with the agent alone, which dials its /mcp and whose exec bridge
    // it calls back.
    [Fact]
    public void TheSandbox_SharesANetworkWithTheAgentAlone()
    {
        var compose = DeploymentFiles.Compose();
        var sandbox = compose.Service("mcp-sandbox").Networks;

        sandbox.ShouldNotBeEmpty();
        compose.Services.Values
            .Where(s => s.Name is not ("mcp-sandbox" or "agent") && s.Networks.Intersect(sandbox).Any())
            .Select(s => s.Name)
            .ShouldBeEmpty();
        compose.Service("agent").Networks.Intersect(sandbox).ShouldNotBeEmpty("the agent and the sandbox call each other");
        compose.Service("agent").Networks.Except(sandbox).ShouldNotBeEmpty("the agent still dials every other server");
    }

    [Theory]
    [InlineData("mcp-sugary")]
    [InlineData("mcp-channel-signalr")]
    [InlineData("observability")]
    [InlineData("webui")]
    [InlineData("accounts")]
    public void NoServiceThatBelievesTheForwardedHeaders_SharesANetworkWithTheSandbox(string service)
    {
        var compose = DeploymentFiles.Compose();
        var networks = compose.Service(service).Networks;

        networks.ShouldNotBeEmpty();
        networks.Intersect(compose.Service("mcp-sandbox").Networks).ShouldBeEmpty();
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

    // The keys assertions verify with are asked for without a credential, so it is the proxy alone
    // that keeps them inside: on every host the route the path lands on is a refusal or an upstream
    // that is not the accounts service, never the accounts route.
    [Fact]
    public void TheAccountsJwks_IsRefusedThroughTheProxyOnEveryHost()
    {
        var hosts = DeploymentFiles.Caddyfile().Hosts.Where(h => !h.Name.StartsWith('(')).ToList();

        hosts.ShouldNotBeEmpty();
        foreach (var host in hosts)
        {
            var route = RouteFor(host, InternalApi.JwksPath);
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
    [Theory]
    [InlineData("mcp-channel-signalr")]
    [InlineData("mcp-channel-telegram")]
    [InlineData("mcp-channel-voice")]
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

    // The service confines each caller to its own channel by the name compose gives it
    // (InternalCallers), so a channel server renamed on one side and not the other would be refused
    // everything it asks.
    [Fact]
    public void EveryChannelCallerOfTheAccountsService_IsOneItKnowsTheChannelOf()
    {
        var compose = DeploymentFiles.Compose();

        compose.Service("accounts").Environment.Keys
            .Where(k => k.StartsWith("CALLERS__", StringComparison.Ordinal))
            .Select(k => k["CALLERS__".Length..])
            .Where(caller => caller != HouseholdImportApi.Caller)
            .ShouldBe([InternalCallers.SignalR, InternalCallers.Telegram, InternalCallers.Voice], ignoreOrder: true);
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
        host.ForwardAuth.Uri.ShouldBe($"/accounts/verify?role=operator&channel={AppChannels.Dashboard}");
        host.ForwardAuth.CopiedHeaders.ShouldBe(ForwardedAccount.Headers, ignoreOrder: true);
        host.Routes.ShouldNotBeEmpty();
        host.Routes.ShouldAllBe(r => r.Upstream == "observability:8080");
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

    // Sign-in and enrollment are how a signed-out person gets a session at all; a login handoff's
    // live view and the logins page keep their own link-claimed cookies so a Telegram or voice
    // person can open them; an outpost and SexyTime's server present secrets of their own.
    [Theory]
    [InlineData(MainHost, "/accounts/*")]
    [InlineData(MainHost, "/browse/*")]
    [InlineData(MainHost, "/api/outposts*")]
    [InlineData(MainHost, "/api/agents*")]
    [InlineData(SexyHost, "/accounts/*")]
    [InlineData(SexyHost, "/api/agents*")]
    [InlineData(SugaryHost, "/accounts/*")]
    public void TheRoutesThatCarryTheirOwnCredential_DoNotAskTheSessionCheck(string hostName, string matcher)
    {
        var route = DeploymentFiles.Caddyfile().Host(hostName).Routes.Single(r => r.Matcher == matcher);

        route.ForwardAuth.ShouldBeNull();
    }

    // A login handoff's live view and the logins page answer on the browse server's second
    // listener, which serves nothing else, so the route that reaches them cannot reach its /mcp.
    [Fact]
    public void TheBrowsePages_ReachTheBrowseServersPagesListener()
    {
        var route = DeploymentFiles.Caddyfile().Host(MainHost).Routes.Single(r => r.Matcher == "/browse/*");

        route.Upstream.ShouldBe($"{BrowseServerListeners.Service}:{BrowseServerListeners.Pages}");
        BrowseServerListeners.Pages.ShouldNotBe(BrowseServerListeners.Mcp);
    }

    // Any host, any route, nested or not: whatever the proxy hands the browse server lands on the
    // pages listener, never on the one /mcp answers.
    [Fact]
    public void NothingTheProxyRoutes_ReachesTheBrowseServersMcpListener()
    {
        var upstreams = DeploymentFiles.Caddyfile().Hosts
            .SelectMany(h => h.Body.Split('\n'))
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("reverse_proxy ", StringComparison.Ordinal))
            .SelectMany(l => l["reverse_proxy ".Length..].TrimEnd('{').Split(' ', StringSplitOptions.RemoveEmptyEntries))
            // http://mcp-websearch:8080 names the same listener as mcp-websearch:8080.
            .Select(u => u.Contains("://", StringComparison.Ordinal) ? u[(u.IndexOf("://", StringComparison.Ordinal) + 3)..] : u)
            .Where(u => u.Split(':')[0] == BrowseServerListeners.Service)
            .ToList();

        upstreams.ShouldNotBeEmpty();
        upstreams.ShouldAllBe(u => u == $"{BrowseServerListeners.Service}:{BrowseServerListeners.Pages}");
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
    // user, and finds them where its settings look.
    [Fact]
    public void TheHouseholdMigration_MountsTheVolumesItMovesDataInWhereItLooksForThem()
    {
        var compose = DeploymentFiles.Compose();
        var migration = compose.Service("household-migration");
        var defaults = new global::HouseholdMigration.MigrationSettings();
        string Source(ComposeService service, string target) =>
            service.Volumes.Single(v => v.EndsWith($":{target}", StringComparison.Ordinal)).Split(':')[0];

        Source(migration, "/sugary").ShouldBe(Source(compose.Service("mcp-sugary"), "/data"));
        Source(migration, "/voices").ShouldBe(Source(compose.Service("mcp-channel-voice"), "/voices"));
        migration.Volumes.ShouldContain("./volumes/household-migration:/input:ro");
        Path.GetDirectoryName(defaults.SugaryDatabasePath).ShouldBe("/sugary");
        Path.GetFileName(defaults.SugaryDatabasePath)
            .ShouldBe(Path.GetFileName(new global::McpServerSugary.Settings.SugarySettings().DatabasePath));
        defaults.VoicesPath.ShouldBe("/voices");
        migration.User.ShouldBe(compose.Service("mcp-sugary").User);
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