using Agent.Modules;
using Agent.Settings;
using Domain.Contracts;
using Infrastructure.Metrics;
using McpChannelSignalR.Modules;
using McpChannelVoice.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Shouldly;
using SignalRSettings = McpChannelSignalR.Settings;
using TelegramSettings = McpChannelTelegram.Settings;
using VoiceSettings = McpChannelVoice.Settings;

namespace Tests.Integration.Metrics;

// One row per metrics-publishing host, driving that host's REAL registration entry point. Hand-
// registering AddMetricsPublishing here instead would stay green against a module that never
// called it, which is exactly the defect this pins: McpChannelVoice registered RedisMetricsPublisher
// straight into IMetricsPublisher, so every publish on the voice path was a live Redis round trip
// and a blip discarded a good transcript (WyomingSatelliteHost) or killed the conversation task.
//
// A caller-facing publisher must always be the buffered one. Nothing else may be resolvable as
// IMetricsPublisher, because a bare sink behind that interface is the whole of the defect.
public class MetricsRegistrationContractTests
{
    // Both hosts register IConnectionMultiplexer as a lazy factory that calls Connect eagerly once
    // resolved, and resolving IMetricsPublisher pulls it in. abortConnect=false hands back a
    // disconnected multiplexer instead of throwing; port 1 is refused instantly and the short
    // timeouts keep the background reconnect loop from lingering.
    private const string UnreachableRedis =
        "127.0.0.1:1,abortConnect=false,connectTimeout=100,connectRetry=1";

    public static TheoryData<string, Action<IServiceCollection>> Hosts => new()
    {
        {
            "agent",
            services => services.AddAgent(new AgentSettings
            {
                OpenRouter = new OpenRouterConfiguration { ApiUrl = "http://openrouter", ApiKey = "x" },
                Redis = new RedisConfiguration { ConnectionString = UnreachableRedis },
                Agents = []
            })
        },
        {
            "mcp-channel-voice",
            services => services.ConfigureVoiceChannel(new VoiceSettings.VoiceSettings
            {
                RedisConnectionString = UnreachableRedis
            })
        },
        // Both chat channels publish transcription metrics for dictation, which is what put them
        // on this table: a host that publishes must hand callers the buffered publisher and must
        // appear on the health roster.
        {
            "mcp-channel-signalr",
            services => services.ConfigureChannel(new SignalRSettings.ChannelSettings
            {
                RedisConnectionString = UnreachableRedis
            })
        },
        {
            "mcp-channel-telegram",
            services => McpChannelTelegram.Modules.ConfigModule.ConfigureChannel(
                services,
                new TelegramSettings.ChannelSettings
                {
                    Bots = [],
                    AllowedUsernames = [],
                    RedisConnectionString = UnreachableRedis
                })
        }
    };

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task HostRegistration_ResolvedMetricsPublisher_IsTheBufferedOne(
        string service, Action<IServiceCollection> configure)
    {
        await using var provider = Build(configure);

        provider.GetRequiredService<IMetricsPublisher>()
            .ShouldBeOfType<BufferedMetricsPublisher>(
                $"{service} must hand callers a fire-and-forget publisher, never a bare sink");
    }

    [Theory]
    [MemberData(nameof(Hosts))]
    public async Task HostRegistration_PublishingHost_IsOnTheHealthRoster(
        string service, Action<IServiceCollection> configure)
    {
        await using var provider = Build(configure);

        provider.GetServices<IHostedService>()
            .OfType<HeartbeatService>()
            .ShouldHaveSingleItem($"{service} publishes metrics, so it must report a heartbeat");
    }

    private static ServiceProvider Build(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // The voice host's MCP hosted services want a lifetime that only a real host builder
        // supplies; enumerating IHostedService constructs them all. Nothing here starts them.
        services.AddSingleton(Mock.Of<IHostApplicationLifetime>());
        configure(services);
        return services.BuildServiceProvider();
    }
}