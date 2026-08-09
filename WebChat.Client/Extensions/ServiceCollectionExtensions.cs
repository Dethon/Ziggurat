using WebChat.Client.Contracts;
using WebChat.Client.Services;
using WebChat.Client.State;
using WebChat.Client.State.AgentActivity;
using WebChat.Client.State.AgentSettings;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Hub;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Space;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace WebChat.Client.Extensions;

public static class ServiceCollectionExtensions
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddWebChatStores()
        {
            services.AddScoped<Dispatcher>();
            services.AddScoped<IDispatcher>(sp => sp.GetRequiredService<Dispatcher>());

            services.AddScoped<TopicsStore>();
            services.AddScoped<MessagesStore>();
            services.AddScoped<StreamingStore>();
            services.AddScoped<ConnectionStore>();
            services.AddScoped<ApprovalStore>();
            services.AddScoped<UserIdentityStore>();
            services.AddScoped<ToastStore>();
            services.AddScoped<AgentSettingsStore>();
            services.AddScoped<SpaceStore>();
            services.AddScoped<AgentActivityStore>();
            services.AddScoped<ComposerStore>();

            services.AddScoped<RenderCoordinator>();

            services.AddScoped<ConfigService>();
            services.AddScoped<IConfigService>(sp => sp.GetRequiredService<ConfigService>());

            return services;
        }

        public IServiceCollection AddWebChatLiveConnection()
        {
            services.AddScoped<IHubConnectionFactory, SignalRHubConnectionFactory>();
            services.AddScoped<IHubEventBinder, HubEventBinder>();
            services.AddScoped<ISessionRecovery, SessionRecovery>();

            services.AddScoped<IChatLiveConnection, ChatLiveConnection>();

            return services;
        }

        public IServiceCollection AddWebChatEffects()
        {
            // Registration order sequences nothing here, and nothing needs it to. Effects are
            // constructed in the order Program.cs resolves them, each subscribes to the epoch
            // on its own, and recovery's own work is detached — so recovery and catch-up
            // overlap by design. No hub method catch-up calls requires the client to have
            // registered first: only SendMessage, EnqueueMessage and the push verbs do, and
            // those are user actions, not catch-up.
            services.AddScoped<SessionRecoveryEffect>();
            services.AddScoped<ReconnectionEffect>();
            services.AddScoped<SendMessageEffect>();
            services.AddScoped<TopicSelectionEffect>();
            services.AddScoped<TopicDeleteEffect>();
            services.AddScoped<InitializationEffect>();
            services.AddScoped<AgentSelectionEffect>();
            services.AddScoped<UserIdentityEffect>();
            services.AddScoped<SpaceEffect>();
            services.AddScoped<AgentActivityEffect>();
            services.AddScoped<AgentSettingsEffect>();
            services.AddScoped<StreamResumeEffect>();
            services.AddScoped<AttachmentEffect>();

            return services;
        }
    }
}