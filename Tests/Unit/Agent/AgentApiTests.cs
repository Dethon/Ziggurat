using System.Net;
using System.Net.Http.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Security;
using global::Agent.App;
using global::Agent.Settings;
using Infrastructure.Agents;
using Infrastructure.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Shouldly;

namespace Tests.Unit.Agent;

// The custom-agent registration API answers anyone who can reach the agent's port — through the
// public proxy, the whole internet — so every verb asks for the shared secret before it reads a
// catalogue or writes into one. Asserted per verb because a gate on the group is one line away
// from a gate on two of the three routes, and a refusal is asserted with the provider untouched:
// a 401 that was decided after the write is no gate at all.
public class AgentApiTests
{
    private const string Secret = "s3cret";
    private const string Home = "home";
    private const string UnsetAssertion = "(signed for the user)";
    private static readonly Account Alice = new(Home, "alice");

    private static readonly CustomAgentRegistration _registration = new()
    {
        Name = "Helper",
        Model = "some/model",
        McpServerEndpoints = []
    };

    private static readonly AgentDefinition _registered = new()
    {
        Id = "custom-1",
        Name = "Helper",
        Description = "Helps",
        Model = "some/model",
        McpServerEndpoints = []
    };

    // No header, the wrong secret, and the right-looking header on a deployment that never set
    // one: an unset secret refuses everything rather than admitting anyone.
    public static TheoryData<string, string?, string> Refusals =>
        new[] { "GET", "POST", "DELETE" }.Aggregate(
            new TheoryData<string, string?, string>(), (data, verb) =>
            {
                data.Add(verb, null, Secret);
                data.Add(verb, "Bearer wrong", Secret);
                data.Add(verb, "Bearer " + Secret, "");
                return data;
            });

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task AVerbWithoutTheConfiguredSecret_IsUnauthorizedAndTouchesNothing(
        string verb, string? authorization, string configured)
    {
        var provider = new Mock<IAgentDefinitionProvider>(MockBehavior.Strict);
        await using var app = await StartAsync(provider.Object, configured);
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Request(verb, authorization));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        provider.VerifyNoOtherCalls();
    }

    // The person is the one SexyTime's server forwards from the signed-in request it is answering.
    // A userId in the query names whoever the caller likes, so it is never read: each request below
    // also names mallory there.
    [Fact]
    public async Task Get_WithTheSecret_ListsTheForwardedPersonsCatalogue()
    {
        var provider = new Mock<IAgentDefinitionProvider>(MockBehavior.Strict);
        provider.Setup(p => p.GetAll("alice")).Returns([_registered]);
        await using var app = await StartAsync(provider.Object, Secret);
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Request("GET", "Bearer " + Secret, user: "alice"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var catalogue = await response.Content.ReadFromJsonAsync<AgentCatalogEntry[]>();
        catalogue.ShouldBe([new AgentCatalogEntry("custom-1", "Helper", "Helps")]);
    }

    [Fact]
    public async Task Post_WithTheSecret_RegistersTheAgentForTheForwardedPerson()
    {
        var provider = new Mock<IAgentDefinitionProvider>(MockBehavior.Strict);
        provider.Setup(p => p.RegisterCustomAgent("alice", It.IsAny<CustomAgentRegistration>()))
            .Returns(_registered);
        await using var app = await StartAsync(provider.Object, Secret);
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Request("POST", "Bearer " + Secret, user: "alice"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<AgentCatalogEntry>())
            .ShouldBe(new AgentCatalogEntry("custom-1", "Helper", "Helps"));
        provider.Verify(p => p.RegisterCustomAgent(
            "alice", It.Is<CustomAgentRegistration>(r => r.Name == "Helper")), Times.Once);
    }

    [Fact]
    public async Task Delete_WithTheSecret_UnregistersTheForwardedPersonsAgent()
    {
        var provider = new Mock<IAgentDefinitionProvider>(MockBehavior.Strict);
        provider.Setup(p => p.UnregisterCustomAgent("alice", "custom-1")).Returns(true);
        await using var app = await StartAsync(provider.Object, Secret);
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Request("DELETE", "Bearer " + Secret, user: "alice"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<bool>()).ShouldBeTrue();
        provider.Verify(p => p.UnregisterCustomAgent("alice", "custom-1"), Times.Once);
    }

    // Through the real catalogue: what alice registers, bob neither lists nor takes back, and alice
    // still has it afterwards.
    [Fact]
    public async Task APersonsCustomAgent_IsListedAndDeletableOnlyByThem()
    {
        var builtIn = new Mock<IOptionsMonitor<AgentRegistryOptions>>();
        builtIn.Setup(o => o.CurrentValue).Returns(new AgentRegistryOptions { Agents = [] });
        await using var app = await StartAsync(new AgentDefinitionProvider(builtIn.Object, new CustomAgentRegistry()), Secret);
        using var client = app.GetTestClient();

        using var registered = await client.SendAsync(Request("POST", "Bearer " + Secret, user: "alice"));
        var agent = (await registered.Content.ReadFromJsonAsync<AgentCatalogEntry>())!;

        (await ListAsync(client, "bob")).ShouldBeEmpty();
        using var bobsDelete = await client.SendAsync(Request("DELETE", "Bearer " + Secret, user: "bob", agentId: agent.Id));
        (await bobsDelete.Content.ReadFromJsonAsync<bool>()).ShouldBeFalse();
        (await ListAsync(client, "alice")).ShouldBe([agent]);
    }

    // A custom agent belongs to somebody, and the secret alone names nobody: whoever holds it could
    // write any X-Ziggurat-User it liked. Without an assertion the accounts service signed for
    // SexyTime — none at all beside a plain header naming alice, one signed by a key the service
    // does not publish, one signed for another app's route, or the household's guest — the call is
    // signed out, refused before the catalogue is read or written.
    public static TheoryData<string, string, string?> Unbelieved =>
        new[] { "GET", "POST", "DELETE" }.Aggregate(
            new TheoryData<string, string, string?>(), (data, verb) =>
            {
                data.Add(verb, "none", null);
                data.Add(verb, "forged", TestAssertions.Forged(Alice, AppChannels.SexyTime));
                data.Add(verb, "for the chat hub", TestAssertions.Of(Alice, ChannelIds.SignalR));
                data.Add(verb, "the guest", TestAssertions.OfGuest(Alice.TenantId, AppChannels.SexyTime));
                return data;
            });

    [Theory]
    [MemberData(nameof(Unbelieved))]
    public async Task AVerbWithTheSecretButNoBelievableAssertion_IsUnauthorizedAndTouchesNothing(
        string verb, string _, string? assertion)
    {
        var provider = new Mock<IAgentDefinitionProvider>(MockBehavior.Strict);
        await using var app = await StartAsync(provider.Object, Secret);
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Request(verb, "Bearer " + Secret, user: "alice", assertion: assertion));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        provider.VerifyNoOtherCalls();
    }

    // Whose catalogue it is was signed; a plain header naming somebody else changes nothing.
    [Fact]
    public async Task AnAssertionNamingAlice_UnderAHeaderNamingMallory_ListsAlicesCatalogue()
    {
        var provider = new Mock<IAgentDefinitionProvider>(MockBehavior.Strict);
        provider.Setup(p => p.GetAll("alice")).Returns([_registered]);
        await using var app = await StartAsync(provider.Object, Secret);
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Request(
            "GET", "Bearer " + Secret, user: "mallory", assertion: TestAssertions.Of(Alice, AppChannels.SexyTime)));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<AgentCatalogEntry[]>())
            .ShouldBe([new AgentCatalogEntry("custom-1", "Helper", "Helps")]);
    }

    // A page reaches the API with a visitor's credentials, so only an origin the agent's own
    // settings list may read the answer — under the section the compose file sets.
    [Theory]
    [InlineData("https://assistants.example.com", true)]
    [InlineData("https://evil.example.com", false)]
    public async Task APreflight_IsAdmittedOnlyFromAnOriginTheSettingsList(string origin, bool admitted)
    {
        var settings = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = "https://assistants.example.com"
            })
            .Build()
            .Get<AgentSettings>()!;
        await using var app = await StartAsync(new Mock<IAgentDefinitionProvider>().Object, Secret, settings.Cors);
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Preflight(origin));

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBe(admitted);
        response.Headers.Contains("Access-Control-Allow-Credentials").ShouldBe(admitted);
    }

    private static async Task<WebApplication> StartAsync(
        IAgentDefinitionProvider provider, string secret, CorsSettings? cors = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(provider);
        builder.Services.AddSingleton(TestAssertions.Verifier());
        builder.Services.AddDeploymentCors(cors ?? new CorsSettings());

        var app = builder.Build();
        app.UseCors();
        app.MapAgents(secret);
        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/agents");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        return request;
    }

    private static async Task<AgentCatalogEntry[]> ListAsync(HttpClient client, string user)
    {
        using var response = await client.SendAsync(Request("GET", "Bearer " + Secret, user: user));
        return (await response.Content.ReadFromJsonAsync<AgentCatalogEntry[]>())!;
    }

    // Every request also names somebody else in the query, the parameter the API once read. A user
    // is sent as SexyTime's server forwards them: the plain header and, unless a test chooses its
    // own, the assertion the session check signed for SexyTime.
    private static HttpRequestMessage Request(
        string verb, string? authorization, string? user = null, string agentId = "custom-1",
        string? assertion = UnsetAssertion)
    {
        var request = verb switch
        {
            "GET" => new HttpRequestMessage(HttpMethod.Get, "/api/agents?userId=mallory"),
            "POST" => new HttpRequestMessage(HttpMethod.Post, "/api/agents?userId=mallory")
            {
                Content = JsonContent.Create(_registration)
            },
            "DELETE" => new HttpRequestMessage(HttpMethod.Delete, $"/api/agents/{agentId}?userId=mallory"),
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, null)
        };

        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        if (user is not null)
        {
            request.Headers.Add(ForwardedAccount.UserHeader, user);
        }

        assertion = assertion == UnsetAssertion
            ? user is null ? null : TestAssertions.Of(new Account(Home, user), AppChannels.SexyTime)
            : assertion;
        if (assertion is not null)
        {
            request.Headers.Add(ForwardedAccount.AssertionHeader, assertion);
        }

        return request;
    }
}