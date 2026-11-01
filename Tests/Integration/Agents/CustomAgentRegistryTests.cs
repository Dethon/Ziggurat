using System.Net.Http.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Prompts;
using Domain.Security;
using global::Agent.App;
using Infrastructure.Agents;
using Infrastructure.StateManagers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

// A custom agent is registered through the agent API and kept in Redis under the household and the
// person the verified assertion names, so a restarted agent still has it and nobody else ever does.
// A restart is a new registry over the same Redis, started as the host starts it.
public class CustomAgentRegistryTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private const string Secret = "s3cret";

    private static readonly CustomAgentRegistration _registration = new()
    {
        Name = "Helper",
        Description = "Helps",
        Model = "some/model",
        McpServerEndpoints = ["vault"],
        WhitelistPatterns = ["mcp:vault:*"],
        CustomInstructions = "Be brief.",
        EnabledFeatures = ["memory"],
        ProviderRouting = new ProviderRouting { Sort = ProviderSort.Throughput, Only = ["deepinfra"] }
    };

    private readonly Account _alice = new($"home-{Guid.NewGuid():N}", "alice");

    [Fact]
    public async Task AnAgentRegisteredThroughTheApi_IsStillListedAfterARestart()
    {
        AgentCatalogEntry registered;
        await using (var before = await StartAsync())
        {
            registered = await RegisterAsync(before.GetTestClient(), _alice);
        }

        await using var after = await StartAsync();

        (await ListAsync(after.GetTestClient(), _alice)).ShouldContain(registered);
    }

    // What comes back is the agent that was registered, every field of it — a household server it
    // names by name included, which a turn resolves against its own household — and the agent is
    // built for its owner's turn.
    [Fact]
    public async Task AnAgentAfterARestart_IsTheOneRegistered_AndBuildsForItsOwnersTurn()
    {
        await using (var before = await StartAsync())
        {
            await RegisterAsync(before.GetTestClient(), _alice);
        }

        var definitions = await RestartedAsync();
        var restored = (await definitions.GetAllAsync(null, _alice)).Where(a => a.Id.StartsWith("custom-")).ShouldHaveSingleItem();

        restored.Name.ShouldBe("Helper");
        restored.Description.ShouldBe("Helps");
        restored.Model.ShouldBe("some/model");
        restored.McpServerEndpoints.ShouldBe(["vault"]);
        restored.WhitelistPatterns.ShouldBe(["mcp:vault:*"]);
        restored.CustomInstructions.ShouldBe("Be brief.");
        restored.EnabledFeatures.ShouldBe(["memory"]);
        restored.ProviderRouting.ShouldNotBeNull().Sort.ShouldBe(ProviderSort.Throughput);
        restored.ProviderRouting.Only.ShouldBe(["deepinfra"]);
        await using var agent = await Factory(definitions).CreateAsync(
            new AgentKey("conv-1", restored.Id), "Alice", restored.Id, Mock.Of<IToolApprovalHandler>(), _alice);
    }

    // The same person id in another household is somebody else, and so is another person of the
    // household: neither finds the agent by its id, lists it or deletes it, and a turn of theirs
    // naming it is refused as an agent nobody configured.
    [Fact]
    public async Task AnAgentsId_NeverResolvesForAnotherHouseholdOrPerson()
    {
        AgentCatalogEntry registered;
        Account[] others = [_alice with { TenantId = $"other-{Guid.NewGuid():N}" }, _alice with { UserId = "bob" }];
        await using (var app = await StartAsync())
        {
            var client = app.GetTestClient();
            registered = await RegisterAsync(client, _alice);
            foreach (var other in others)
            {
                (await ListAsync(client, other)).ShouldNotContain(registered);
                (await DeleteAsync(client, other, registered.Id)).ShouldBeFalse();
            }
        }

        var definitions = await RestartedAsync();
        var factory = Factory(definitions);

        (await definitions.GetByIdAsync(registered.Id, null, _alice)).ShouldNotBeNull();
        (await definitions.GetByIdAsync(registered.Id, null)).ShouldBeNull();
        foreach (var other in others)
        {
            (await definitions.GetByIdAsync(registered.Id, null, other)).ShouldBeNull();
            await Should.ThrowAsync<InvalidOperationException>(() => factory.CreateAsync(
                new AgentKey("conv-1", registered.Id), "alice", registered.Id, Mock.Of<IToolApprovalHandler>(), other));
        }
    }

    [Fact]
    public async Task AnAgentItsOwnerDeleted_StaysDeletedAfterARestart()
    {
        await using (var app = await StartAsync())
        {
            var client = app.GetTestClient();
            var registered = await RegisterAsync(client, _alice);
            (await DeleteAsync(client, _alice, registered.Id)).ShouldBeTrue();
        }

        (await (await RestartedAsync()).GetAllAsync(null, _alice)).ShouldNotContain(a => a.Id.StartsWith("custom-"));
    }

    private async Task<AgentDefinitionProvider> RestartedAsync()
    {
        var registry = new CustomAgentRegistry(new RedisCustomAgentStore(redis.Connection));
        await registry.StartAsync(CancellationToken.None);
        return FixedHouseholdAgents.Provider(registry, BuiltIn);
    }

    private static MultiAgentFactory Factory(IAgentDefinitionProvider definitions) =>
        new(
            new ServiceCollection().AddSingleton(Mock.Of<IThreadStateStore>()).BuildServiceProvider(),
            definitions,
            new OpenRouterConfig { ApiUrl = "http://test", ApiKey = "test-key" },
            Mock.Of<IDomainToolRegistry>(r =>
                r.GetToolsForFeatures(It.IsAny<IEnumerable<string>>(), It.IsAny<FeatureConfig>()) == Enumerable.Empty<AIFunction>()
                && r.GetPromptsForFeatures(It.IsAny<IEnumerable<string>>()) == Enumerable.Empty<PromptSection>()));

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IAgentDefinitionProvider>(await RestartedAsync());
        builder.Services.AddSingleton(TestAssertions.Verifier());

        var app = builder.Build();
        app.MapAgents(Secret);
        await app.StartAsync();
        return app;
    }

    private static readonly AgentDefinition BuiltIn =
        new() { Id = "jonas", Name = "Jonas", Model = "m", McpServerEndpoints = [] };

    private static async Task<AgentCatalogEntry> RegisterAsync(HttpClient client, Account person)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Post, "/api/agents", person,
            JsonContent.Create(_registration)));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AgentCatalogEntry>())!;
    }

    private static async Task<AgentCatalogEntry[]> ListAsync(HttpClient client, Account person)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Get, "/api/agents", person));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<AgentCatalogEntry[]>())!;
    }

    private static async Task<bool> DeleteAsync(HttpClient client, Account person, string agentId)
    {
        using var response = await client.SendAsync(Request(HttpMethod.Delete, $"/api/agents/{agentId}", person));
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<bool>();
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, Account person, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + Secret);
        request.Headers.Add(ForwardedAccount.AssertionHeader, TestAssertions.Of(person, AppChannels.SexyTime));
        return request;
    }
}