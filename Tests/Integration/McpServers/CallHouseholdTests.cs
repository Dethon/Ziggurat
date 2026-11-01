using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Accounts.Commands;
using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Household;
using Domain.Prompts;
using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.Agents.Mcp;
using Infrastructure.Clients.Accounts;
using Mcp.Hosting;
using McpServerIdealista.Modules;
using McpServerScheduling.Modules;
using McpServerScheduling.Services;
using McpServerScheduling.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;
using IdealistaSettings = McpServerIdealista.Settings;

namespace Tests.Integration.McpServers;

// A shared server reading its section of the calling household's configuration (seam 4): a real
// server host, called as the agent calls it, asking a real accounts service with what the call
// proved — never with a household it picked — and seeing a saved section without a restart.
public class CallHouseholdTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string Scheduling = "mcp-scheduling";
    private const string Idealista = "mcp-idealista";

    private const string Tokyo = """{ "name": "Casa", "timeZone": "Asia/Tokyo" }""";

    [Fact]
    public async Task AOneShotWithNoOffset_IsReadInItsHouseholdsZone()
    {
        await using var accounts = await StartAccountsAsync();
        var (household, person) = await accounts.SeedHouseholdAsync("Casa", "Ana");
        await SetAsync(accounts, household, HouseholdSections.Settings, Tokyo);
        await using var scheduling = await StartSchedulingAsync(accounts);
        await using var mount = await scheduling.MountAsync(household, person);

        (await mount.Backend.CreateAsync("/jonas/dentist/schedule.json",
                """{"prompt":"remind me of the dentist","runAt":"2030-01-01T09:00:00"}""", false, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        var status = (await mount.Backend.ReadAsync("/jonas/dentist/status.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content;

        (await scheduling.Store.GetAsync(household, "dentist", CancellationToken.None))!.RunAt
            .ShouldBe(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        status.ShouldContain("2030-01-01T09:00:00+09:00");
    }

    // The directories under /schedules are the calling household's own agents, as its configuration
    // names them: never the home's catalogue the agent registers with every channel, so a household is
    // not shown the home's agents nor refused a schedule for an agent it added. The prompt the server
    // serves — fetched for nobody and cached for every household — names no agent at all.
    [Fact]
    public async Task TheSchedulesMount_ShowsTheCallingHouseholdsOwnAgents_AndThePromptNamesNone()
    {
        await using var accounts = await StartAccountsAsync();
        var (household, person) = await accounts.SeedHouseholdAsync("Casa", "Ana");
        await SetAsync(accounts, household, HouseholdSections.Agents,
            """{ "agents": [ { "id": "chef", "template": "jonas", "name": "Chef" } ], "defaultAgent": "chef" }""");
        await using var scheduling = await StartSchedulingAsync(accounts);
        await using var mount = await scheduling.MountAsync(household, person);

        var listed = (await mount.Backend.GlobAsync("/", "*/", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value.Entries;
        var created = await mount.Backend.CreateAsync("/chef/soup/schedule.json",
            """{"prompt":"start the soup","runAt":"2030-01-01T09:00:00Z"}""", false, true, CancellationToken.None);
        var refused = await mount.Backend.CreateAsync("/jack/film/schedule.json",
            """{"prompt":"download a film","runAt":"2030-01-01T09:00:00Z"}""", false, true, CancellationToken.None);
        await using var client = await scheduling.ConnectAsync();
        var prompt = string.Concat((await client.GetPromptAsync(SchedulingPrompt.Name)).Messages
            .Select(m => m.Content).OfType<TextContentBlock>().Select(t => t.Text));

        listed.ShouldBe(["/chef/"]);
        created.ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        refused.ShouldBeOfType<FsResult<FsCreateResult>.Err>();
        prompt.ShouldNotContain("jack");
        prompt.ShouldNotContain("chef");
    }

    // A fire has no call behind it: the schedule's own delegation proves its household, whose zone the
    // cron's next run is read in.
    [Fact]
    public async Task ACronSchedule_FiresAndIsAdvancedInItsHouseholdsZone()
    {
        await using var accounts = await StartAccountsAsync();
        var (household, person) = await accounts.SeedHouseholdAsync("Casa", "Ana");
        await SetAsync(accounts, household, HouseholdSections.Settings, Tokyo);
        await using var scheduling = await StartSchedulingAsync(accounts);
        await using (var mount = await scheduling.MountAsync(household, person))
        {
            (await mount.Backend.CreateAsync("/jonas/news/schedule.json",
                    """{"prompt":"send me the news","cron":"0 8 * * *"}""", false, true, CancellationToken.None))
                .ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
            (await mount.Backend.ExecAsync("/jonas/news", "./run_now", null, CancellationToken.None))
                .ShouldBeOfType<FsResult<FsExecResult>.Ok>();
        }

        var fired = await scheduling.FireDueAsync();

        fired.ShouldHaveSingleItem().Origin.ShouldBe(new MessageOrigin(MessageOriginKind.Schedule, "news"));
        var schedule = (await scheduling.Store.GetAsync(household, "news", CancellationToken.None)).ShouldNotBeNull();
        schedule.Delegation.ShouldNotBeNull();
        var next = TimeZoneInfo.ConvertTimeFromUtc(schedule.NextRunAt!.Value, TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"));
        (next.Hour, next.Minute).ShouldBe((8, 0));
        schedule.NextRunAt.Value.ShouldBeGreaterThan(DateTime.UtcNow);
    }

    // Kept for ten minutes here, so only the save's announcement can reach the server this soon.
    [Fact]
    public async Task ASavedZone_ReachesTheServerWithoutARestart()
    {
        await using var accounts = await StartAccountsAsync();
        var (household, person) = await accounts.SeedHouseholdAsync("Casa", "Ana");
        await SetAsync(accounts, household, HouseholdSections.Settings, Tokyo);
        await using var scheduling = await StartSchedulingAsync(accounts);
        await using var mount = await scheduling.MountAsync(household, person);
        (await mount.Backend.CreateAsync("/jonas/dentist/schedule.json",
                """{"prompt":"remind me of the dentist","runAt":"2030-01-01T09:00:00+09:00"}""", false, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCreateResult>.Ok>();
        var before = await StatusAsync(mount.Backend, "/jonas/dentist");

        await SetAsync(accounts, household, HouseholdSections.Settings, """{ "name": "Casa", "timeZone": "Atlantic/Canary" }""");

        before.ShouldContain("2030-01-01T09:00:00+09:00");
        await Eventually.Until(async () => (await StatusAsync(mount.Backend, "/jonas/dentist")).Contains("2030-01-01T00:00:00+00:00"),
            "the saved zone to reach the scheduling server");
    }

    [Fact]
    public async Task APropertySearch_IsMadeWithTheCallingHouseholdsKeys()
    {
        await using var accounts = await StartAccountsAsync();
        var (casa, ana) = await accounts.SeedHouseholdAsync("Casa", "Ana");
        var (piso, luis) = await accounts.SeedHouseholdAsync("Piso", "Luis");
        await SetAsync(accounts, casa, HouseholdSections.Keys, Keys("casa-key", "casa-secret"));
        await SetAsync(accounts, piso, HouseholdSections.Keys, Keys("piso-key", "piso-secret"));
        await using var api = await IdealistaApi.StartAsync();
        await using var idealista = await StartIdealistaAsync(accounts, api);

        var forCasa = await idealista.SearchAsync(casa, ana);
        var forPiso = await idealista.SearchAsync(piso, luis);

        forCasa.IsError.ShouldNotBe(true);
        forPiso.IsError.ShouldNotBe(true);
        api.SignedInWith.ShouldBe([("casa-key", "casa-secret"), ("piso-key", "piso-secret")]);
    }

    // Idealista searches on the household's own account, and the operator's is no household's.
    [Fact]
    public async Task AHouseholdWithNoKeys_IsToldSoAndNothingIsSearched()
    {
        await using var accounts = await StartAccountsAsync();
        var (household, person) = await accounts.SeedHouseholdAsync("Casa", "Ana");
        await using var api = await IdealistaApi.StartAsync();
        await using var idealista = await StartIdealistaAsync(accounts, api);

        var result = await idealista.SearchAsync(household, person);

        result.IsError.ShouldBe(true);
        Text(result).ShouldContain("Idealista key");
        api.SignedInWith.ShouldBeEmpty();
    }

    // Idealista's server has no Redis to hear a save on, so a saved key reaches it when what it kept
    // runs out: a lifetime of a second here, a minute on the deployment.
    [Fact]
    public async Task SavedKeys_ReachTheIdealistaServerWithoutARestart()
    {
        await using var accounts = await StartAccountsAsync();
        var (household, person) = await accounts.SeedHouseholdAsync("Casa", "Ana");
        await SetAsync(accounts, household, HouseholdSections.Keys, Keys("old-key", "old-secret"));
        await using var api = await IdealistaApi.StartAsync();
        await using var idealista = await StartIdealistaAsync(accounts, api, lifetime: TimeSpan.FromSeconds(1));
        await idealista.SearchAsync(household, person);

        await SetAsync(accounts, household, HouseholdSections.Keys, Keys("new-key", "new-secret"));

        await Eventually.Until(async () =>
        {
            await idealista.SearchAsync(household, person);
            return api.SignedInWith.Contains(("new-key", "new-secret"));
        }, "the saved keys to reach the Idealista server");
    }

    private static string Keys(string key, string secret) =>
        $$"""{ "idealista": { "apiKey": "{{key}}", "secret": "{{secret}}" } }""";

    private static string Text(CallToolResult result) =>
        string.Join("", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private static async Task<string> StatusAsync(IFileSystemBackend backend, string schedule) =>
        (await backend.ReadAsync($"{schedule}/status.json", null, null, CancellationToken.None))
        .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content;

    private Task<AccountsServer> StartAccountsAsync() =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { [Scheduling] = McpTestSecret.Value, [Idealista] = McpTestSecret.Value },
            Redis = new() { ConnectionString = redis.ConnectionString }
        });

    private static async Task<ServerHost> StartIdealistaAsync(AccountsServer accounts, IdealistaApi api, TimeSpan? lifetime = null)
    {
        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureMcp(new IdealistaSettings.McpSettings
        {
            Idealista = new IdealistaSettings.IdealistaConfiguration { ApiUrl = api.Address },
            Mcp = McpTestSecret.Gate with { Audience = Idealista },
            Accounts = new AccountsClientConfig
            {
                BaseUrl = accounts.Address.ToString(), ConfigurationLifetime = lifetime ?? TimeSpan.FromMinutes(10)
            }
        });
        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        return new ServerHost(app, port, Idealista);
    }

    private async Task<ServerHost> StartSchedulingAsync(AccountsServer accounts, TimeSpan? lifetime = null)
    {
        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureScheduling(new SchedulingSettings
        {
            RedisConnectionString = redis.ConnectionString,
            DispatchIntervalSeconds = 3600,
            Delivery = TestDelegations.Delivery,
            Mcp = McpTestSecret.Gate with { Audience = Scheduling },
            Accounts = new AccountsClientConfig
            {
                BaseUrl = accounts.Address.ToString(), ConfigurationLifetime = lifetime ?? TimeSpan.FromMinutes(10)
            }
        });
        var app = builder.Build();
        app.MapMcp("/mcp");
        // The home's catalogue, as the agent registers it with every channel.
        app.Services.GetRequiredService<IMutableAgentCatalog>().Replace(
            [new AgentCatalogEntry("jack", "Jack", "downloads"), new AgentCatalogEntry("jonas", "Jonas", "test agent")]);
        await app.StartAsync();
        return new ServerHost(app, port, Scheduling);
    }

    private static async Task SetAsync(AccountsServer server, string household, string section, string document)
    {
        var (exit, output) = await server.RunAsync(server.Settings, document,
            AccountsCommand.SetHouseholdConfig, "--household", household, "--section", section, "--file", "-");
        exit.ShouldBe(0, output);
    }

    // A person's turn as the agent's conversation group leaves it: the identity it verified.
    private static ConversationContext TurnOf(string household, string person) =>
        new("jonas", "conv-1", person, new ReplyTarget(ChannelIds.SignalR, "conv-1"),
            Principal: new Principal(ChannelIds.SignalR, person, new Account(household, person)),
            Identity: IdentityClaims.Person(person, household, ChannelIds.SignalR, ["member"]));

    private sealed class ServerHost(WebApplication app, int port, string name) : IAsyncDisposable
    {
        public IServiceProvider Services => app.Services;

        public IScheduleStore Store => Services.GetRequiredService<IScheduleStore>();

        public string Endpoint => $"http://localhost:{port}/mcp";

        // The agent's client for this server, signing every call for the name the server answers to.
        public Task<McpClient> ConnectAsync() =>
            McpClient.CreateAsync(McpTestSecret.Transport(Endpoint, audience: TokenAudiences.CallTo(ServerAddress.Named(name))));

        public async Task<Mount> MountAsync(string household, string person)
        {
            var client = await ConnectAsync();
            return new Mount(client, new McpFileSystemBackend(client, "schedules", null).As(TurnOf(household, person)));
        }

        // A search for a flat to rent in Madrid, as a person's turn asks it.
        public async Task<CallToolResult> SearchAsync(string household, string person)
        {
            await using var client = await ConnectAsync();
            return await client.CallToolAsync(new CallToolRequestParams
            {
                Name = "property_search",
                Arguments = new Dictionary<string, JsonElement>
                {
                    ["country"] = JsonSerializer.SerializeToElement("es"),
                    ["operation"] = JsonSerializer.SerializeToElement("rent"),
                    ["propertyType"] = JsonSerializer.SerializeToElement("homes"),
                    ["locationId"] = JsonSerializer.SerializeToElement("0-EU-ES-28")
                },
                Meta = new JsonObject
                {
                    [ChannelProtocol.ConversationContextMetaKey] =
                        JsonSerializer.SerializeToNode(TurnOf(household, person), ChannelProtocol.SerializerOptions)
                }
            });
        }

        // One tick of the server's own dispatcher with the agent listening, as the agent's channel
        // connection drains what it fired.
        public async Task<IReadOnlyList<ChannelMessageNotification>> FireDueAsync()
        {
            const string agent = ChannelProtocol.ChannelClientNamePrefix + "scheduling";
            var inbox = Services.GetRequiredService<ChannelInbox>();
            await inbox.ReceiveAsync(agent, TimeSpan.Zero, CancellationToken.None);
            await Services.GetServices<IHostedService>().OfType<ScheduleDispatcherService>().Single()
                .DispatchDueAsync(CancellationToken.None);
            return (await inbox.ReceiveAsync(agent, TimeSpan.Zero, CancellationToken.None))
                .Where(item => item.Kind == ChannelInboxItemKind.Message).Select(item => item.Message!).ToList();
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            TestPort.Release(port);
        }
    }

    // Idealista's API as far as a search needs it: a token for whatever key and secret sign in, and an
    // empty page of results. It keeps which keys signed in.
    private sealed class IdealistaApi(WebApplication app, int port) : IAsyncDisposable
    {
        public string Address => $"http://localhost:{port}/";

        public IReadOnlyList<(string Key, string Secret)> SignedInWith =>
            [.. app.Services.GetRequiredService<ConcurrentQueue<(string, string)>>()];

        public static async Task<IdealistaApi> StartAsync()
        {
            var port = TestPort.GetAvailable();
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
            builder.Services.AddSingleton(new ConcurrentQueue<(string, string)>());
            var app = builder.Build();
            app.MapPost("/oauth/token", (HttpRequest request, ConcurrentQueue<(string, string)> signedIn) =>
            {
                var basic = Encoding.UTF8.GetString(Convert.FromBase64String(
                    request.Headers.Authorization.ToString()["Basic ".Length..])).Split(':', 2);
                signedIn.Enqueue((basic[0], basic[1]));
                return Results.Json(new { access_token = $"token-{basic[0]}", token_type = "bearer", expires_in = 3600 });
            });
            app.MapPost("/3.5/es/search", () => Results.Json(new
            {
                actualPage = 1,
                itemsPerPage = 20,
                total = 0,
                totalPages = 0,
                paginable = false,
                elementList = Array.Empty<object>()
            }));
            await app.StartAsync();
            return new IdealistaApi(app, port);
        }

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync();
            await app.DisposeAsync();
            TestPort.Release(port);
        }
    }

    private sealed record Mount(McpClient Client, IFileSystemBackend Backend) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }
}