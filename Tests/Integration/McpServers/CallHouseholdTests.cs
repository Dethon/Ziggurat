using System.Net;
using Accounts.Commands;
using Domain.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.DTOs.Household;
using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.Agents.Mcp;
using Infrastructure.Clients.Accounts;
using Mcp.Hosting;
using McpServerScheduling.Modules;
using McpServerScheduling.Services;
using McpServerScheduling.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServers;

// A shared server reading its section of the calling household's configuration (seam 4): a real
// server host, called as the agent calls it, asking a real accounts service with what the call
// proved — never with a household it picked — and seeing a saved section without a restart.
public class CallHouseholdTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string Scheduling = "mcp-scheduling";

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

    private static async Task<string> StatusAsync(IFileSystemBackend backend, string schedule) =>
        (await backend.ReadAsync($"{schedule}/status.json", null, null, CancellationToken.None))
        .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value.Content;

    private Task<AccountsServer> StartAccountsAsync() =>
        AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { [Scheduling] = McpTestSecret.Value },
            Redis = new() { ConnectionString = redis.ConnectionString }
        });

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
        app.Services.GetRequiredService<IMutableAgentCatalog>().Replace([new AgentCatalogEntry("jonas", "Jonas", "test agent")]);
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

    private sealed record Mount(McpClient Client, IFileSystemBackend Backend) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }
}