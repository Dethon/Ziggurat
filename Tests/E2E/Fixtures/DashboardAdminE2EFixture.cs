using System.Text;
using System.Text.RegularExpressions;
using Domain.DTOs.Channel;
using Domain.DTOs.Tunnel;
using Domain.Security;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Tests.Integration.Fixtures;

namespace Tests.E2E.Fixtures;

// The dashboard as the operator reaches it in the deployment: behind DockerCompose/caddy/Caddyfile's
// dashboard host, whose session check admits the operator alone, with the accounts service's admin
// API for the operator's routes on the same host behind its own check for the admin channel. A test
// signs in by a session the accounts service's own command mints, as the operator would have after
// enrolling. The agent's relay is a stub in this process answering the listing of a household's
// connected servers, so a test says which servers a household has connected.
//
// For the household list and what the operator's later admin pages (usage, suspension) add to it;
// the dashboard's metric pages keep DashboardE2EFixture, which needs no accounts service.
public partial class DashboardAdminE2EFixture : E2EFixtureBase
{
    private const string RedisPassword = "e2e-dashboard-admin";
    private const string AgentCredential = "e2e-agent-api-secret";

    private INetwork? _network;
    private IContainer? _redis;
    private IContainer? _accounts;
    private IContainer? _observability;
    private IContainer? _caddy;
    private WebApplication? _agent;
    private int _caddyPort;
    private readonly Dictionary<string, List<ConnectedServer>> _connected = [];
    private readonly SemaphoreSlim _commands = new(1, 1);

    public string DashboardUrl { get; private set; } = "";

    protected override async Task BuildImagesAsync(CancellationToken ct)
    {
        var solutionRoot = TestHelpers.FindSolutionRoot();
        await TestHelpers.EnsureBaseSdkImageAsync(solutionRoot, ct);
        await Task.WhenAll(
            TestHelpers.EnsureImageAsync(solutionRoot, E2EImages.Observability, ct),
            TestHelpers.EnsureImageAsync(solutionRoot, E2EImages.Accounts, ct));
    }

