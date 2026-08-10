using Domain.Agents;
using Domain.Contracts;
using Infrastructure.Clients.Push;
using Infrastructure.Clients.Transcription;
using Infrastructure.Conversations;
using Infrastructure.Metrics;
using Infrastructure.StateManagers;
using Mcp.Hosting;
using McpChannelSignalR.Attachments;
using McpChannelSignalR.McpTools;
using McpChannelSignalR.Services;
using McpChannelSignalR.Settings;
using StackExchange.Redis;

namespace McpChannelSignalR.Modules;

public static class ConfigModule
{
    public static IServiceCollection ConfigureChannel(this IServiceCollection services, ChannelSettings settings)
    {
        var redisMultiplexer = ConnectionMultiplexer.Connect(settings.RedisConnectionString);

        // The shared Lemonade client is named the same in every server that transcribes, so a
        // dictation always goes out on a registration that exists.
        services.AddHttpClient(LemonadeTranscriptionClient.ClientName);

        services
            .AddSingleton<IConnectionMultiplexer>(redisMultiplexer)
            .AddSingleton<MutableAgentCatalog>()
            .AddSingleton<IAgentCatalog>(sp => sp.GetRequiredService<MutableAgentCatalog>())
            .AddSingleton<IMutableAgentCatalog>(sp => sp.GetRequiredService<MutableAgentCatalog>())
            .AddSingleton<RedisStateService>()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IThreadStateStore>(sp =>
                new RedisThreadStateStore(sp.GetRequiredService<IConnectionMultiplexer>(), TimeSpan.FromDays(30)))
            .AddSingleton<IConversationFactory, ConversationFactory>()
            .AddSingleton<StreamService>()
            .AddSingleton<IStreamService>(sp => sp.GetRequiredService<StreamService>())
            .AddSingleton<SessionService>()
            .AddSingleton<ISessionService>(sp => sp.GetRequiredService<SessionService>())
            .AddSingleton<ApprovalService>()
            .AddSingleton<IApprovalService>(sp => sp.GetRequiredService<ApprovalService>())
            .AddSingleton<IHubNotificationSender, SignalRHubNotificationSender>()
            .AddSingleton<IPushSubscriptionStore, RedisPushSubscriptionStore>()
            .AddSingleton(settings.Attachments)
            .AddMetricsPublishing("mcp-channel-signalr")
            .AddSingleton(settings.Dictation)
            .AddSingleton<IAudioTranscriber>(sp => new LemonadeTranscriptionClient(
                sp.GetRequiredService<IHttpClientFactory>(),
                settings.Dictation.Transcription,
                sp.GetRequiredService<ILogger<LemonadeTranscriptionClient>>()))
            .AddSingleton<AttachmentTickets>()
            .AddSingleton<AttachmentStore>()
            .AddSingleton<AttachmentService>()
            .AddHostedService<AttachmentSweeper>();

        if (settings.WebPush?.IsConfigured == true)
        {
            var webPush = settings.WebPush;
            services
                .AddHttpClient()
                .AddSingleton<IPushMessageSender>(sp =>
                    new ModernWebPushSender(
                        sp.GetRequiredService<IHttpClientFactory>().CreateClient("WebPush"),
                        webPush.PublicKey!,
                        webPush.PrivateKey!,
                        webPush.Subject!))
                .AddSingleton<IPushNotificationService, WebPushNotificationService>();
        }
        else
        {
            services.AddSingleton<IPushNotificationService, NullPushNotificationService>();
        }

        services.AddChatSignalR();

        services
            .AddMcpHost(settings)
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>()
            .WithTools<CreateConversationTool>()
            .WithTools<RegisterAgentsTool>()
            .WithTools<FetchAttachmentTool>()
            // Broadcast: a subscriber that is idle but not yet pruned still receives, so a brief
            // agent gap does not lose a message the browser has already been told was sent.
            .AddChannelServer(DeliveryPolicy.Broadcast);

        return services;
    }

    // How long a browser may go silent before the server calls it gone. The default thirty seconds
    // is shorter than a person picking a photo on a phone: the picker holds the page down, a frozen
    // page sends no keepalive, and the connection dies mid-pick. Two minutes is the browser's own
    // tolerance for a silent server (six minutes, keepalive every ten seconds) read from this side,
    // and costs nothing to hold — this hub keeps no per-connection state to release.
    private static readonly TimeSpan _frozenPageTolerance = TimeSpan.FromMinutes(2);

    internal static IServiceCollection AddChatSignalR(this IServiceCollection services) =>
        services.AddSignalR(options => options.ClientTimeoutInterval = _frozenPageTolerance).Services;
}