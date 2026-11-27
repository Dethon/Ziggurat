using System.Net;
using System.Text.Json.Nodes;
using Accounts.Configuration;
using Accounts.Settings;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Admin;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.Security.Signing;
using Domain.Tools;
using Infrastructure.Clients.Accounts;
using Infrastructure.Clients.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

// An owner's caps from end to end, against a real accounts service on a real Redis (seam 1, the origin): the
// owner saves them on the apps page's route, the apps server reads them on the viewer's own `apps` assertion
// at each ask and keeps them, and a save it hears is read again. Both servers run on the accounts service's
// clock, which stands still, so nothing it kept runs out: only the save's announcement explains a new limit.
public class AppAskCapsOverTheWireTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private static readonly TimeSpan _wait = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task AnOwnersCaps_AreTheLimitAnAskMeets_ReadAgainOnceASaveIsHeard()
    {
        var appsPort = TestPort.GetAvailable();
        await using var accounts = await AccountsServer.StartAsync(s => s with
        {
            AppsDomain = AppsServer.AppsDomain,
            Signing = new SigningKeySettings { PrivateKey = TestSigningKeys.Accounts },
            Callers = new Dictionary<string, string> { ["mcp-apps"] = McpTestSecret.Gate.SharedSecret },
            Redis = new RedisSettings { ConnectionString = redis.ConnectionString },
            AppsUrl = $"http://localhost:{appsPort}"
        });
        await using var apps = await AppsServer.StartAsync(
            s => s with
            {
                PublicBaseUrl = accounts.Address.ToString().TrimEnd('/'),
                Accounts = s.Accounts with { BaseUrl = accounts.Address.ToString() },
                RedisConnectionString = redis.ConnectionString
            },
            services =>
            {
                services.Replace(ServiceDescriptor.Singleton<TimeProvider>(accounts.Clock));
                services.Replace(ServiceDescriptor.Singleton<IHouseholdConfiguration>(sp =>
                    new HouseholdConfigFromAccounts(sp.GetRequiredService<HouseholdConfigurationClient>())));
            },
            appsPort);
        var agent = await apps.AgentAsync();
        var fran = await accounts.MintOperatorAsync("Fran");
        await accounts.Service<HouseholdConfigStore>().SaveAsync(
            fran.TenantId, HouseholdSections.FeaturesSchema, new JsonObject { ["apps"] = true }, CancellationToken.None);
        var author = new Account(fran.TenantId, fran.UserId);
        var id = AppsServer.IdIn(await apps.AppAsync(author, "glucose"));
        await apps.CreateAsync(author, "/glucose/app.json",
            """{"name":"Glucose today","accentColor":"#e9601f","description":"My glucose today.","instructions":"asks/glucose.md"}""",
            overwrite: true);
        await apps.CreateAsync(author, "/glucose/index.html", "<h1>Glucose</h1>");
        await apps.CreateAsync(author, "/glucose/asks/glucose.md", "Answer in one word.");
        await apps.ExecAsync(author, "/glucose", "./publish");
        var host = apps.Published("glucose", id);
        var admin = accounts.Admin(await accounts.MintSessionAsync(fran.UserId));
        var session = (await apps.SignInThroughAsync(accounts, host, "/", await accounts.MintSessionAsync(fran.UserId))).Token;
        (await admin.PutAsync(AdminProtocol.AppAsks, new AppAsksSection { DailyCap = 0 })).StatusCode.ShouldBe(HttpStatusCode.OK);

        // The household's default, as no cap of the app's own is set: kept by the apps server from here on.
        using (var none = await apps.AskAsync(host, """{"text":"Hi"}""", session))
        {
            await ShouldBeCappedAtAsync(none, 0);
        }

        var raised = await admin.PutAsync(AdminProtocol.AppAsks,
            new AppAsksSection { DailyCap = 0, Apps = new Dictionary<string, int> { [id] = 1 } });

        raised.StatusCode.ShouldBe(HttpStatusCode.OK, await raised.Content.ReadAsStringAsync());
        await Eventually.Until(async () => await AnsweredAsync(apps, agent, host, session), "the heard save to raise the app's own cap");
        using var capped = await apps.AskAsync(host, """{"text":"Hi"}""", session);
        await ShouldBeCappedAtAsync(capped, 1);
    }

    // An ask the app's cap of none refuses, or one sent and answered: never anything else.
    private static async Task<bool> AnsweredAsync(AppsServer apps, McpChannelConnection agent, string host, string session)
    {
        using var turnWait = new CancellationTokenSource(_wait);
        var answer = apps.AskAsync(host, """{"text":"Hi"}""", session);
        var turn = agent.Messages.FirstAsync(turnWait.Token).AsTask();
        if (await Task.WhenAny(answer, turn) == answer)
        {
            await turnWait.CancelAsync();
            await Should.ThrowAsync<OperationCanceledException>(turn);
            using var refused = await answer;
            await ShouldBeCappedAtAsync(refused, 0);
            return false;
        }

        var sent = await turn;
        await agent.SendReplyAsync(Reply(sent, ReplyContentType.Text, "Quietly."), CancellationToken.None);
        await agent.SendReplyAsync(Reply(sent, ReplyContentType.StreamComplete, "", isComplete: true), CancellationToken.None);
        using var response = await answer;
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return true;
    }

    private static SendReplyParams Reply(ChannelMessage turn, ReplyContentType type, string content, bool isComplete = false) => new()
    {
        ConversationId = turn.ConversationId,
        Content = content,
        ContentType = type,
        IsComplete = isComplete,
        MessageId = "resp_1",
        TurnKey = turn.TurnKey,
        AgentInitiated = false
    };

    private static async Task ShouldBeCappedAtAsync(HttpResponseMessage response, int limit)
    {
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, body);
        var envelope = JsonNode.Parse(body)!;
        envelope["errorCode"]!.GetValue<string>().ShouldBe(ToolError.Codes.QuotaExceeded);
        envelope["message"]!.GetValue<string>().ShouldContain($"daily limit of {limit} asks");
    }
}