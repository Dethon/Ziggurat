using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Accounts.Configuration;
using Accounts.Internal;
using Accounts.Settings;
using Accounts.Signing;
using Domain.Contracts;
using Domain.DTOs.Admin;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.Security;
using Domain.Security.Signing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Observability;
using Observability.Hubs;
using Observability.Services;
using Shouldly;
using StackExchange.Redis;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Accounts;

// A household's usage (seam 1 of Tenants 4c), as the owner's admin area reads it through the proxy:
// the accounts service's admin API asks the Observability host's usage route with an assertion it
// signs itself for the owner it verified, and that route answers the household the assertion names
// and no other. The Observability host here is its shipped gate and API over a real Redis, its
// assertions verified against the accounts service's published keys, its roll-ups written by the
// collector as events from two households arrive.
public class HouseholdUsageAdminTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string AppsCredential = "apps-credential";

    private static readonly DateTimeOffset _october = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AnOwner_ReadsTheirHouseholdsMonth_PerAgentAndPerson_WithItsLemonadeUse()
    {
        await using var stack = await UsageStack.StartAsync(redis);
        var (casa, ana) = await stack.Accounts.SeedHouseholdAsync("Casa", "Ana");
        await stack.SeedAsync(
            Tokens(casa, "jonas", "Jonas", "Ana", 100, 20, 0.30m),
            Tokens(casa, "jonas", "Jonas", "Luis", 50, 10, 0.10m),
            Tokens(casa, "sugary", "Sugary", "Ana", 5, 1, 0.01m),
            Tokens(casa, "jonas", "Jonas", "Ana", 999, 999, 9m, _october.AddMonths(-1)),
            Lemonade(casa, LemonadeUse.Transcription, 12.5),
            Lemonade(casa, LemonadeUse.Speech, 300),
            Lemonade(casa, LemonadeUse.Embedding, 4));

        var usage = await stack.UsageAsync(ana, "2026-10");

        usage.Month.ShouldBe("2026-10");
        usage.Agents.ShouldBe([new UsageLine("Jonas", 180, 0.40m), new UsageLine("Sugary", 6, 0.01m)]);
        usage.People.ShouldBe([new UsageLine("Ana", 126, 0.31m), new UsageLine("Luis", 60, 0.10m)]);
        usage.Lemonade.ShouldBe(new LemonadeUsage(12.5, 300, 4));
    }

    // A person is one line under their account whatever name their turns went by — renamed since, or
    // a memory call that carried only their id — and the line shows the name the household knows them
    // by now. A call from before accounts were carried stays under its name.
    [Fact]
    public async Task APersonsUsage_IsOneLineUnderTheirAccount_ShownByTheirNameNow()
    {
        await using var stack = await UsageStack.StartAsync(redis);
        var (casa, ana) = await stack.Accounts.SeedHouseholdAsync("Casa", "Ana");
        await stack.SeedAsync(
            Tokens(casa, "jonas", "Jonas", "Ana Before", 100, 20, 0.30m) with { UserId = ana },
            Tokens(casa, "jonas", "Jonas", ana, 5, 1, 0.01m) with { UserId = ana },
            Tokens(casa, "jonas", "Jonas", "Luis", 50, 10, 0.10m));

        var usage = await stack.UsageAsync(ana, "2026-10");

        usage.People.ShouldBe([new UsageLine("Ana", 126, 0.31m, ana), new UsageLine("Luis", 60, 0.10m)]);
    }

    // What the household's apps asked costs a line per app, on top of the person who asked and the agent that
    // answered. An app no published listing names (the apps feature off here) shows under its id.
    [Fact]
    public async Task AnAppsAsks_AreALineOfTheirOwn_AndThePersonWhoAskedPays()
    {
        await using var stack = await UsageStack.StartAsync(redis);
        var (casa, ana) = await stack.Accounts.SeedHouseholdAsync("Casa", "Ana");
        await stack.SeedAsync(
            Tokens(casa, "jonas", "Jonas", "Ana", 100, 20, 0.30m),
            Tokens(casa, "jonas", "Jonas", "Ana", 40, 2, 0.05m) with { Channel = AppChannels.Apps, AppId = "a1b2c3" },
            Tokens(casa, "jonas", "Jonas", "Luis", 10, 1, 0.02m) with { Channel = AppChannels.Apps, AppId = "a1b2c3" },
            Tokens(casa, "jonas", "Jonas", "Luis", 7, 0, 0.01m) with { Channel = AppChannels.Apps, AppId = "d4e5f6" });

        var usage = await stack.UsageAsync(ana, "2026-10");

        usage.Apps.ShouldBe([new UsageLine("a1b2c3", 53, 0.07m, "a1b2c3"), new UsageLine("d4e5f6", 7, 0.01m, "d4e5f6")]);
        usage.People.ShouldBe([new UsageLine("Ana", 162, 0.35m), new UsageLine("Luis", 18, 0.03m)]);
        usage.Agents.ShouldBe([new UsageLine("Jonas", 180, 0.38m)]);
    }

    // A published app's line shows under the name the household's apps list it by now.
    [Fact]
    public async Task APublishedAppsLine_IsShownByItsName()
    {
        await using var apps = await AppsServer.StartAsync(services: s =>
            s.Replace(ServiceDescriptor.Singleton<TimeProvider>(new FakeTimeProvider(AccountsServer.StartedAt))));
        await using var stack = await UsageStack.StartAsync(redis, $"http://localhost:{apps.Settings.Listeners.Mcp}");
        var (casa, ana) = await AppsHouseholdAsync(stack);
        var author = new Account(casa, ana);
        var glucose = AppsServer.IdIn(await apps.AppAsync(author, "glucose", "Glucose today"));
        await apps.CreateAsync(author, "/glucose/index.html", "<h1>Hi</h1>");
        await apps.ExecAsync(author, "/glucose", "./publish");
        await stack.SeedAsync(
            Tokens(casa, "jonas", "Jonas", "Ana", 40, 2, 0.05m) with { Channel = AppChannels.Apps, AppId = glucose },
            Tokens(casa, "jonas", "Jonas", "Ana", 7, 0, 0.01m) with { Channel = AppChannels.Apps, AppId = "gone00" });

        var usage = await stack.UsageAsync(ana, "2026-10");

        usage.Apps.ShouldBe([new UsageLine("Glucose today", 42, 0.05m, glucose), new UsageLine("gone00", 7, 0.01m, "gone00")]);
    }

    // The apps server not answering costs only the apps' names: the figures are still the household's.
    [Fact]
    public async Task WithTheAppsServerDown_AnAppsLineKeepsItsId()
    {
        await using var stack = await UsageStack.StartAsync(redis, DeadEnd.Url());
        var (casa, ana) = await AppsHouseholdAsync(stack);
        await stack.SeedAsync(
            Tokens(casa, "jonas", "Jonas", "Ana", 40, 2, 0.05m) with { Channel = AppChannels.Apps, AppId = "a1b2c3" });

        var usage = await stack.UsageAsync(ana, "2026-10");

        usage.Apps.ShouldBe([new UsageLine("a1b2c3", 42, 0.05m, "a1b2c3")]);
    }

    // Each owner reads their own household, whoever asks for what: the admin route takes no household,
    // and the Observability route believes the household its assertion names over anything the query
    // string says.
    [Fact]
    public async Task AnOwner_NeverSeesAnotherHouseholdsUsage()
    {
        await using var stack = await UsageStack.StartAsync(redis);
        var (casa, ana) = await stack.Accounts.SeedHouseholdAsync("Casa", "Ana");
        var (otra, eve) = await stack.Accounts.SeedHouseholdAsync("Otra", "Eve");
        await stack.SeedAsync(
            Tokens(casa, "jonas", "Jonas", "Ana", 10, 0, 0.01m),
            Lemonade(casa, LemonadeUse.Speech, 30),
            Tokens(otra, "spy", "Spy", "Eve", 7000, 0, 7m),
            Lemonade(otra, LemonadeUse.Embedding, 70));

        var anas = await stack.UsageAsync(ana, "2026-10");
        var eves = await stack.UsageAsync(eve, "2026-10");

        anas.Agents.ShouldBe([new UsageLine("Jonas", 10, 0.01m)]);
        anas.People.ShouldBe([new UsageLine("Ana", 10, 0.01m)]);
        anas.Lemonade.ShouldBe(new LemonadeUsage(0, 30, 0));
        eves.Agents.ShouldBe([new UsageLine("Spy", 7000, 7m)]);
        eves.Lemonade.ShouldBe(new LemonadeUsage(0, 0, 70));

        var asked = await stack.ObservabilityAsync(
            $"{HouseholdUsage.Path}?household={otra}&from=2026-10-01&to=2026-10-31",
            stack.Accounts.Service<Assertions>().Of(new ForwardedAccount(ana, casa, [ForwardedAccount.OwnerRole]), AppChannels.Usage));
        asked.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await asked.Content.ReadFromJsonAsync<HouseholdUsage>())!.Agents.ShouldBe([new UsageLine("Jonas", 10, 0.01m)]);
    }

    // The Observability host's usage route believes the accounts service's usage assertion for an
    // owner and nothing else: not an operator's dashboard assertion, not the admin one the proxy hands
    // the accounts service, not a member's; and a usage assertion opens none of the operator's routes.
    [Fact]
    public async Task TheUsageRoute_BelievesOnlyAnOwnersUsageAssertion()
    {
        await using var stack = await UsageStack.StartAsync(redis);
        var (casa, ana) = await stack.Accounts.SeedHouseholdAsync("Casa", "Ana");
        var assertions = stack.Accounts.Service<Assertions>();
        var owner = new ForwardedAccount(ana, casa, [ForwardedAccount.OwnerRole]);
        var theOperator = new ForwardedAccount(ana, casa, [ForwardedAccount.OwnerRole, ForwardedAccount.OperatorRole]);
        var member = new ForwardedAccount(ana, casa, ["member"]);

        (await stack.ObservabilityAsync(HouseholdUsage.Path, null)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await stack.ObservabilityAsync(HouseholdUsage.Path, assertions.Of(theOperator, AppChannels.Dashboard)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await stack.ObservabilityAsync(HouseholdUsage.Path, assertions.Of(owner, AppChannels.Admin)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await stack.ObservabilityAsync(HouseholdUsage.Path, assertions.Of(member, AppChannels.Usage)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await stack.ObservabilityAsync("/api/metrics/summary", assertions.Of(theOperator, AppChannels.Usage)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await stack.ObservabilityAsync(HouseholdUsage.Path, assertions.Of(owner, AppChannels.Usage)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AMember_IsRefusedTheUsage_AndSomebodySignedOutIsAskedToSignIn()
    {
        await using var stack = await UsageStack.StartAsync(redis);
        var (casa, _) = await stack.Accounts.SeedHouseholdAsync("Casa", "Ana");
        var luis = await stack.Accounts.SeedPersonAsync(casa, "Luis");

        (await stack.Accounts.Admin(await stack.Accounts.MintSessionAsync(luis)).GetAsync(AdminProtocol.Usage))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await stack.Accounts.Admin(session: null).GetAsync(AdminProtocol.Usage)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // Left out, the month is the current one on the service's clock (late October 2026 here).
    [Fact]
    public async Task WithNoMonthNamed_TheCurrentMonthIsRead()
    {
        await using var stack = await UsageStack.StartAsync(redis);
        var (casa, ana) = await stack.Accounts.SeedHouseholdAsync("Casa", "Ana");
        await stack.SeedAsync(Tokens(casa, "jonas", "Jonas", "Ana", 3, 0, 0m));

        var usage = await stack.UsageAsync(ana, month: null);

        usage.Month.ShouldBe("2026-10");
        usage.Agents.ShouldBe([new UsageLine("Jonas", 3, 0m)]);
    }

    [Fact]
    public async Task AMonthThatIsNotOne_IsRefusedInWords()
    {
        await using var stack = await UsageStack.StartAsync(redis);
        var (_, ana) = await stack.Accounts.SeedHouseholdAsync("Casa", "Ana");

        var refused = await stack.Accounts.Admin(await stack.Accounts.MintSessionAsync(ana)).GetAsync($"{AdminProtocol.Usage}?month=octubre");

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await refused.Content.ReadFromJsonAsync<AdminRefusal>())!.Message.ShouldContain("yyyy-MM");
    }

    // The figures are the Observability host's: with it down the page says they could not be read,
    // never that the household used nothing.
    [Fact]
    public async Task WithTheObservabilityHostDown_TheUsageIsUnavailable()
    {
        var nowhere = TestPort.GetAvailable();
        try
        {
            await using var accounts = await AccountsServer.StartAsync(s => s with { ObservabilityUrl = $"http://localhost:{nowhere}" });
            var (_, ana) = await accounts.SeedHouseholdAsync("Casa", "Ana");

            var answer = await accounts.Admin(await accounts.MintSessionAsync(ana)).GetAsync(AdminProtocol.Usage);

            answer.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await answer.Content.ReadFromJsonAsync<AdminRefusal>())!.Message.ShouldNotBeNullOrWhiteSpace();
        }
        finally
        {
            TestPort.Release(nowhere);
        }
    }

    // A household the operator gave the apps feature, and its owner.
    private static async Task<(string Household, string Owner)> AppsHouseholdAsync(UsageStack stack)
    {
        var (household, owner) = await stack.Accounts.SeedHouseholdAsync("Casa", "Ana");
        await stack.Accounts.Service<HouseholdConfigStore>().SaveAsync(
            household, HouseholdSections.FeaturesSchema, new JsonObject { ["apps"] = true }, CancellationToken.None);
        return (household, owner);
    }

    private static TokenUsageEvent Tokens(
        string household, string agentId, string agentName, string sender, int input, int output, decimal cost,
        DateTimeOffset? at = null) => new()
        {
            TenantId = household, AgentId = agentId, AgentName = agentName, Sender = sender, Model = "z-ai/glm-5",
            InputTokens = input, OutputTokens = output, Cost = cost, Timestamp = at ?? _october
        };

    private static LemonadeUsageEvent Lemonade(string household, LemonadeUse use, double amount) =>
        new() { TenantId = household, Use = use, Amount = amount, Timestamp = _october };

    // The accounts service and the Observability host as compose wires them: the accounts service's
    // ObservabilityUrl at the host, the host's verifier over the service's published keys, both on
    // the accounts service's clock.
    private sealed class UsageStack(AccountsServer accounts, WebApplication observability, int port, RedisFixture redis)
        : IAsyncDisposable
    {
        public AccountsServer Accounts => accounts;

        // appsUrl: where the apps server whose listing names the household's published apps listens, which then
        // verifies the service's assertions and reads its households' configuration as compose wires it.
        public static async Task<UsageStack> StartAsync(RedisFixture redis, string? appsUrl = null)
        {
            var port = TestPort.GetAvailable();
            var accounts = await AccountsServer.StartAsync(s => appsUrl is null
                ? s with { ObservabilityUrl = $"http://localhost:{port}" }
                : s with
                {
                    ObservabilityUrl = $"http://localhost:{port}",
                    Callers = new Dictionary<string, string> { ["mcp-apps"] = AppsCredential },
                    Redis = new() { ConnectionString = redis.ConnectionString },
                    Signing = new SigningKeySettings { PrivateKey = TestSigningKeys.Accounts },
                    AppsUrl = appsUrl
                });

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseKestrel(kestrel => kestrel.ListenLocalhost(port));
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(redis.Connection);
            builder.Services.AddSingleton<TimeProvider>(accounts.Clock);
            builder.Services.AddSingleton<MetricsQueryService>();
            builder.Services.AddOperatorGate(new Uri(accounts.Address, InternalApi.JwksPath).ToString(), new TokenVerificationSettings());
            var app = builder.Build();
            app.UseOperatorGate();
            app.UseRouting();
            app.MapMetricsApi();
            await app.StartAsync();
            return new UsageStack(accounts, app, port, redis);
        }

        public async Task SeedAsync(params MetricEvent[] events)
        {
            var collector = new MetricsCollectorService(
                redis.Connection, Hub(), NullLogger<MetricsCollectorService>.Instance, accounts.Clock);
            foreach (var evt in events)
            {
                await collector.ProcessEventAsync(evt, redis.Connection.GetDatabase());
            }
        }

        public async Task<AdminUsage> UsageAsync(string userId, string? month)
        {
            var admin = accounts.Admin(await accounts.MintSessionAsync(userId));
            var answer = await admin.GetAsync(month is null ? AdminProtocol.Usage : $"{AdminProtocol.Usage}?month={month}");
            answer.StatusCode.ShouldBe(HttpStatusCode.OK);
            answer.Headers.CacheControl!.NoStore.ShouldBeTrue();
            return (await answer.Content.ReadFromJsonAsync<AdminUsage>())!;
        }

        public async Task<HttpResponseMessage> ObservabilityAsync(string path, string? assertion)
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (assertion is not null)
            {
                request.Headers.Add(ForwardedAccount.AssertionHeader, assertion);
            }

            return await http.SendAsync(request);
        }

        private static IHubContext<MetricsHub> Hub()
        {
            var clients = new Mock<IHubClients>();
            clients.Setup(c => c.All).Returns(Mock.Of<IClientProxy>());
            var hub = new Mock<IHubContext<MetricsHub>>();
            hub.Setup(h => h.Clients).Returns(clients.Object);
            return hub.Object;
        }

        public async ValueTask DisposeAsync()
        {
            await observability.StopAsync();
            await observability.DisposeAsync();
            TestPort.Release(port);
            await accounts.DisposeAsync();
        }
    }
}