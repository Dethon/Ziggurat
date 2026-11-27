using Domain.Contracts;
using Domain.Monitor;
using Domain.Security;
using Domain.Tools.FileSystem.Bridge;
using Infrastructure.Agents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Extensions;

public static class AppReadsExtensions
{
    // A built app's reads over the household agents' read registries (AgentReadRegistries, registered
    // beside the agent factory), cached per reader and disposed with the host. A check's reads are
    // believed by the CheckCallVerifier the host registers over its own signing keys.
    public static IServiceCollection AddAppReads(
        this IServiceCollection services, VfsBridgeSettings bridge, AppReadSettings settings)
    {
        services.TryAddSingleton(TimeProvider.System);
        return services
            .AddSingleton(settings)
            .AddSingleton(sp => new ReadRegistryCache(
                sp.GetRequiredService<AgentReadRegistries>(), sp.GetRequiredService<TimeProvider>(), settings))
            .AddSingleton(sp => new AppReader(
                sp.GetRequiredService<AssertionVerifier>(),
                sp.GetRequiredService<CheckCallVerifier>(),
                new HouseholdStanding(
                    sp.GetService<IAgentDefinitionProvider>(), sp.GetService<IAccountStanding>(),
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger<AppReader>()),
                sp.GetRequiredService<ReadRegistryCache>(),
                bridge,
                sp.GetRequiredService<ILogger<AppReader>>()));
    }
}