using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Services;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State;
using WebChat.Client.State.AgentActivity;
using WebChat.Client.State.AgentSettings;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Composer;
using WebChat.Client.State.Connection;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Hub;
using WebChat.Client.State.Messages;
using WebChat.Client.State.Pipeline;
using WebChat.Client.State.Space;
using WebChat.Client.State.Streaming;
using WebChat.Client.State.Toast;
using WebChat.Client.State.Topics;
using WebChat.Client.State.UserIdentity;

namespace Tests.Unit.WebChat.Client.Fixtures;

// The whole client over one seam. The transport is scripted; the live connection, the six
// calling services, the effects, the dispatcher and the stores are the real ones, wired by
// the same registration extensions Program.cs uses. That is what makes a wiring defect —
// a service that drops not live on the floor instead of passing it up — fail a test here.
public sealed class ScriptedChatClient : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;

    public ScriptedChatClient()
    {
        _provider = BuildRegistrations().BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        _scope = _provider.CreateAsyncScope();

        Factory.CreateBehavior = () =>
        {
            var connection = new FakeHubConnection();
            ApplyDefaultAnswers(connection);
            return connection;
        };

        // A store registers its reducer in its constructor and an effect its handler, so
        // anything left unresolved silently ignores every dispatch. In the browser the
        // components resolve the stores; here nothing would until an assertion asked for one.
        new[]
        {
            typeof(TopicsStore), typeof(MessagesStore), typeof(StreamingStore), typeof(ConnectionStore),
            typeof(ApprovalStore), typeof(UserIdentityStore), typeof(ToastStore), typeof(AgentSettingsStore),
            typeof(SpaceStore), typeof(AgentActivityStore), typeof(ComposerStore),
            typeof(SessionRecoveryEffect), typeof(ReconnectionEffect), typeof(SendMessageEffect),
            typeof(TopicSelectionEffect), typeof(TopicDeleteEffect), typeof(InitializationEffect),
            typeof(AgentSelectionEffect), typeof(UserIdentityEffect), typeof(SpaceEffect),
            typeof(AgentActivityEffect), typeof(AgentSettingsEffect), typeof(StreamResumeEffect),
            typeof(AttachmentEffect), typeof(TopicRenameEffect)
        }.ToList().ForEach(type => Services.GetRequiredService(type));
    }

    private IServiceProvider Services => _scope.ServiceProvider;

    public FakeHubConnectionFactory Factory { get; } = new();

    public FakeConfigService ConfigService { get; } = new FakeConfigService().WithSpace("default");

    public FakeLocalStorageService LocalStorage { get; } = new();

    public IChatLiveConnection LiveConnection => Services.GetRequiredService<IChatLiveConnection>();

    public IDispatcher Dispatcher => Services.GetRequiredService<IDispatcher>();

    public TopicsStore Topics => Services.GetRequiredService<TopicsStore>();

    public MessagesStore Messages => Services.GetRequiredService<MessagesStore>();

    public StreamingStore Streaming => Services.GetRequiredService<StreamingStore>();

    public ToastStore Toasts => Services.GetRequiredService<ToastStore>();

    public ApprovalStore Approvals => Services.GetRequiredService<ApprovalStore>();

    public ConnectionStore Connection => Services.GetRequiredService<ConnectionStore>();

    public SpaceStore Space => Services.GetRequiredService<SpaceStore>();

    public ComposerStore Composer => Services.GetRequiredService<ComposerStore>();

    public FakeAttachmentUploader Uploader { get; } = new();

    public UserIdentityStore UserIdentity => Services.GetRequiredService<UserIdentityStore>();

    public T Service<T>() where T : notnull => Services.GetRequiredService<T>();

    // The transport the live connection is holding right now. A rebuild replaces it, so read
    // it again rather than caching one across a reconnect.
    public FakeHubConnection Transport => Factory.Created[^1];

    public async Task<FakeHubConnection> ConnectAsync()
    {
        await LiveConnection.ConnectAsync();
        return Transport;
    }

    // Between connections: the transport is there and cannot carry a call. This is the state
    // the old null guards never covered, so it is the one worth putting tests in.
    public void GoNotLive() => Transport.State = HubConnectionState.Reconnecting;

    public void GoLive() => Transport.State = HubConnectionState.Connected;

    private ServiceCollection BuildRegistrations()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<NavigationManager>(_ => new FakeNavigationManager());
        services.AddScoped(_ => new Mock<IJSRuntime>().Object);

        services.AddScoped<IHubEventDispatcher, HubEventDispatcher>();
        services.AddScoped<ConnectionEventDispatcher>();

        services.AddWebChatLiveConnection();

        services.AddScoped<IChatSessionService, ChatSessionService>();
        services.AddScoped<IChatMessagingService, ChatMessagingService>();
        services.AddScoped<ITopicService, TopicService>();
        services.AddScoped<IAgentService, AgentService>();
        services.AddScoped<IApprovalService, ApprovalService>();
        services.AddScoped<AttachmentEndpointResolver>();
        services.AddScoped<IAttachmentService, AttachmentService>();
        services.AddScoped<ApprovalResponder>();

        services.AddWebChatStores();
        services.AddWebChatEffects();

        services.AddScoped<TopicStreams>();
        services.AddScoped<IStreamingService, StreamingService>();
        services.AddScoped<StreamResumeService>();
        services.AddScoped<IStreamResumeService>(sp => sp.GetRequiredService<StreamResumeService>());
        services.AddScoped<IMessagePipeline, MessagePipeline>();

        // Substituted last so they win: only the browser and the transport are fake.
        services.AddScoped<IHubConnectionFactory>(_ => Factory);
        services.AddScoped<IConfigService>(_ => ConfigService);
        services.AddScoped<ILocalStorageService>(_ => LocalStorage);
        services.AddScoped<IPushSubscriptionService>(_ => new FakePushSubscriptionService());
        services.AddScoped<IAttachmentUploader>(_ => Uploader);

        return services;
    }

    // A call nobody scripted still has to come back with something usable, or a test about
    // one call fails inside an unrelated one. These are the empty-but-live answers.
    private static void ApplyDefaultAnswers(FakeHubConnection connection)
    {
        connection.Answer("GetAllTopics", (IReadOnlyList<TopicMetadata>)[]);
        connection.Answer("GetHistory", (IReadOnlyList<ChatHistoryMessage>)[]);
        connection.Answer("GetAgents", (IReadOnlyList<AgentCatalogEntry>)[]);
        connection.Answer("GetStreamState", (object?)null);
        connection.Answer("GetPendingApprovalForTopic", (object?)null);
        connection.Answer("StartSession", true);
        connection.Answer("GetAttachmentLimits", new AttachmentLimits(
            25L * 1024 * 1024, 10, ["image/png", "image/jpeg", "application/pdf"]));
        connection.Answer("CreateUploadTicket",
            new UploadTicket("ticket-1", DateTimeOffset.UtcNow.AddMinutes(15)));
        connection.Answer("CreateAttachmentDownload",
            (object?[] args) => new AttachmentDownload(
                $"/api/attachments/{args[0]}?ticket=download-1", DateTimeOffset.UtcNow.AddMinutes(15)));
        connection.Answer("EnqueueMessage", false);
        connection.Answer("RespondToApprovalAsync", true);
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
    }
}