using Accounts.Commands;
using Domain.DTOs.Channel;
using Domain.DTOs.Household;
using Domain.DTOs.Tunnel;
using Domain.Security;
using Domain.Security.Signing;
using Infrastructure.Agents.Mcp;
using Infrastructure.Clients;
using Mcp.Hosting;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;
using Tests.Integration.McpServerTests;
using Tests.Unit;

namespace Tests.Integration.Tunnels;

// The whole way a kit's search finds its key, each end the real thing: the agent's signed call through
// the household's tunnel, the server's own gate, its accounts back-call through the relay, the accounts
// service proving it from the credential and the token, and Brave asked with what an owner saved.
public class TunnelledWebSearchKeyTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string AgentCredential = "agent-credential";
    private const string SavedKey = "BSA-brave-key-the-owner-saved";

    [Fact]
    public async Task ASearchThroughItsTunnel_AsksBraveWithTheKeyItsOwnerSaved()
    {
        await using var accounts = await AccountsServer.StartAsync(s => s with
        {
            Callers = new Dictionary<string, string> { ["agent"] = AgentCredential },
            Redis = new() { ConnectionString = redis.ConnectionString }
        });
        var (household, ana) = await accounts.SeedHouseholdAsync("Casa", "Ana");
        (await accounts.RunAsync(accounts.Settings, $$"""{ "braveSearch": { "apiKey": "{{SavedKey}}" } }""",
                AccountsCommand.SetHouseholdConfig, "--household", household, "--section", HouseholdSections.Keys, "--file", "-"))
            .Exit.ShouldBe(0);
        var credential = await accounts.MintTunnelCredentialAsync(household, HouseholdServerNames.WebSearch);
        await using var relay = await RelayHost.StartAsync(accounts, AgentCredential, redis.ConnectionString,
            TestSigningKeys.Store(TestSigningKeys.Agent), new RecordingMetricsPublisher());
        var brave = new McpWebSearchHouseholdKeyTests.RecordingBrave();
        var backCall = TestPort.GetAvailable();
        try
        {
            await using var server = await WebIdentityServerHarness.StartAsync(
                configure: services =>
                    services.AddHttpClient(BraveSearchClients.ClientName).ConfigurePrimaryHttpMessageHandler(() => brave),
                settings: s => s with
                {
                    Mcp = new McpGateSettings
                    {
                        Tunnel = TunnelledServer.Quick(relay.Hub, credential.Credential, HouseholdServerNames.WebSearch) with
                        {
                            BackCalls = new() { [BackCallProtocol.Accounts] = backCall }
                        }
                    },
                    Accounts = new() { BaseUrl = $"http://127.0.0.1:{backCall}" }
                });
            await Eventually.Until(
                () => relay.Registry.Find(household, HouseholdServerNames.WebSearch) is not null, "web search's tunnel opens");
            await using var agent = await McpClientManager.CreateAsync(
                "jonas", ana, "test", [relay.OwnServers.Resolve(household, HouseholdServerNames.WebSearch)!],
                new McpClientHandlers(), callTokens: McpTestSecret.CallTokens);
            var person = new Account(household, ana);
            var turn = WebIdentityServerHarness.FromChatClient(person) with
            {
                Identity = IdentityClaims.Person(ana, household, ChannelIds.SignalR, [])
            };

            var result = await agent.Clients.Single().CallToolAsync(new CallToolRequestParams
            {
                Name = "web_search",
                Arguments = new Dictionary<string, System.Text.Json.JsonElement>
                {
                    ["query"] = System.Text.Json.JsonSerializer.SerializeToElement("tiempo")
                },
                Meta = WebIdentityServerHarness.MetaFor(turn)
            });

            result.IsError.ShouldNotBe(true, string.Concat(result.Content.OfType<TextContentBlock>().Select(t => t.Text)));
            brave.Tokens.ShouldBe([SavedKey]);
        }
        finally
        {
            TestPort.Release(backCall);
        }
    }
}