using Agent.Modules;
using Agent.Settings;
using Domain.Agents;
using Domain.Contracts;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Tests.Unit.Agent;

public class InjectorModuleTests
{
    private const string OpenRouterUrl = "https://openrouter.example/api/v1/";
    private const string TypeSafeUrl = "https://typesafe.example/v1/";

    [Fact]
    public void AddAgent_KeepsAConnectionAliveToEachHostedProvider()
    {
        // Two keep-alives of one class: the host must start both, or the second provider goes
        // cold after every gap and its first call pays the handshake the keep-alive exists to
        // spare. The descriptors are invoked directly rather than resolved as IHostedService,
        // because the rest of the agent's hosted services want a live Redis.
        var keepAlives = KeepAlivesRegisteredBy(Settings(typeSafeKey: "key"));

        keepAlives.Select(k => k.Options.BaseAddress).ShouldBe([OpenRouterUrl, TypeSafeUrl]);
    }

    [Fact]
    public void AddAgent_WithoutATypeSafeKey_KeepsOnlyTheOpenRouterConnectionAlive()
    {
        var keepAlives = KeepAlivesRegisteredBy(Settings(typeSafeKey: ""));

        keepAlives.Select(k => k.Options.BaseAddress).ShouldBe([OpenRouterUrl]);
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

    private static AgentSettings Settings(string typeSafeKey) => new()
    {
        OpenRouter = new OpenRouterConfiguration { ApiUrl = OpenRouterUrl, ApiKey = "key" },
        Redis = new RedisConfiguration { ConnectionString = "localhost:1" },
        Agents = [],
        TypeSafe = new TypeSafeConfiguration { ApiUrl = TypeSafeUrl, ApiKey = typeSafeKey }
    };
}