    protected override async Task StartContainersAsync(CancellationToken ct)
    {
        _network = TestContainers.Network().WithName($"e2e-dashboard-admin-{Guid.NewGuid():N}").Build();
        await _network.CreateAsync(ct);
        var agentPort = await StartAgentListingAsync(ct);

        // Taken here rather than by Docker: the accounts service builds its links on where it is served.
        _caddyPort = TestPort.GetAvailable();
        var baseUrl = $"http://localhost:{_caddyPort}";

        _redis = TestContainers.Container("redis/redis-stack-server:latest")
            .WithName($"redis-{Guid.NewGuid():N}")
            .WithNetwork(_network)
            .WithNetworkAliases("redis")
            .WithEnvironment("REDIS_ARGS", $"--requirepass {RedisPassword}")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "-a", RedisPassword, "ping"))
            .Build();

        // As compose wires it, but for this host: its store in a tmpfs that starts empty, the stack's
        // fixed signing key, and the agent's relay where this process answers for it.
        _accounts = TestContainers.Container(E2EImages.Accounts.ImageName)
            .WithNetwork(_network)
            .WithNetworkAliases("accounts")
            .WithTmpfsMount("/data")
            .WithExtraHost("host.docker.internal", "host-gateway")
            .WithEnvironment("WEBAUTHN__RELYINGPARTYID", "localhost")
            .WithEnvironment("ALLOWEDORIGINS__0", baseUrl)
            .WithEnvironment("PUBLICBASEURL", baseUrl)
            .WithEnvironment("SIGNING__PRIVATEKEY", TestSigningKeys.Accounts)
            .WithEnvironment("CALLERS__agent", AgentCredential)
            .WithEnvironment("AGENTURL", $"http://host.docker.internal:{agentPort}")
            .WithEnvironment("TZ", "Europe/Madrid")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/accounts/sign-in")))
            .Build();

        await Task.WhenAll(_redis.StartAsync(ct), _accounts.StartAsync(ct));

        var probe = TestAssertions.Of(
            new Account("e2e-household", "e2e-operator"), AppChannels.Dashboard, ["owner", ForwardedAccount.OperatorRole]);
        _observability = TestContainers.Container(E2EImages.Observability.ImageName)
            .WithNetwork(_network)
            .WithNetworkAliases("observability")
            .WithEnvironment("REDIS__CONNECTIONSTRING", $"redis:6379,password={RedisPassword}")
            .WithEnvironment("ACCOUNTS__JWKSURL", "http://accounts:8080/accounts/internal/jwks")
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r
                    .ForPath("/api/metrics/health")
                    .ForPort(8080)
                    .WithHeader(ForwardedAccount.AssertionHeader, probe)))
            .Build();
        await _observability.StartAsync(ct);

        // The dashboard host, minus TLS, mirroring DockerCompose/caddy/Caddyfile.
        const string copied = "copy_headers X-Ziggurat-User X-Ziggurat-Tenant X-Ziggurat-Roles X-Ziggurat-Assertion";
        var caddyfile =
            ":80 {\n" +
            "    forward_auth accounts:8080 {\n" +
            $"        uri /accounts/verify?role=operator&channel={AppChannels.Dashboard}\n" +
            $"        {copied}\n" +
            "    }\n" +
            "    handle /accounts/internal/* {\n" +
            "        respond 404\n" +
            "    }\n" +
            "    handle /accounts/admin/operator/* {\n" +
            "        forward_auth accounts:8080 {\n" +
            $"            uri /accounts/verify?channel={AppChannels.Admin}\n" +
            $"            {copied}\n" +
            "        }\n" +
            "        reverse_proxy accounts:8080\n" +
            "    }\n" +
            "    handle {\n" +
            "        reverse_proxy observability:8080\n" +
            "    }\n" +
            "}\n";
        _caddy = TestContainers.Container("caddy:2-alpine")
            .WithNetwork(_network)
            .WithPortBinding(_caddyPort, 80)
            .WithResourceMapping(Encoding.UTF8.GetBytes(caddyfile), "/etc/caddy/Caddyfile")
            // Everything is behind the operator's check, which answers a script signed out with 401.
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(80).ForPath("/").ForStatusCode(System.Net.HttpStatusCode.Unauthorized)))
            .Build();
        await _caddy.StartAsync(ct);

        DashboardUrl = $"{baseUrl}/";
    }

    // The operator, made by the accounts service's command the first time and signed in by a session
    // it mints, as `mint-link --operator` and `mint-session` do on the deployment's machine.
    // The page and the home household's id, which the operator belongs to.
    public async Task<(IPage Page, string HomeId)> CreateOperatorPageAsync()
    {
        var (userId, homeId) = Who(await AccountsAsync("mint-link", "--operator", "--name", "Fran"));
        return (await CreatePageSignedInAsync((await AccountsAsync("mint-session", "--user", userId)).Trim()), homeId);
    }

    // Somebody of the home household who is not the operator: an owner, say.
    public async Task<IPage> CreatePersonPageAsync(string name, string role)
    {
        var (userId, _) = Who(await AccountsAsync("create-user", "--name", name, "--role", role));
        return await CreatePageSignedInAsync((await AccountsAsync("mint-session", "--user", userId)).Trim());
    }

    // What the agent's relay will say is connected for a household from now on.
    public void Connect(string householdId, ConnectedServer server)
    {
        lock (_connected)
        {
            if (!_connected.TryGetValue(householdId, out var servers))
            {
                _connected[householdId] = servers = [];
            }

            servers.Add(server);
        }
    }

    private async Task<IPage> CreatePageSignedInAsync(string session)
    {
        var page = await CreatePageAsync();
        await page.Context.AddCookiesAsync(
        [
            new Cookie
            {
                Name = "ziggurat_session", Value = session, Url = DashboardUrl, HttpOnly = true, SameSite = SameSiteAttribute.Lax
            }
        ]);
        return page;
    }

    private async Task<string> AccountsAsync(params string[] args)
    {
        await _commands.WaitAsync();
        try
        {
            var result = await _accounts!.ExecAsync(["dotnet", "Accounts.dll", .. args]);
            return result.ExitCode == 0
                ? result.Stdout
                : throw new InvalidOperationException(
                    $"accounts {string.Join(' ', args)} exited {result.ExitCode}: {result.Stdout}{result.Stderr}");
        }
        finally
        {
            _commands.Release();
        }
    }

    private static (string UserId, string TenantId) Who(string output)
    {
        var who = WhoWasMade().Match(output);
        return who.Success
            ? (who.Groups["user"].Value, who.Groups["household"].Value)
            : throw new InvalidOperationException($"no person in: {output}");
    }

    [GeneratedRegex(@"user (?<user>[0-9a-f]+), household (?<household>[0-9a-f]+)")]
    private static partial Regex WhoWasMade();

    // The agent's listing (ConnectedServers.Path), behind the agent API's secret as the agent serves it.
    private async Task<int> StartAgentListingAsync(CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(kestrel => kestrel.ListenAnyIP(0));
        builder.Logging.ClearProviders();
        _agent = builder.Build();
        _agent.MapGet(ConnectedServers.Path + "/{tenantId}", (string tenantId, HttpContext http) =>
        {
            if (!SharedSecret.Matches(http.Request.Headers.Authorization.ToString(), AgentCredential))
            {
                return Results.Unauthorized();
            }

            lock (_connected)
            {
                return Results.Json(_connected.GetValueOrDefault(tenantId)?.ToList() ?? [], TunnelProtocol.SerializerOptions);
            }
        });
        await _agent.StartAsync(ct);
        return new Uri(_agent.Urls.First()).Port;
    }

    protected override async Task StopContainersAsync()
    {
        foreach (var container in new[] { _caddy, _observability, _accounts, _redis }.OfType<IContainer>())
        {
            await container.DisposeAsync();
        }

        if (_agent is not null)
        {
            await _agent.DisposeAsync();
        }

        if (_network is not null)
        {
            await _network.DeleteAsync();
        }

        if (_caddyPort != 0)
        {
            TestPort.Release(_caddyPort);
        }
    }
}

[CollectionDefinition("DashboardAdminE2E")]
public class DashboardAdminE2ECollection : ICollectionFixture<DashboardAdminE2EFixture>;