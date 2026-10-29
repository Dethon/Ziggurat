using System.Net;
using System.Net.Http.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Security;
using global::Agent.App;
using global::Agent.Settings;
using Infrastructure.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    [Fact]
    public async Task Get_WithTheSecret_ListsTheCatalogueForTheUser()
    {
        var provider = new Mock<IAgentDefinitionProvider>();
        provider.Setup(p => p.GetAll("alice")).Returns([_registered]);
        await using var app = await StartAsync(provider.Object, Secret);
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Request("GET", "Bearer " + Secret));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var catalogue = await response.Content.ReadFromJsonAsync<AgentCatalogEntry[]>();
        catalogue.ShouldBe([new AgentCatalogEntry("custom-1", "Helper", "Helps")]);
    }

    [Fact]
    public async Task Post_WithTheSecret_RegistersTheAgentForTheUser()
    {
        var provider = new Mock<IAgentDefinitionProvider>();
        provider.Setup(p => p.RegisterCustomAgent("alice", It.IsAny<CustomAgentRegistration>()))
            .Returns(_registered);
        await using var app = await StartAsync(provider.Object, Secret);
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Request("POST", "Bearer " + Secret));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<AgentCatalogEntry>())
            .ShouldBe(new AgentCatalogEntry("custom-1", "Helper", "Helps"));
        provider.Verify(p => p.RegisterCustomAgent(
            "alice", It.Is<CustomAgentRegistration>(r => r.Name == "Helper")), Times.Once);
    }

    [Fact]
    public async Task Delete_WithTheSecret_UnregistersTheUsersAgent()
    {
        var provider = new Mock<IAgentDefinitionProvider>();
        provider.Setup(p => p.UnregisterCustomAgent("alice", "custom-1")).Returns(true);
        await using var app = await StartAsync(provider.Object, Secret);
        using var client = app.GetTestClient();

        using var response = await client.SendAsync(Request("DELETE", "Bearer " + Secret));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<bool>()).ShouldBeTrue();
        provider.Verify(p => p.UnregisterCustomAgent("alice", "custom-1"), Times.Once);
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
        builder.Services.AddDeploymentCors(cors ?? new CorsSettings());

        var app = builder.Build();
        app.UseCors();
        app.MapAgents(secret);
        await app.StartAsync();
        return app;
    }

    private static HttpRequestMessage Preflight(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/agents?userId=alice");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        return request;
    }

    private static HttpRequestMessage Request(string verb, string? authorization)
    {
        var request = verb switch
        {
            "GET" => new HttpRequestMessage(HttpMethod.Get, "/api/agents?userId=alice"),
            "POST" => new HttpRequestMessage(HttpMethod.Post, "/api/agents?userId=alice")
            {
                Content = JsonContent.Create(_registration)
            },
            "DELETE" => new HttpRequestMessage(HttpMethod.Delete, "/api/agents/custom-1?userId=alice"),
            _ => throw new ArgumentOutOfRangeException(nameof(verb), verb, null)
        };

        if (authorization is not null)
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return request;
    }
}