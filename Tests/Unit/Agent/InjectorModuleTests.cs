using System.Text.Json.Nodes;
using Agent.App;
using Agent.Modules;
using Agent.Settings;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Judgments;
using Domain.Monitor;
using Domain.Security;
using Infrastructure.Agents;
using Infrastructure.Agents.ChatClients;
using Infrastructure.Tunnels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Tests.Unit.Agent;

public class InjectorModuleTests
{
    private const string OpenRouterUrl = "https://openrouter.example/api/v1/";

    [Fact]
    public void AddAgent_KeepsOneConnectionAlive_BecauseJevRidesTheOpenRouterOne()
    {
        // Jev is asked through OpenRouter, on the pool the chat clients share, so the keep-alive
        // that holds their connection open holds the judge's too and a second one would ping the
        // same host twice. The descriptors are invoked directly rather than resolved as
        // IHostedService, because the rest of the agent's hosted services want a live Redis.
        var keepAlives = KeepAlivesRegisteredBy(Settings(openRouterKey: "key"));

        keepAlives.Select(k => k.Options.BaseAddress).ShouldBe([OpenRouterUrl]);
    }

    [Fact]
    public async Task AddAgent_WithAnOpenRouterKey_AsksJevOnIt()
    {
        var judge = JudgeRegisteredBy(Settings(openRouterKey: "key"));

        // A local turn is refused by the configured client alone; the keyless one says
        // unconfigured before it looks at the turn.
        (await judge.JudgeAsync(ALocalTurn(), CancellationToken.None))
            .ShouldBeOfType<JudgmentOutcome.Absent>().Reason.ShouldBe(AbsenceReason.LocalTurn);
    }

    [Fact]
    public async Task AddAgent_WithoutAnOpenRouterKey_AnswersUnconfigured()
    {
        var judge = JudgeRegisteredBy(Settings(openRouterKey: ""));

        (await judge.JudgeAsync(ALocalTurn(), CancellationToken.None))
            .ShouldBeOfType<JudgmentOutcome.Absent>().Reason.ShouldBe(AbsenceReason.Unconfigured);
    }

