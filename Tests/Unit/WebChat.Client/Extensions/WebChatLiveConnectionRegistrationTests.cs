using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using Shouldly;
using Tests.Unit.WebChat.Client.Fixtures;
using WebChat.Client.Contracts;
using WebChat.Client.Extensions;
using WebChat.Client.Services;
using WebChat.Client.Services.Streaming;
using WebChat.Client.State.Approval;
using WebChat.Client.State.Effects;
using WebChat.Client.State.Hub;
using WebChat.Client.State.Pipeline;

namespace Tests.Unit.WebChat.Client.Extensions;

// The live connection sits underneath everything that makes a hub call, and session recovery
// reaches back down to it — so the registration is one Lazy away from a container cycle, and
// a cycle here is a blank page rather than a failing test. Faking a collaborator would fake
// away the edge under test, so everything the client registers is real except the browser
// primitives.
public sealed class WebChatLiveConnectionRegistrationTests
{
    [Fact]
    public async Task TheLiveConnection_IsTheSameInstanceSessionRecoveryReachesBackTo()
    {
        await using var provider = CreateProvider();
        await using var scope = provider.CreateAsyncScope();
        var liveConnection = scope.ServiceProvider.GetRequiredService<IChatLiveConnection>();

        scope.ServiceProvider.GetRequiredService<ISessionRecovery>();

        scope.ServiceProvider.GetRequiredService<IChatLiveConnection>().ShouldBeSameAs(liveConnection);
    }

    // A cycle behind a factory registration is invisible to ValidateOnBuild — only actually
    // resolving finds it — and it does not report itself as a cycle, it recurses until the
    // process dies. So resolve everything the client registers, not only the start-up roots.
    [Fact]
    public async Task TheClientRegistrations_ResolveEveryRegisteredService()
    {
        var services = CreateRegistrations();
        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();

        var serviceTypes = services
            .Select(descriptor => descriptor.ServiceType)
            .Where(type => !type.IsGenericTypeDefinition)
            .Distinct()
            .ToList();

        serviceTypes.ShouldContain(typeof(IChatLiveConnection));

        serviceTypes.ForEach(type =>
            Should.NotThrow(
                () => scope.ServiceProvider.GetRequiredService(type),
                $"{type.Name} did not resolve"));
    }

    private static ServiceProvider CreateProvider() =>
        CreateRegistrations().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    // Mirrors Program.cs. Only the browser primitives are substituted.
    private static ServiceCollection CreateRegistrations()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => new HttpClient { BaseAddress = new Uri("https://localhost/") });
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<NavigationManager>(_ => new FakeNavigationManager());
        services.AddScoped(_ => new Mock<IJSRuntime>().Object);
        services.AddScoped<ILocalStorageService>(_ => new FakeLocalStorageService());

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
        services.AddScoped<IAttachmentUploader, HttpAttachmentUploader>();
        services.AddScoped<ApprovalResponder>();

        services.AddWebChatStores();
        services.AddWebChatEffects();

        services.AddScoped<TopicStreams>();
        services.AddScoped<IStreamingService, StreamingService>();
        services.AddScoped<StreamResumeService>();
        services.AddScoped<IStreamResumeService>(sp => sp.GetRequiredService<StreamResumeService>());
        services.AddScoped<IMessagePipeline, MessagePipeline>();

        services.AddScoped<PushNotificationService>();
        services.AddScoped<IPushSubscriptionService>(sp => sp.GetRequiredService<PushNotificationService>());

        return services;
    }
}