    // A server the deployment configured but gave no credential would be dialled bare and refused
    // on every call; the agent says so once, at startup, naming it.
    [Fact]
    public void AddAgent_WhenAConfiguredServerHasNoCredential_Refuses()
    {
        var settings = Settings(openRouterKey: "key") with
        {
            Agents = [new AgentDefinition
            {
                Id = "jonas", Name = "Jonas", Model = "m", McpServerEndpoints = ["http://mcp-vault:8080/mcp"]
            }],
            ChannelEndpoints = [new ChannelEndpoint { ChannelId = "signalr", Endpoint = "http://mcp-channel-signalr:8080/mcp" }],
            Mcp = new McpGateSettings { Secrets = new() { ["mcp-vault"] = "vault-secret" } }
        };

        Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddLogging().AddAgent(settings))
            .Message.ShouldContain("mcp-channel-signalr");
    }

    // An endpoint is a server's URL or a household server's name. Anything else — a scheme no server
    // answers on, a name that is no DNS label — would be skipped by the credential check above and
    // fail far from here, in the first session that dialled it.
    [Theory]
    [InlineData("mcp-vault:8080/mcp")]
    [InlineData("tunnel://vault")]
    [InlineData("Vault Server")]
    public void AddAgent_WhenAnEndpointIsNeitherAUrlNorAServersName_Refuses(string endpoint)
    {
        var settings = Settings(openRouterKey: "key") with
        {
            SubAgents = [new SubAgentDefinition { Id = "worker", Name = "Worker", McpServerEndpoints = ["vault", endpoint] }]
        };

        Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddLogging().AddAgent(settings))
            .Message.ShouldContain(endpoint);
    }

    // A channel endpoint is read the same way: one that is neither would be a connection retrying a
    // dial that can never work for as long as the agent runs.
    [Fact]
    public void AddAgent_WhenAChannelEndpointIsNeitherAUrlNorAServersName_Refuses()
    {
        var settings = Settings(openRouterKey: "key") with
        {
            ChannelEndpoints = [new ChannelEndpoint { ChannelId = "homeassistant", Endpoint = "Home Assistant" }]
        };

        Should.Throw<InvalidOperationException>(() => new ServiceCollection().AddLogging().AddAgent(settings))
            .Message.ShouldContain("Home Assistant");
    }

    [Fact]
    public void AddAgent_TakesAChannelEndpointNamingAHouseholdServer_WithNoCredential()
    {
        var settings = Settings(openRouterKey: "key") with
        {
            ChannelEndpoints = [new ChannelEndpoint { ChannelId = "homeassistant", Endpoint = "homeassistant" }]
        };

        Should.NotThrow(() => new ServiceCollection().AddLogging().AddAgent(settings));
    }

    [Fact]
    public void AddAgent_TakesAHouseholdServersName_WithNoCredential()
    {
        var settings = Settings(openRouterKey: "key") with
        {
            Agents = [new AgentDefinition { Id = "jonas", Name = "Jonas", Model = "m", McpServerEndpoints = ["vault"] }]
        };

        Should.NotThrow(() => new ServiceCollection().AddLogging().AddAgent(settings));
    }

    // The deployment's own composition: the relay a household's server opens its tunnel to, the
    // revocations that close one, and the agent reaching household servers through it rather than
    // through whatever a test stack hosts.
    [Fact]
    public void ConfigureAgents_ComposesTheRelay_AndReachesHouseholdServersThroughIt()
    {
        var settings = Settings(openRouterKey: "key") with { Signing = TestSigningKeys.Settings(TestSigningKeys.Agent) };
        var services = new ServiceCollection().AddLogging();
        services.ConfigureAgents(settings, new CommandLineParams(), new ConfigurationBuilder().Build());
        services.RemoveAll<IMetricsPublisher>();
        services.AddSingleton<IMetricsPublisher>(new RecordingMetricsPublisher());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<TunnelRelay>().ShouldNotBeNull();
        provider.GetRequiredService<IOwnServers>().ShouldBeOfType<RelayedOwnServers>();
        provider.GetRequiredService<IOwnChannelServers>().ShouldBeOfType<RelayedOwnServers>();
        services.ShouldContain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(TunnelRevocationListener));
    }

    // A timer with no satellite is rung on its person's channels by the agent, which alone holds
    // them: the deployment's composition runs one alert relay over its channel connections.
    [Fact]
    public void ConfigureAgents_RingsAlertsOverItsChannelConnections()
    {
        var settings = Settings(openRouterKey: "key") with { Signing = TestSigningKeys.Settings(TestSigningKeys.Agent) };
        var services = new ServiceCollection().AddLogging();
        services.ConfigureAgents(settings, new CommandLineParams(), new ConfigurationBuilder().Build());
        services.RemoveAll<IMetricsPublisher>();
        services.AddSingleton<IMetricsPublisher>(new RecordingMetricsPublisher());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<AlertRelay>().ShouldNotBeNull();
        services.ShouldContain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AlertRinging));
    }

    // An outpost is one of the household's tunnels, so the deployment's outposts are the relay's.
    [Fact]
    public void ConfigureAgents_OffersOutpostsFromTheRelaysTunnels()
    {
        var settings = Settings(openRouterKey: "key") with { Signing = TestSigningKeys.Settings(TestSigningKeys.Agent) };
        var services = new ServiceCollection().AddLogging();
        services.ConfigureAgents(settings, new CommandLineParams(), new ConfigurationBuilder().Build());
        services.RemoveAll<IMetricsPublisher>();
        services.AddSingleton<IMetricsPublisher>(new RecordingMetricsPublisher());
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOutposts>().ShouldBeOfType<TunnelledOutposts>()
            .ShouldBeSameAs(provider.GetRequiredService<TunnelledOutposts>());
    }

    // One set of credentials for the host: the one startup checked is the one every agent, worker
    // and channel connection is built with.
    [Fact]
    public void AddAgent_RegistersTheCredentialsItChecked()
    {
        var settings = Settings(openRouterKey: "key") with
        {
            Mcp = new McpGateSettings { Secrets = new() { ["mcp-vault"] = "vault-secret" } }
        };

        using var provider = new ServiceCollection().AddLogging().AddAgent(settings).BuildServiceProvider();

        provider.GetRequiredService<McpCredentials>().For(ServerAddress.FromUrl("http://mcp-vault:8080/mcp")!).ShouldBe("vault-secret");
    }

    private static JudgmentRequest ALocalTurn() =>
        new(new JsonObject(), new Dictionary<string, JudgmentQuestion>(), new JudgmentCaller("lemonade/qwen3", Sender: null));

    private static IJudge JudgeRegisteredBy(AgentSettings settings)
    {
        // The judge as the agent's own container builds it: every collaborator is the registered
        // one but the metrics publisher, whose real one is a Redis connection.
        var services = new ServiceCollection().AddLogging().AddAgent(settings);
        services.RemoveAll<IMetricsPublisher>();
        services.AddSingleton<IMetricsPublisher>(new RecordingMetricsPublisher());
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IJudge>();
    }

    private static List<HostedConnectionKeepAlive> KeepAlivesRegisteredBy(AgentSettings settings)
    {
        var services = new ServiceCollection().AddLogging().AddAgent(settings);
        services.RemoveAll<IMetricsPublisher>();
        services.AddSingleton<IMetricsPublisher>(new RecordingMetricsPublisher());
        using var provider = services.BuildServiceProvider();

        return services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationFactory as Func<IServiceProvider, HostedConnectionKeepAlive>)
            .OfType<Func<IServiceProvider, HostedConnectionKeepAlive>>()
            .Select(factory => factory(provider))
            .ToList();
    }

    private static AgentSettings Settings(string openRouterKey) => new()
    {
        OpenRouter = new OpenRouterConfiguration { ApiUrl = OpenRouterUrl, ApiKey = openRouterKey },
        Redis = new RedisConfiguration { ConnectionString = "localhost:1" },
        BackCalls = new() { LemonadeUrl = "http://lemonade:13305" },
        Agents = []
    };
}